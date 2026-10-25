#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using T3.Core.Model;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Window;
using T3.Editor.Show;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Collab;

/// <summary>
/// Owns the collaboration session state machine and is the single entry point for the rest of the
/// editor. Everything mutating editor state (applying remote ops, updating peer presence) happens in
/// <see cref="Update"/> on the UI thread; network threads only produce queued inbound messages.
///
/// Topology: host-authoritative star. Clients send ops to the host; the host applies every op to its
/// own graph and relays it. Clients apply only host-relayed ops, so all participants converge in
/// send order without clocks or vector versions. Presence (cursors/selection) is fire-and-forget
/// display data on its own channel and never influences the graph.
/// </summary>
internal sealed class CollabManager
{
    public static CollabManager Instance { get; } = new();

    public enum SessionState
    {
        Off,
        Hosting,
        Connecting,
        Connected,
    }

    public SessionState State { get; private set; } = SessionState.Off;
    public string StatusText { get; private set; } = "Collaboration is off";
    public string ErrorText { get; private set; } = "";

    /// <summary>Room code when hosting through a relay; empty for direct sessions.</summary>
    public string RoomCode { get; private set; } = "";

    public bool IsSessionActive => State is SessionState.Hosting or SessionState.Connected;

    public CollabUserIdentity LocalIdentity => _localIdentity;

    /// <summary>Peers currently in the session (UI thread only).</summary>
    public IReadOnlyList<CollabPeerConnection> Peers => _transport?.Peers ?? [];

    /// <summary>The link back to the session host on client transports, direct or relay.</summary>
    private CollabPeerConnection? HostLink => _transport?.HostLink;

    public IReadOnlyList<DiscoveredSession> DiscoveredSessions => _discovered;

    private CollabUserIdentity _localIdentity = new();

    private CollabTransport? _transport;
    private LanDiscoveryScanner? _discoveryScanner;
    private readonly List<DiscoveredSession> _discovered = [];

    /// <summary>Ops applied or received since session start; replayed to late joiners.</summary>
    private readonly List<JObject> _sessionLog = [];
    private const int MaxSessionLogEntries = 10_000;

    private long _nextHeartbeatTicks;
    private long _nextPresenceSendTicks;
    private long _nextRateResetTicks;

    private string _password = "";

    /// <summary>Inbound op counter per peer for rate limiting, keyed by peer id. UI thread only.</summary>
    private readonly Dictionary<int, int> _opsReceivedThisSecond = new();

    private PresencePacket _localPresence = new();

    /// <summary>
    /// Completions of background connects, applied on the UI thread so a Leave() that races them
    /// wins instead of being overwritten by a session nobody asked for.
    /// </summary>
    private readonly ConcurrentQueue<Action> _uiThreadActions = new();

    /// <summary>Bumped by every session start and Leave; stale asynchronous completions check it.</summary>
    private volatile int _sessionGeneration;

    // ---------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------

    private CollabManager()
    {
        RefreshLocalIdentity();
    }

    /// <summary>Rebuilds the local identity from user settings (after a Discord login or name change).</summary>
    public void RefreshLocalIdentity()
    {
        var config = UserSettings.Config;
        var userIdText = config.CollabUserId;
        if (!Guid.TryParse(userIdText, out var userId) || userId == Guid.Empty)
        {
            userId = Guid.NewGuid();
            config.CollabUserId = userId.ToString();
            UserSettings.Save();
        }

        var displayName = !string.IsNullOrWhiteSpace(config.CollabDisplayName)
                              ? config.CollabDisplayName
                              : (string.IsNullOrWhiteSpace(config.CollabDiscordUserName)
                                     ? Environment.UserName
                                     : config.CollabDiscordUserName);

        _localIdentity = new CollabUserIdentity
                             {
                                 UserId = userId,
                                 DisplayName = TruncateName(displayName),
                                 Source = string.IsNullOrEmpty(config.CollabDiscordUserId) ? "local" : "discord",
                                 AvatarUrl = string.IsNullOrEmpty(config.CollabDiscordUserId)
                                                 ? ""
                                                 : $"https://cdn.discordapp.com/avatars/{config.CollabDiscordUserId}/{config.CollabDiscordUserAvatar}.png",
                             };
    }

    private static string TruncateName(string name)
    {
        name = name?.Trim() ?? "";
        return name.Length <= 48 ? name : name[..48];
    }

    public void StartHost(int tcpPort, string password, bool enableLanDiscovery, string? projectName = null,
                          string? interfaceName = null)
    {
        if (IsSessionActive)
            Leave();

        ++_sessionGeneration;
        ErrorText = "";

        // A named project that isn't focused is opened first: the session snapshots and streams the
        // focused project, so selecting one in the settings has to make it the one being edited.
        if (!TryFocusProject(projectName))
        {
            ErrorText = $"Can't share '{projectName}': the project could not be opened";
            StatusText = "Failed to host";
            State = SessionState.Off;
            _sessionProjectName = "";
            return;
        }

        try
        {
            _password = password ?? "";
            _sessionLog.Clear();
            var hostName = TruncateName(_localIdentity.DisplayName);
            _sessionProjectName = projectName
                                  ?? ProjectView.Focused?.OpenedProject.Package.DisplayName
                                  ?? "";
            _transport = new CollabHostTransport(tcpPort,
                                                 enableLanDiscovery,
                                                 () => new JObject
                                                           {
                                                               ["collab"] = true,
                                                               ["name"] = hostName,
                                                               ["host"] = Environment.MachineName,
                                                               ["port"] = tcpPort,
                                                               ["pwd"] = !string.IsNullOrEmpty(_password),
                                                               ["project"] = _sessionProjectName,
                                                           },
                                                 interfaceName ?? UserSettings.Config.NetworkInterface);
            _transport.PeerDisconnected += OnPeerDisconnectedTransport;
            State = SessionState.Hosting;
            StatusText = $"Hosting on port {tcpPort}";
            Log.Info($"Collab: session started (host, port {tcpPort}, {(string.IsNullOrEmpty(_password) ? "open" : "password protected")})");
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not open port {tcpPort}: {e.Message}";
            StatusText = "Failed to host";
            _transport = null;
            State = SessionState.Off;
            _sessionProjectName = "";
        }
    }

    /// <summary>
    /// Makes the named project the focused one. Empty is a no-op (the focused project is shared);
    /// false means the project could not be opened, and hosting should not start without it.
    /// </summary>
    private static bool TryFocusProject(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return true;

        var focused = ProjectView.Focused;
        if (focused != null && MatchesProjectName(focused.OpenedProject.Package.DisplayName, projectName))
            return true;

        return TryOpenProjectByName(projectName);
    }


    /// <summary>
    /// Hosts through a relay so participants behind NATs can join by room code without port
    /// forwarding. Runs async: state goes Connecting until the relay confirms the room.
    /// </summary>
    public void StartHostViaRelay(string relayHost, int relayPort, string? projectName = null)
    {
        if (IsSessionActive)
            Leave();

        ErrorText = "";

        if (!TryFocusProject(projectName))
        {
            ErrorText = $"Can't share '{projectName}': the project could not be opened";
            StatusText = "Failed to host";
            State = SessionState.Off;
            _sessionProjectName = "";
            return;
        }

        _sessionLog.Clear();
        _sessionProjectName = projectName
                              ?? ProjectView.Focused?.OpenedProject.Package.DisplayName
                              ?? "";

        State = SessionState.Connecting;
        StatusText = $"Registering room at {relayHost}:{relayPort}...";

        var generation = ++_sessionGeneration;
        var password = UserSettings.Config.CollabPassword ?? "";
        _ = Task.Run(async () =>
                {
                    try
                    {
                        var transport = await CollabRelayHostTransport.ConnectAsync(relayHost, relayPort, "", CancellationToken.None);
                        _uiThreadActions.Enqueue(() => CompleteRelayHostConnect(transport, generation, password, relayHost, relayPort));
                    }
                    catch (Exception e)
                    {
                        ReportRelayFailure(generation, "Relay hosting failed", "Failed to host", e);
                    }
                });
    }

    /// <summary>
    /// UI thread: adopts a relay host link. The connect runs on a pool thread, so the user may have
    /// left (or started another session) while it was in flight; a stale completion is discarded
    /// and its room registration released instead of resurrecting the session.
    /// </summary>
    private void CompleteRelayHostConnect(CollabRelayHostTransport transport, int generation, string password, string relayHost, int relayPort)
    {
        if (generation != _sessionGeneration)
        {
            transport.Dispose();
            return;
        }

        _password = password;
        _sessionLog.Clear();
        transport.PeerDisconnected += OnPeerDisconnectedTransport;
        transport.StartReading();
        _transport = transport;
        RoomCode = transport.AssignedRoomCode;
        State = SessionState.Hosting;
        StatusText = $"Hosting via relay, room {RoomCode}";
        Log.Info($"Collab: hosting via relay {relayHost}:{relayPort}, room {RoomCode}");
    }

    /// <summary>Joins a relay-hosted room by code.</summary>
    public void JoinViaRelay(string relayHost, int relayPort, string roomCode, string password)
    {
        if (IsSessionActive)
            Leave();

        if (string.IsNullOrWhiteSpace(roomCode))
        {
            ErrorText = "Enter the room code shared by the host";
            return;
        }

        ErrorText = "";
        State = SessionState.Connecting;
        StatusText = $"Joining room {roomCode} via {relayHost}:{relayPort}...";

        var generation = ++_sessionGeneration;
        var passwordToUse = password ?? "";
        _ = Task.Run(async () =>
                {
                    try
                    {
                        var transport = await CollabRelayClientTransport.ConnectAsync(relayHost, relayPort, roomCode, CancellationToken.None);
                        _uiThreadActions.Enqueue(() => CompleteRelayClientConnect(transport, generation, passwordToUse));
                    }
                    catch (Exception e)
                    {
                        ReportRelayFailure(generation, "Relay join failed", "Connection failed", e);
                    }
                });
    }

    /// <summary>UI thread: adopts a joined relay link, unless that join was abandoned meanwhile.</summary>
    private void CompleteRelayClientConnect(CollabRelayClientTransport transport, int generation, string password)
    {
        if (generation != _sessionGeneration)
        {
            transport.Dispose();
            return;
        }

        _password = password;
        _sessionLog.Clear();
        _transport = transport;
        transport.PeerDisconnected += OnPeerDisconnectedTransport;
        transport.StartReading();
        SendJson(transport.HostPeer, new
                                         {
                                             t = "hello",
                                             proto = CollabProtocol.ProtocolVersion,
                                             name = _localIdentity.DisplayName,
                                             userId = _localIdentity.UserId,
                                         });
        State = SessionState.Connecting;
        StatusText = "Connected to relay, waiting for host...";
    }

    /// <summary>UI thread: surfaces an asynchronous relay failure only for the session that asked.</summary>
    private void ReportRelayFailure(int generation, string errorPrefix, string statusText, Exception exception)
    {
        var message = exception.Message;
        _uiThreadActions.Enqueue(() =>
                                     {
                                         if (generation != _sessionGeneration)
                                             return;

                                         ErrorText = $"{errorPrefix}: {message}";
                                         StatusText = statusText;
                                         State = SessionState.Off;
                                         _sessionProjectName = "";
                                         Log.Warning($"Collab: {errorPrefix.ToLowerInvariant()}: {message}");
                                     });
    }

    public void JoinDirect(string hostAddress, int tcpPort, string password)
    {
        if (IsSessionActive)
            Leave();

        ++_sessionGeneration;
        ErrorText = "";
        _joinedHost = $"{hostAddress}:{tcpPort}";
        State = SessionState.Connecting;
        StatusText = $"Connecting to {hostAddress}:{tcpPort}...";

        try
        {
            if (!IPAddress.TryParse(hostAddress, out var address))
                address = ResolveHostName(hostAddress);

            if (address == null)
                throw new ArgumentException($"'{hostAddress}' is not a valid address");

            _password = password ?? "";
            _sessionLog.Clear();
            var transport = new CollabClientTransport(new IPEndPoint(address, tcpPort));
            _transport = transport;
            _transport.PeerDisconnected += OnPeerDisconnectedTransport;
            transport.Connect();
            transport.SendJson(transport.HostPeer, new
                                                       {
                                                           t = "hello",
                                                           proto = CollabProtocol.ProtocolVersion,
                                                           name = _localIdentity.DisplayName,
                                                           userId = _localIdentity.UserId,
                                                       });
            State = SessionState.Connecting;
        }
        catch (Exception e)
        {
            ErrorText = $"Connection failed: {e.Message}";
            StatusText = "Connection failed";
            _transport?.Dispose();
            _transport = null;
            State = SessionState.Off;
        }
    }

    private static IPAddress? ResolveHostName(string host)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public void Leave()
    {
        // Anything still connecting becomes stale; its completion is discarded.
        ++_sessionGeneration;

        if (_transport != null)
        {
            _transport.PeerDisconnected -= OnPeerDisconnectedTransport;
            _transport.Dispose();
            _transport = null;
        }

        _sessionLog.Clear();
        _opsReceivedThisSecond.Clear();
        _pendingAuth.Clear();
        _chatLog.Clear();
        foreach (var upload in _projectUploads.Values)
            upload.Sender?.Dispose();

        _projectUploads.Clear();
        _projectDownload?.Cancel();
        _projectDownload = null;
        _hostProjectName = "";
        _sessionProjectName = "";
        UserSettings.Config.CollabLastSessionHost = "";
        UserSettings.Save();
        while (_pendingDisconnects.TryDequeue(out _))
        {
            // Drop disconnect events from the dead session; replaying them into a future
            // session would remove peers it never had.
        }
        _localPresence = new PresencePacket();
        _password = "";
        RoomCode = "";
        _undoStack.Clear();
        _redoStack.Clear();
        _activeLocks.Clear();
        UndoNotice = "";

        // The local undo stack only contains this machine's edits; after a session it no longer
        // matches the shared graph state, so replaying it would corrupt the project.
        T3.Editor.UiModel.Commands.UndoRedoStack.Clear();

        State = SessionState.Off;
        StatusText = "Collaboration is off";
        Log.Info("Collab: session ended");
    }

    private void OnPeerDisconnectedTransport(CollabPeerConnection peer)
    {
        // Queued so the manager handles it on the UI thread with everything else.
        _pendingDisconnects.Enqueue(peer);
    }

    private readonly ConcurrentQueue<CollabPeerConnection> _pendingDisconnects = new();

    /// <summary>Session participant names by session id, for display on hosts and clients alike.</summary>
    private readonly Dictionary<int, string> _roster = new();
    public IReadOnlyDictionary<int, string> Roster => _roster;

    /// <summary>User ids by session id; seeds participant colors on the client side too.</summary>
    private readonly Dictionary<int, Guid> _rosterUserIds = new();

    /// <summary>Latest presence per session id, kept separately so clients can render peers they
    /// only know from relayed presence frames.</summary>
    private readonly Dictionary<int, PresencePacket> _latestPresenceByPeer = new();

    /// <summary>Display info about a session participant, or null when nothing is known yet.</summary>
    public CollabPeerDisplay? GetPresence(int peerId)
    {
        PresencePacket? presence = null;
        Guid userId;

        var transportPeer = Peers.FirstOrDefault(p => p.AssignedId == peerId);
        if (transportPeer != null)
        {
            presence = transportPeer.LatestPresence;
            userId = transportPeer.UserId;
        }
        else
        {
            presence = _latestPresenceByPeer.TryGetValue(peerId, out var stored) ? stored : null;
            userId = _rosterUserIds.TryGetValue(peerId, out var id) ? id : Guid.Empty;
        }

        if (userId == Guid.Empty)
            return null;

        return new CollabPeerDisplay
                   {
                       UserId = userId,
                       CompositionId = presence?.CompositionId ?? Guid.Empty,
                       IsHoveringCanvas = presence?.IsHoveringCanvas ?? false,
                       CursorOnCanvas = presence?.CursorOnCanvas ?? Vector2.Zero,
                       AccentColor = CollabColors.FromId(userId),
                       SelectedChildIds = presence?.SelectedChildIds ?? [],
                   };
    }

    /// <summary>Session id assigned to this editor by the host (0 on the host itself).</summary>
    public int MyPeerId { get; private set; }

    /// <summary>All other participants currently known to be inside the given composition.</summary>
    public IEnumerable<(string Name, CollabPeerDisplay Display)> GetPeersInComposition(Guid compositionId)
    {
        foreach (var (peerId, name) in _roster)
        {
            if (peerId == MyPeerId)
                continue;

            var display = GetPresence(peerId);
            if (display == null || display.CompositionId != compositionId)
                continue;

            yield return (name, display);
        }
    }

    internal sealed class CollabPeerDisplay
    {
        public Guid UserId;
        public Guid CompositionId;
        public bool IsHoveringCanvas;
        public Vector2 CursorOnCanvas;
        public Vector4 AccentColor;
        public Guid[] SelectedChildIds = [];
    }

    // ---------------------------------------------------------------------------------------
    // Per-frame update (UI thread)
    // ---------------------------------------------------------------------------------------

    /// <summary>Upper bound of inbound messages processed per frame so a burst can't stall rendering.</summary>
    private const int MaxInboundPerFrame = 512;

    public void Update()
    {
        // Apply background connect results first: they may install or (when stale) release a
        // transport, and everything below depends on the current session state.
        while (_uiThreadActions.TryDequeue(out var uiThreadAction))
        {
            try
            {
                uiThreadAction();
            }
            catch (Exception e)
            {
                Log.Warning($"Collab: applying a session update failed: {e.Message}");
            }
        }

        // Auto-rejoin shortly after launch (e.g. after a project download restarted us).
        if (State == SessionState.Off)
        {
            if (_rejoinCheckTicks == 0)
            {
                _rejoinCheckTicks = Environment.TickCount64 + 8000;
            }
            else if (Environment.TickCount64 > _rejoinCheckTicks && !_rejoinAttempted)
            {
                _rejoinAttempted = true;
                TryAutoRejoin();
            }
        }

        if (_restartAtTicks > 0 && Environment.TickCount64 > _restartAtTicks)
        {
            _restartAtTicks = 0;
            EditorRestart.TryRestart();
            return;
        }

        if (State == SessionState.Off)
            return;

        ProcessPendingDisconnects();

        // A disconnect handled above can end the session (host loss, relay link death), and
        // Leave() disposes the transport — nothing below may run against it.
        if (State == SessionState.Off)
            return;

        DrainInboundQueue();

        // A message handler can also end the session (auth failure, host bye) — the transport
        // is gone at that point, so the remaining steps must not run.
        if (State == SessionState.Off)
            return;

        PumpProjectUploads();
        SendHeartbeatsAndPrune();
        SendLocalPresence();
        RefreshDiscoveredSessions();
    }

    private long _rejoinCheckTicks;
    private bool _rejoinAttempted;

    private void TryAutoRejoin()
    {
        var host = UserSettings.Config.CollabLastSessionHost;
        if (string.IsNullOrEmpty(host))
            return;

        var separator = host.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(host[(separator + 1)..], out var port))
            return;

        Log.Info($"Collab: auto-rejoining last session {host}");
        JoinDirect(host[..separator], port, UserSettings.Config.CollabPassword ?? "");
    }

    private void ProcessPendingDisconnects()
    {
        while (_pendingDisconnects.TryDequeue(out var peer))
        {
            // Sentinel emitted by the relay host transport when the relay link itself died.
            if (peer.AssignedId == int.MaxValue)
            {
                ErrorText = "Lost connection to the relay server";
                Leave();
                return;
            }

            _roster.Remove(peer.AssignedId);
            _rosterUserIds.Remove(peer.AssignedId);
            _latestPresenceByPeer.Remove(peer.AssignedId);
            _pendingAuth.Remove(peer);
            DropLocksOwnedBy(peer.AssignedId);
            DropProjectUpload(peer);

            if (State == SessionState.Hosting)
            {
                Log.Info($"Collab: peer left: {peer}");
                _opsReceivedThisSecond.Remove(peer.AssignedId);
                BroadcastSystemMessage(new
                                           {
                                               t = "peerLeft",
                                               id = peer.AssignedId,
                                               name = peer.Name,
                                           }, except: null);
                StatusText = $"Hosting{(!string.IsNullOrEmpty(RoomCode) ? $" (room {RoomCode})" : "")}, {_roster.Count} participant(s)";
            }
            else if (State == SessionState.Connected && ReferenceEquals(peer, (_transport as CollabClientTransport)?.HostPeer))
            {
                ErrorText = "Disconnected from host";
                Leave();
            }
            else if (State == SessionState.Connected && _transport is CollabRelayClientTransport)
            {
                ErrorText = "Disconnected from relay session";
                Leave();
            }
        }
    }

    private int ActivePeerCount
    {
        get
        {
            var count = 0;
            foreach (var peer in Peers)
            {
                if (peer.IsAuthenticated)
                    count++;
            }

            return count;
        }
    }

    private void DrainInboundQueue()
    {
        var processed = 0;
        // A message handler may end the session (auth failure, host bye), nulling the transport
        // mid-loop — the null-conditional keeps the loop condition safe on the next iteration.
        while (processed < MaxInboundPerFrame && _transport?.Inbound.TryDequeue(out var message) == true)
        {
            processed++;
            if (message.Peer.SessionClosed && message.IsPresence)
                continue;

            if (message is { IsPresence: true, Presence: { } presence })
                HandlePresence(message.Peer, presence);
            else if (message.Json is { } json)
                HandleJson(message.Peer, json);
        }
    }

    private void HandlePresence(CollabPeerConnection peer, PresencePacket presence)
    {
        peer.LatestPresence = presence;
        _latestPresenceByPeer[presence.PeerId] = presence;

        // The host relays presence between clients on the reliable path when UDP isn't available
        // yet. The UDP path already relays on the network thread; relaying here as well would
        // double-send, so only fall back for peers with no known UDP endpoint.
        if (State != SessionState.Hosting)
            return;

        foreach (var other in Peers)
        {
            if (other == peer || !other.IsAuthenticated)
                continue;

            if (other.UdpEndpoint != null)
                continue;

            var buffer = new byte[CollabProtocol.MaxPresencePayload];
            var size = presence.Encode(buffer);
            if (size > 0)
                _transport?.SendToPeer(other, CollabProtocol.FrameType.Presence, buffer.AsSpan(0, size));
        }
    }

    private void HandleJson(CollabPeerConnection peer, JObject json)
    {
        var type = CollabProtocol.GetString(json, "t");

        // Only the handshake may arrive before a peer authenticated. Everything else either
        // mutates the shared graph, locks parameters or streams project files, and a
        // password-protected host must not let a connection that never passed the challenge
        // do any of those.
        if (!peer.IsAuthenticated && type is not ("hello" or "auth" or "welcome" or "authOk" or "authFail"))
        {
            Log.Debug($"Collab: ignoring '{type}' from unauthenticated {peer}");
            return;
        }

        switch (type)
        {
            case "hello" when State == SessionState.Hosting:
                HandleHostHello(peer, json);
                break;

            case "welcome" when State == SessionState.Connecting:
                HandleClientWelcome(json);
                break;

            case "auth" when State == SessionState.Hosting:
                HandleHostAuth(peer, json);
                break;

            case "authOk" when State == SessionState.Connecting:
                HandleClientAuthOk(peer, json);
                break;

            case "authFail" when State == SessionState.Connecting:
                ErrorText = "Rejected by host: " + CollabProtocol.GetString(json, "reason", "authentication failed");
                Leave();
                break;

            case "op" when IsSessionActive:
                HandleOp(peer, json);
                break;

            case "undoRequest" when State == SessionState.Hosting:
                PerformGlobalUndo();
                break;

            case "redoRequest" when State == SessionState.Hosting:
                PerformGlobalRedo();
                break;

            case "lock" when IsSessionActive:
                HandleLock(peer, json, true);
                break;

            case "unlock" when IsSessionActive:
                HandleLock(peer, json, false);
                break;

            case "requestSync" when State == SessionState.Hosting:
                SendSyncLog(peer);
                break;

            case "syncStart" when State == SessionState.Connected:
                _syncBatch = 0;
                Log.Debug("Collab: applying host composition snapshots...");
                break;

            case "syncOp" when State == SessionState.Connected:
                if (json["op"] is JObject op)
                {
                    CollabCommandWire.ApplyOp(op);
                    _syncBatch++;
                }

                break;

            case "syncEnd" when State == SessionState.Connected:
                Log.Info($"Collab: replay complete ({_syncBatch} ops)");
                break;

            case "peerJoined" when State == SessionState.Connected:
            {
                var name = CollabProtocol.GetString(json, "name", "?");
                if (CollabProtocol.TryGetInt(json, "id", out var joinedId))
                {
                    _roster[joinedId] = name;
                    if (CollabProtocol.TryGetGuid(json, "userId", out var joinedUserId))
                        _rosterUserIds[joinedId] = joinedUserId;
                }

                Log.Info($"Collab: {name} joined");
                AppendChat(null, $"{name} joined");
                StatusText = State == SessionState.Hosting
                                 ? $"Hosting{(!string.IsNullOrEmpty(RoomCode) ? $" (room {RoomCode})" : "")}, {_roster.Count} participant(s)"
                                 : StatusText;
                break;
            }

            case "peerLeft" when State == SessionState.Connected:
            {
                var name = CollabProtocol.GetString(json, "name", "?");
                if (CollabProtocol.TryGetInt(json, "id", out var leftId))
                    _roster.Remove(leftId);

                Log.Info($"Collab: {name} left");
                AppendChat(null, $"{name} left");
                break;
            }

            case "chat" when IsSessionActive:
            {
                // Hosts relay chat to everyone else; both sides show it in the session chat log.
                var text = CollabProtocol.GetString(json, "text");
                if (string.IsNullOrEmpty(text))
                    break;

                var sender = CollabProtocol.GetString(json, "name", "?");
                AppendChat(sender, text);
                if (State == SessionState.Hosting)
                    BroadcastSystemMessage(json, except: peer);
                break;
            }

            case "requestProject" when State == SessionState.Hosting:
                HandleRequestProject(peer, json);
                break;

            case "projectBegin":
            case "fileBegin":
            case "fileChunk":
            case "fileEnd":
            case "projectDone":
            case "projectFail":
                HandleProjectDownload(peer, json);
                break;

            case "notice":
                Log.Debug($"Collab host: {CollabProtocol.GetString(json, "msg")}");
                break;

            default:
                Log.Debug($"Collab: ignoring message type '{type}' in state {State}");
                break;
        }
    }

    private int _syncBatch;

    // ---------------------------------------------------------------------------------------
    // Handshake
    // ---------------------------------------------------------------------------------------

    private sealed class PendingHandshake
    {
        public byte[]? Salt;
        public byte[]? Nonce;
    }

    /// <summary>
    /// Challenge per peer. A single shared challenge made simultaneous joins clobber each other:
    /// the second hello overwrote the first nonce, so the first participant's correct answer was
    /// rejected, and completing one handshake discarded every other peer's pending challenge.
    /// </summary>
    private readonly Dictionary<CollabPeerConnection, PendingHandshake> _pendingAuth = new();

    private void HandleHostHello(CollabPeerConnection peer, JObject json)
    {
        peer.Name = TruncateName(CollabProtocol.GetString(json, "name", "peer"));
        peer.UserId = CollabProtocol.TryGetGuid(json, "userId", out var userId) ? userId : Guid.NewGuid();
        peer.MarkHeardFrom();

        if (string.IsNullOrEmpty(_password))
        {
            CompleteHostAuth(peer);
            return;
        }

        var salt = CollabAuth.CreateSalt();
        var nonce = CollabAuth.CreateNonce();
        _pendingAuth[peer] = new PendingHandshake { Salt = salt, Nonce = nonce };
        SendJson(peer, new
                           {
                               t = "welcome",
                               proto = CollabProtocol.ProtocolVersion,
                               pwd = true,
                               salt = CollabAuth.ToHexString(salt),
                               nonce = CollabAuth.ToHexString(nonce),
                           });
    }

    private void HandleHostAuth(CollabPeerConnection peer, JObject json)
    {
        if (!_pendingAuth.TryGetValue(peer, out var pending) || pending.Salt == null || pending.Nonce == null)
        {
            RejectPeer(peer, "no challenge pending");
            return;
        }

        if (!CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "mac"), out var mac)
            || mac.Length != CollabAuth.MacSizeBytes)
        {
            RejectPeer(peer, "malformed authentication response");
            return;
        }

        var key = CollabAuth.DeriveKey(_password, pending.Salt);
        var expected = CollabAuth.ComputeMac(key, pending.Nonce);
        if (!CollabAuth.MacsEqual(expected, mac))
        {
            RejectPeer(peer, "wrong password");
            return;
        }

        CompleteHostAuth(peer);
    }

    private void CompleteHostAuth(CollabPeerConnection peer)
    {
        peer.IsAuthenticated = true;
        _pendingAuth.Remove(peer);
        _roster[peer.AssignedId] = peer.Name;
        _rosterUserIds[peer.AssignedId] = peer.UserId;

        var peersPayload = new JArray();
        foreach (var other in Peers)
        {
            if (other == peer || !other.IsAuthenticated)
                continue;

            peersPayload.Add(new JObject
                                 {
                                     ["id"] = other.AssignedId,
                                     ["userId"] = other.UserId,
                                     ["name"] = other.Name,
                                 });
        }

        SendJson(peer, new
                           {
                               t = "authOk",
                               yourId = peer.AssignedId,
                               hostId = 0,
                               hostName = _localIdentity.DisplayName,
                               hostUserId = _localIdentity.UserId,
                               peers = peersPayload,
                               logCount = _sessionLog.Count,
                               project = _sessionProjectName,
                           });

        BroadcastSystemMessage(new
                                   {
                                       t = "peerJoined",
                                       id = peer.AssignedId,
                                       userId = peer.UserId,
                                       name = peer.Name,
                                   }, except: peer);

        StatusText = $"Hosting, {ActivePeerCount} participant(s)";
        Log.Info($"Collab: peer authenticated: {peer}");
    }

    private void RejectPeer(CollabPeerConnection peer, string reason)
    {
        Log.Info($"Collab: rejected peer {peer}: {reason}");
        _pendingAuth.Remove(peer);
        SendJson(peer, new { t = "authFail", reason });
        peer.SessionClosed = true;
        peer.Close();
    }

    private void HandleClientWelcome(JObject json)
    {
        if (State != SessionState.Connecting || HostLink is not { } host)
            return;

        var passwordRequired = json["pwd"]?.Value<bool>() ?? false;
        if (!passwordRequired)
            return;

        if (!CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "salt"), out var salt)
            || !CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "nonce"), out var nonce))
        {
            ErrorText = "Host sent a malformed challenge";
            Leave();
            return;
        }

        var key = CollabAuth.DeriveKey(_password, salt);
        var mac = CollabAuth.ComputeMac(key, nonce);
        SendJson(host, new { t = "auth", mac = CollabAuth.ToHexString(mac) });
    }

    private void HandleClientAuthOk(CollabPeerConnection peer, JObject json)
    {
        peer.IsAuthenticated = true;
        var assignedId = json["yourId"]?.Value<int>() ?? 0;
        _localPresence = new PresencePacket { PeerId = (byte)assignedId };
        MyPeerId = assignedId;

        // The host introduces itself so clients can render its cursor and participant entry.
        if (CollabProtocol.TryGetInt(json, "hostId", out var hostId))
        {
            _roster[hostId] = CollabProtocol.GetString(json, "hostName", "host");
            if (CollabProtocol.TryGetGuid(json, "hostUserId", out var hostUserId))
                _rosterUserIds[hostId] = hostUserId;
        }

        foreach (var token in json["peers"] ?? new JArray())
        {
            if (token is not JObject entry)
                continue;

            var name = CollabProtocol.GetString(entry, "name", "?");
            var id = CollabProtocol.TryGetInt(entry, "id", out var parsedId) ? parsedId : 0;
            _roster[id] = name;
            if (CollabProtocol.TryGetGuid(entry, "userId", out var rosterUserId))
                _rosterUserIds[id] = rosterUserId;

            Log.Info($"Collab: {name} is in this session");
        }

        State = SessionState.Connected;
        StatusText = $"Connected as {_localIdentity.DisplayName}";
        SendJson(peer, new { t = "requestSync" });

        // Remember the session so a restart (e.g. after a project download) can rejoin, then make
        // sure the host's project is open locally — download it when this machine doesn't have it.
        UserSettings.Config.CollabLastSessionHost = _joinedHost;
        UserSettings.Save();
        _hostProjectName = CollabProtocol.GetString(json, "project");
        EnsureHostProjectAvailable();
    }

    /// <summary>host:port of the session this client dialed into (persisted for auto-rejoin).</summary>
    private string _joinedHost = "";

    private string _sessionProjectName = "";
    private string _hostProjectName = "";

    /// <summary>The project new participants receive; empty when hosting without a project open.</summary>
    public string SharedProjectName => _sessionProjectName;

    /// <summary>
    /// The project chosen in the collaboration settings; null means "share the focused project".
    /// Call sites that host outside the settings panel use it too, so the dropdown applies everywhere.
    /// </summary>
    public static string? ConfiguredShareProject =>
        string.IsNullOrWhiteSpace(UserSettings.Config.CollabShareProject) ? null : UserSettings.Config.CollabShareProject;

    /// <summary>
    /// Opens the host's project locally, or asks the host to stream it over when it doesn't exist
    /// here. Once the files have landed the editor restarts and auto-rejoins; from then on the
    /// session only syncs deltas.
    /// </summary>
    private void EnsureHostProjectAvailable()
    {
        // Only direct sessions: the persisted auto-rejoin target (`_joinedHost`) is a host:port, so
        // a relay joiner that restarted for a download could not rejoin by room code.
        if (string.IsNullOrEmpty(_hostProjectName) || _transport is not CollabClientTransport { HostPeer: { } host })
            return;

        if (TryOpenProjectByName(_hostProjectName))
            return;

        Log.Info($"Collab: requesting project '{_hostProjectName}' from host");
        StatusText = $"Downloading project '{ShortProjectName(_hostProjectName)}' from host...";
        SendJson(host, new { t = "requestProject", project = _hostProjectName });
    }

    internal static string ShortProjectName(string displayName)
    {
        // Created projects get a "<name> (<namespace>)" display name.
        var paren = displayName.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? displayName[..paren] : displayName;
    }

    /// <summary>Opens a local project by display name (short form accepted) into the visible graph window.</summary>
    internal static bool TryOpenProjectByName(string name)
    {
        // An already opened view is reused: recreating its ProjectView would drop its view state.
        if (TryFocusOpenProject(name))
            return true;

        foreach (var p in SymbolPackage.AllPackages)
        {
            if (p is not EditorSymbolPackage editorPackage
                || !MatchesProjectName(editorPackage.DisplayName, name))
                continue;

            if (!OpenedProject.TryCreate(editorPackage, out var openedProject, out var failureLog)
                || openedProject == null)
            {
                Log.Warning($"Collab: could not open project '{name}': {failureLog}");
                return false;
            }

            GraphWindow? graphWindow = null;
            foreach (var window in GraphWindow.GraphWindowInstances)
            {
                if (!window.Config.Visible)
                    continue;

                graphWindow = window;
                break;
            }

            graphWindow ??= GraphWindow.GraphWindowInstances.Count > 0 ? GraphWindow.GraphWindowInstances[0] : null;
            if (graphWindow == null || !graphWindow.TrySetToProject(openedProject, tryRestoreViewArea: false))
            {
                Log.Warning($"Collab: could not open project '{name}' in a graph window");
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>Brings an already opened project's graph view to the front without recreating it.</summary>
    internal static bool TryFocusOpenProject(string name)
    {
        foreach (var window in GraphWindow.GraphWindowInstances)
        {
            var view = window.ProjectView;
            if (view == null || !MatchesProjectName(view.OpenedProject.Package.DisplayName, name))
                continue;

            window.Config.Visible = true;
            view.SetAsFocused();
            return true;
        }

        return false;
    }

    private static bool MatchesProjectName(string displayName, string name)
    {
        return string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase)
               || displayName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------
    // Ops
    // ---------------------------------------------------------------------------------------

    /// <summary>Hooked from <see cref="T3.Editor.UiModel.Commands.UndoRedoStack"/> for every local edit.</summary>
    public void OnLocalCommandAdded(ICommand command)
    {
        if (!IsSessionActive)
            return;

        var op = CollabCommandWire.EncodeCommand(command);
        if (op == null)
            return;

        AppendToLog(op);
        RecordForUndo(op);

        var envelope = new JObject
                           {
                               ["t"] = "op",
                               ["op"] = op,
                           };

        if (State == SessionState.Hosting)
        {
            BroadcastSystemMessage(envelope, except: null);
        }
        else if (State == SessionState.Connected && HostLink is { } host)
        {
            SendJson(host, envelope);
        }
    }

    private void HandleOp(CollabPeerConnection peer, JObject json)
    {
        if (!peer.IsAuthenticated)
            return;

        if (!RateAllow(peer))
        {
            Log.Warning($"Collab: rate limiting {peer}");
            return;
        }

        if (json["op"] is not JObject op)
            return;

        // Host-authoritative: apply locally, then relay to everyone else so clients only ever
        // consume host-ordered ops.
        CollabCommandWire.ApplyOp(op);
        AppendToLog(op);
        RecordForUndo(op);

        if (State == SessionState.Hosting)
        {
            var envelope = new JObject { ["t"] = "op", ["op"] = op };
            _transport?.BroadcastJson(Peers, envelope, except: peer);
        }
    }

    private void AppendToLog(JObject op)
    {
        if (_sessionLog.Count >= MaxSessionLogEntries)
            _sessionLog.RemoveAt(0);

        _sessionLog.Add(op);
    }

    // ---------------------------------------------------------------------------------------
    // Session-wide undo/redo (host-authoritative)
    // ---------------------------------------------------------------------------------------

    /// <summary>Undoable ops in live order. The host owns the truth; clients just send requests.</summary>
    private readonly List<JObject> _undoStack = [];
    private readonly List<JObject> _redoStack = [];

    /// <summary>Last transient undo/redo notice for the settings panel.</summary>
    public string UndoNotice { get; private set; } = "";

    /// <summary>
    /// Routes a local undo gesture into the session: hosting performs the global undo, connected
    /// clients ask the host. Returns false when no session is running (classic local undo).
    /// </summary>
    public bool TryRouteGlobalUndo()
    {
        if (State == SessionState.Off)
            return false;

        if (State == SessionState.Hosting)
        {
            PerformGlobalUndo();
            return true;
        }

        if (State == SessionState.Connected && HostLink is { } host)
        {
            SendJson(host, new { t = "undoRequest" });
            return true;
        }

        return true; // mid-handshake: swallow the gesture
    }

    public bool TryRouteGlobalRedo()
    {
        if (State == SessionState.Off)
            return false;

        if (State == SessionState.Hosting)
        {
            PerformGlobalRedo();
            return true;
        }

        if (State == SessionState.Connected && HostLink is { } host)
        {
            SendJson(host, new { t = "redoRequest" });
            return true;
        }

        return true;
    }

    private void RecordForUndo(JObject op)
    {
        if (State != SessionState.Hosting)
            return;

        _undoStack.Add(op);
        _redoStack.Clear();

        // Cap memory; the replay log keeps its own bound.
        if (_undoStack.Count > MaxSessionLogEntries)
            _undoStack.RemoveAt(0);
    }

    private void PerformGlobalUndo()
    {
        UndoNotice = "";
        if (State != SessionState.Hosting)
            return;

        while (_undoStack.Count > 0)
        {
            var op = _undoStack[^1];
            var inverse = CollabCommandWire.InvertOp(op);
            if (inverse == null)
            {
                UndoNotice = "The last edit can't be undone remotely and was skipped";
                Log.Debug($"Collab: op type '{CollabProtocol.GetString(op, "type")}' is not invertible");
                _undoStack.RemoveAt(_undoStack.Count - 1);
                _redoStack.Clear(); // Can't keep a consistent redo chain past a skipped op.
                continue;
            }

            _undoStack.RemoveAt(_undoStack.Count - 1);
            CollabCommandWire.ApplyOp(inverse);
            AppendToLog(inverse);
            _redoStack.Add(op);
            BroadcastSystemMessage(new JObject { ["t"] = "op", ["op"] = inverse }, except: null);
            Log.Debug($"Collab: global undo of '{CollabProtocol.GetString(op, "type")}'");
            return;
        }

        UndoNotice = "Nothing left to undo";
    }

    private void PerformGlobalRedo()
    {
        UndoNotice = "";
        if (State != SessionState.Hosting)
            return;

        if (_redoStack.Count == 0)
        {
            UndoNotice = "Nothing to redo";
            return;
        }

        var op = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        CollabCommandWire.ApplyOp(op);
        AppendToLog(op);
        _undoStack.Add(op);
        BroadcastSystemMessage(new JObject { ["t"] = "op", ["op"] = op }, except: null);
        Log.Debug($"Collab: global redo of '{CollabProtocol.GetString(op, "type")}'");
    }

    // ---------------------------------------------------------------------------------------
    // Parameter locks (soft mutexes)
    // ---------------------------------------------------------------------------------------

    public readonly record struct LockKey(Guid CompositionId, Guid ChildId, Guid InputId);

    /// <summary>Active input locks: which participant session id holds which parameter.</summary>
    private readonly Dictionary<LockKey, int> _activeLocks = [];

    public void AcquireInputLock(Guid compositionId, Guid childId, Guid inputId)
    {
        if (!IsSessionActive)
            return;

        var key = new LockKey(compositionId, childId, inputId);
        _activeLocks[key] = MyPeerId;
        BroadcastSystemMessage(new
                                   {
                                       t = "lock",
                                       c = compositionId,
                                       ch = childId,
                                       i = inputId,
                                       id = MyPeerId,
                                   }, except: null);
    }

    public void ReleaseInputLock(Guid compositionId, Guid childId, Guid inputId)
    {
        if (!IsSessionActive)
            return;

        var key = new LockKey(compositionId, childId, inputId);
        if (!_activeLocks.TryGetValue(key, out var owner) || owner != MyPeerId)
        {
            // Someone else took over (e.g. after our reconnect) — don't release their lock.
            return;
        }

        _activeLocks.Remove(key);
        BroadcastSystemMessage(new
                                   {
                                       t = "unlock",
                                       c = compositionId,
                                       ch = childId,
                                       i = inputId,
                                       id = MyPeerId,
                                   }, except: null);
    }

    public bool IsInputLockedByOther(Guid compositionId, Guid childId, Guid inputId)
    {
        return _activeLocks.TryGetValue(new LockKey(compositionId, childId, inputId), out var owner)
               && owner != MyPeerId;
    }

    public sealed class InputLockInfo
    {
        public Guid ChildId;
        public int PeerId;
        public string HolderName = "";
        public Vector4 Color;
    }

    /// <summary>Locks inside the given composition, for the graph badge overlay.</summary>
    public IEnumerable<InputLockInfo> GetInputLocksForComposition(Guid compositionId)
    {
        foreach (var (key, peerId) in _activeLocks)
        {
            if (key.CompositionId != compositionId || peerId == MyPeerId)
                continue;

            var userId = _rosterUserIds.TryGetValue(peerId, out var id) ? id : Guid.Empty;
            if (userId == Guid.Empty)
                continue;

            yield return new InputLockInfo
                             {
                                 ChildId = key.ChildId,
                                 PeerId = peerId,
                                 HolderName = _roster.TryGetValue(peerId, out var name) ? name : "peer",
                                 Color = CollabColors.FromId(userId),
                             };
        }
    }

    private void HandleLock(CollabPeerConnection peer, JObject json, bool acquire)
    {
        if (!peer.IsAuthenticated || !RateAllow(peer))
            return;

        if (!CollabProtocol.TryGetGuid(json, "c", out var compositionId)
            || !CollabProtocol.TryGetGuid(json, "ch", out var childId)
            || !CollabProtocol.TryGetGuid(json, "i", out var inputId))
            return;

        var key = new LockKey(compositionId, childId, inputId);

        // On a host the sender owns the lock; an "id" in its message is only a claim. On a client
        // the message was relayed by the host and the transport peer is always the host (id 0),
        // so the id field is the owner the host recorded.
        var ownerPeerId = peer.AssignedId;
        if (State == SessionState.Connected && CollabProtocol.TryGetInt(json, "id", out var relayedOwnerId))
            ownerPeerId = relayedOwnerId;

        var relay = true;
        if (acquire)
        {
            _activeLocks[key] = ownerPeerId;
        }
        else
        {
            // Only the holder (or the host on their behalf) may clear a lock.
            if (_activeLocks.TryGetValue(key, out var currentOwner) && currentOwner == ownerPeerId)
                _activeLocks.Remove(key);
            else
                relay = false;
        }

        if (State == SessionState.Hosting && relay)
        {
            var envelope = (JObject)json.DeepClone();
            envelope["id"] = ownerPeerId;
            _transport?.BroadcastJson(Peers, envelope, except: peer);
        }
    }

    private void DropLocksOwnedBy(int peerId)
    {
        var released = new List<LockKey>();
        foreach (var (key, owner) in _activeLocks)
        {
            if (owner == peerId)
                released.Add(key);
        }

        foreach (var key in released)
        {
            _activeLocks.Remove(key);
            if (State == SessionState.Hosting)
            {
                BroadcastSystemMessage(new
                                           {
                                               t = "unlock",
                                               c = key.CompositionId,
                                               ch = key.ChildId,
                                               i = key.InputId,
                                               id = peerId,
                                           }, except: null);
            }
        }
    }

    private void SendSyncLog(CollabPeerConnection peer)
    {
        // State sync (roadmap 5.1): instead of replaying the raw op log — which only converges when
        // both sides started from identical projects — send full snapshots of every composition the
        // session touched, plus the host's current composition. apply_snapshot replaces the client's
        // copy of those compositions with the host's state.
        var compositionIds = new List<Guid>();
        foreach (var op in _sessionLog)
        {
            if (CollabProtocol.TryGetGuid(op, "c", out var compositionId)
                && compositionId != Guid.Empty
                && !compositionIds.Contains(compositionId))
            {
                compositionIds.Add(compositionId);
            }
        }

        var focused = ProjectView.Focused;
        var currentCompositionId = focused?.InstView?.Symbol.Id ?? Guid.Empty;
        if (currentCompositionId != Guid.Empty && !compositionIds.Contains(currentCompositionId))
            compositionIds.Insert(0, currentCompositionId);

        var snapshotCount = 0;
        SendJson(peer, new { t = "syncStart", count = compositionIds.Count });
        foreach (var compositionId in compositionIds)
        {
            if (!SymbolUiRegistry.TryGetSymbolUi(compositionId, out var compositionUi))
                continue;

            var snapshotOp = CollabCommandWire.BuildSnapshotOp(compositionUi);
            SendJson(peer, new { t = "syncOp", op = snapshotOp });
            snapshotCount++;
        }

        SendJson(peer, new { t = "syncEnd" });
        Log.Info($"Collab: sent {snapshotCount} composition snapshot(s) to {peer}");
    }

    private bool RateAllow(CollabPeerConnection peer)
    {
        _opsReceivedThisSecond.TryGetValue(peer.AssignedId, out var count);
        if (count >= MaxOpsPerPeerPerSecond)
            return false;

        _opsReceivedThisSecond[peer.AssignedId] = count + 1;
        return true;
    }

    private const int MaxOpsPerPeerPerSecond = 256;

    // ---------------------------------------------------------------------------------------
    // Presence
    // ---------------------------------------------------------------------------------------

    /// <summary>Called by the graph overlay each frame; only the latest snapshot is kept.</summary>
    public void SetLocalPresence(Guid compositionId, Vector2 cursorOnCanvas, bool hoveringCanvas, Guid[] selectedChildIds)
    {
        _localPresence.CompositionId = compositionId;
        _localPresence.CursorOnCanvas = cursorOnCanvas;
        _localPresence.Flags = hoveringCanvas ? PresencePacket.FlagHoveringCanvas : (byte)0;
        _localPresence.SelectedChildIds = selectedChildIds;
    }

    private void SendLocalPresence()
    {
        var now = Environment.TickCount64;
        if (now < _nextPresenceSendTicks || !IsSessionActive)
            return;

        _nextPresenceSendTicks = now + CollabProtocol.PresenceSendIntervalMs;

        var buffer = new byte[CollabProtocol.MaxPresencePayload];
        var size = _localPresence.Encode(buffer);
        if (size <= 0)
            return;

        var payload = buffer.AsSpan(0, size);

        if (_transport is CollabClientTransport directClient)
        {
            directClient.SendPresence(payload);
        }
        else if (_transport is CollabRelayClientTransport relayClient)
        {
            relayClient.SendPresence(payload);
        }
        else if (_transport is CollabHostTransport host)
        {
            _localPresence.PeerId = 0;
            size = _localPresence.Encode(buffer);
            if (size <= 0)
                return;

            foreach (var peer in host.Peers)
            {
                if (peer.IsAuthenticated)
                    host.SendPresenceTo(peer, buffer.AsSpan(0, size));
            }
        }
        else if (_transport is CollabRelayHostTransport relayHost)
        {
            _localPresence.PeerId = 0;
            size = _localPresence.Encode(buffer);
            if (size <= 0)
                return;

            foreach (var peer in relayHost.Peers)
            {
                if (peer.IsAuthenticated)
                    relayHost.SendPresenceTo(peer, buffer.AsSpan(0, size));
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Heartbeats, timeouts and discovery
    // ---------------------------------------------------------------------------------------

    private void SendHeartbeatsAndPrune()
    {
        var now = Environment.TickCount64;
        var rateWindowExpired = now - _nextRateResetTicks >= 1000;
        if (rateWindowExpired)
        {
            _nextRateResetTicks = now;
            _opsReceivedThisSecond.Clear();
        }

        if (now < _nextHeartbeatTicks)
            return;

        _nextHeartbeatTicks = now + CollabProtocol.HeartbeatIntervalMs;
        var timestamp = BitConverter.GetBytes(Environment.TickCount64);

        foreach (var peer in Peers)
        {
            if (peer.IsTimedOut)
            {
                Log.Info($"Collab: peer {peer} timed out");
                peer.SessionClosed = true;
                peer.Close();
                continue;
            }

            if (peer.IsAuthenticated)
                _transport?.SendToPeer(peer, CollabProtocol.FrameType.Heartbeat, timestamp);
        }
    }

    public void StartDiscovery()
    {
        _discoveryScanner ??= new LanDiscoveryScanner();
        _discovered.Clear();
        _discoveryScanner.Start(DefaultDiscoveryPort, UserSettings.Config.NetworkInterface);
    }

    public void StopDiscovery()
    {
        _discoveryScanner?.Stop();
    }

    private static int DefaultDiscoveryPort => UserSettings.Config.CollabPort + LanDiscoveryResponder.DiscoveryPortOffset;

    private LanDiscoveryScanner? _hubScanner;
    private long _lastHubScanTicks;

    /// <summary>
    /// Hub-facing discovery refresh: keeps scanning while no session is active so the project
    /// list can show live sessions. During a session the collab update loop refreshes instead,
    /// and the hub scanner shuts down.
    /// </summary>
    public void RefreshDiscoveryFromHub()
    {
        if (State != SessionState.Off)
        {
            if (_hubScanner != null)
            {
                _hubScanner.Stop();
                _hubScanner.Dispose();
                _hubScanner = null;
            }

            return;
        }

        _hubScanner ??= new LanDiscoveryScanner();
        if (Environment.TickCount64 - _lastHubScanTicks > 1000)
        {
            _lastHubScanTicks = Environment.TickCount64;
            _hubScanner.Start(DefaultDiscoveryPort, UserSettings.Config.NetworkInterface); // re-probe; Start restarts its loop cleanly
        }

        RefreshDiscoveredFrom(_hubScanner);
    }

    private void RefreshDiscoveredSessions()
    {
        if (_discoveryScanner == null)
            return;

        RefreshDiscoveredFrom(_discoveryScanner);
    }

    private void RefreshDiscoveredFrom(LanDiscoveryScanner scanner)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(3);
        while (scanner.Results.TryDequeue(out var result))
        {
            var announcement = result.Announcement;
            var session = new DiscoveredSession
                              {
                                  Name = CollabProtocol.GetString(announcement, "name", "TiXL session"),
                                  HostName = CollabProtocol.GetString(announcement, "host", ""),
                                  Address = result.Source.Address.ToString(),
                                  Port = announcement["port"]?.Value<int>() ?? CollabProtocol.DefaultTcpPort,
                                  PasswordProtected = announcement["pwd"]?.Value<bool>() ?? false,
                                  Project = CollabProtocol.GetString(announcement, "project", ""),
                              };

            if (_discovered.Any(d => d.Address == session.Address && d.Port == session.Port))
                continue;

            _discovered.Add(session);
            Log.Debug($"Collab: discovered session '{session.Name}' at {session.Address}:{session.Port}");
        }

        _discovered.RemoveAll(d => d.Seen < cutoff);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers shared with transports
    // ---------------------------------------------------------------------------------------

    private void SendJson(CollabPeerConnection peer, object message) => _transport?.SendJson(peer, message);

    private void BroadcastSystemMessage(object message, CollabPeerConnection? except = null) => _transport?.BroadcastJson(Peers, message, except);

    /// <summary>LAN discovery announcement used in the settings tab.</summary>
    public sealed class DiscoveredSession
    {
        public string Name = "";
        public string HostName = "";
        public string Address = "";
        public int Port;
        public bool PasswordProtected;
        public string Project = "";
        public DateTime Seen = DateTime.UtcNow;

        public override string ToString() => $"{Name}  ({Address}:{Port})";
    }

    // ---------------------------------------------------------------------------------------
    // Session chat
    // ---------------------------------------------------------------------------------------

    /// <summary>One line of the session chat; system lines (join/left) carry a null name.</summary>
    public sealed class ChatEntry
    {
        public DateTime Time = DateTime.Now;
        public string? Name;
        public string Text = "";
    }

    private readonly List<ChatEntry> _chatLog = [];
    private const int MaxChatEntries = 200;

    /// <summary>Session chat history, newest last. UI thread only (handlers run on it).</summary>
    public IReadOnlyList<ChatEntry> ChatLog => _chatLog;

    private void AppendChat(string? name, string text)
    {
        _chatLog.Add(new ChatEntry { Name = name, Text = text });
        if (_chatLog.Count > MaxChatEntries)
            _chatLog.RemoveRange(0, _chatLog.Count - MaxChatEntries);
    }

    /// <summary>Sends a chat line to the session (to the host as a client, to everyone as host).</summary>
    public void SendChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsSessionActive)
            return;

        var message = new
                          {
                              t = "chat",
                              name = _localIdentity.DisplayName,
                              text,
                          };
        if (State == SessionState.Hosting)
        {
            AppendChat(_localIdentity.DisplayName, text);
            BroadcastSystemMessage(message, except: null);
        }
        else if (HostLink is { } host)
        {
            _transport?.SendJson(host, message);
            AppendChat(_localIdentity.DisplayName, text);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Project download on join (host streams the whole project folder, client restarts into it)
    // ---------------------------------------------------------------------------------------

    private sealed class ProjectUpload
    {
        public required CollabPeerConnection Peer;
        public required string ProjectFolder;
        public required string ProjectName;
        public required Queue<string> PendingFiles;
        public BoygroupDeployer.FileSender? Sender;
        public int TotalFiles;
        public int DoneFiles;
        public bool HeaderSent;
    }

    private readonly Dictionary<CollabPeerConnection, ProjectUpload> _projectUploads = new();

    private void HandleRequestProject(CollabPeerConnection peer, JObject json)
    {
        var projectName = CollabProtocol.GetString(json, "project");
        var folder = FindProjectFolder(projectName);
        if (folder == null)
        {
            SendJson(peer, new { t = "projectFail", reason = $"host does not have project '{projectName}'" });
            return;
        }

        // A repeated request replaces the running transfer; its open file handle must go with it.
        DropProjectUpload(peer);

        var inventory = BoygroupDeployer.BuildInventory(folder);
        _projectUploads[peer] = new ProjectUpload
                                    {
                                        Peer = peer,
                                        ProjectFolder = folder,
                                        ProjectName = ShortProjectName(projectName),
                                        PendingFiles = new Queue<string>(inventory.Keys),
                                        TotalFiles = inventory.Count,
                                    };
        Log.Info($"Collab: sending project '{projectName}' ({inventory.Count} files) to {peer.Name}");
    }

    /// <summary>Ends a pending upload for one peer and releases the file it had open.</summary>
    private void DropProjectUpload(CollabPeerConnection peer)
    {
        if (!_projectUploads.Remove(peer, out var upload))
            return;

        upload.Sender?.Dispose();
    }

    internal static string? FindProjectFolder(string displayName)
    {
        foreach (var p in SymbolPackage.AllPackages)
        {
            if (p is EditableSymbolProject project
                && MatchesProjectName(project.DisplayName, displayName))
            {
                return project.CsProjectFile.Directory;
            }
        }

        return null;
    }

    /// <summary>Feeds pending project files into the connection, bounded per frame like the boygroup deploy.</summary>
    private void PumpProjectUploads()
    {
        if (_projectUploads.Count == 0)
            return;

        var budget = 32;
        List<CollabPeerConnection>? finished = null;
        foreach (var upload in _projectUploads.Values)
        {
            while (budget > 0)
            {
                if (!upload.HeaderSent)
                {
                    upload.HeaderSent = true;
                    SendJson(upload.Peer, new { t = "projectBegin", project = upload.ProjectName, files = upload.TotalFiles });
                    budget--;
                    continue;
                }

                if (upload.Sender == null)
                {
                    if (upload.PendingFiles.Count == 0)
                    {
                        SendJson(upload.Peer, new { t = "projectDone", project = upload.ProjectName });
                        (finished ??= []).Add(upload.Peer);
                        break;
                    }

                    var next = upload.PendingFiles.Dequeue();
                    BoygroupDeployer.FileSender sender;
                    try
                    {
                        sender = new BoygroupDeployer.FileSender(upload.ProjectFolder, next);
                    }
                    catch (Exception e)
                    {
                        // The file vanished or became unreadable after the inventory was taken.
                        Log.Warning($"Collab: cannot send {next} to {upload.Peer}: {e.Message}");
                        upload.DoneFiles++;
                        continue;
                    }

                    SendJson(upload.Peer, new { t = "fileBegin", path = next });
                    upload.Sender = sender;
                    budget--;
                    continue;
                }

                JObject? message;
                try
                {
                    message = upload.Sender.NextMessage();
                }
                catch (Exception e)
                {
                    // A read error must not escape into the frame loop mid-transfer.
                    Log.Warning($"Collab: cannot read {upload.Sender.RelativePath} for {upload.Peer}: {e.Message}");
                    upload.Sender.Dispose();
                    upload.Sender = null;
                    upload.DoneFiles++;
                    continue;
                }

                if (message == null)
                {
                    upload.Sender.Dispose();
                    upload.Sender = null;
                    upload.DoneFiles++;
                    continue;
                }

                SendJson(upload.Peer, message);
                budget--;
            }
        }

        if (finished != null)
        {
            foreach (var peer in finished)
                _projectUploads.Remove(peer);
        }
    }

    private BoygroupDeployer.FileReceiver? _projectDownload;
    private string _projectDownloadFolder = "";
    private long _restartAtTicks;

    private void HandleProjectDownload(CollabPeerConnection peer, JObject json)
    {
        // Only the host streams a project; a peer sending transfer frames must never create
        // directories or write files here (the path checks below are the second line of defense).
        if (State != SessionState.Connected || !ReferenceEquals(peer, HostLink))
            return;

        try
        {
            HandleProjectDownloadMessage(json);
        }
        catch (Exception e)
        {
            // Malformed base64, an unsafe path or an unwritable folder must fail this transfer,
            // not the render loop that drains the inbound queue.
            Log.Warning($"Collab: project download failed: {e.Message}");
            StatusText = "Project download failed";
            _projectDownload?.Cancel();
            _projectDownload = null;
        }
    }

    private void HandleProjectDownloadMessage(JObject json)
    {
        var type = CollabProtocol.GetString(json, "t");
        switch (type)
        {
            case "projectBegin":
            {
                var name = SafeProjectFolderName(CollabProtocol.GetString(json, "project"));
                if (name == null)
                    throw new IOException($"host offered a project with an unusable folder name");
                if (UserSettings.Config.ProjectDirectories.Count == 0)
                    throw new IOException("no project directory is configured");

                _projectDownload?.Cancel();
                _projectDownloadFolder = Path.Combine(UserSettings.Config.ProjectDirectories[0], name);
                Directory.CreateDirectory(_projectDownloadFolder);
                _projectDownload = new BoygroupDeployer.FileReceiver();
                Log.Info($"Collab: receiving project '{name}' ({json["files"]?.Value<int>() ?? 0} files) into {_projectDownloadFolder}");
                break;
            }

            case "fileBegin":
                _projectDownload?.Begin(_projectDownloadFolder, json);
                break;

            case "fileChunk":
                _projectDownload?.Chunk(json);
                break;

            case "fileEnd":
            {
                var error = _projectDownload?.Finish(json);
                if (error != null)
                {
                    Log.Warning($"Collab: project download failed: {error}");
                    StatusText = "Project download failed";
                    _projectDownload?.Cancel();
                    _projectDownload = null;
                }

                break;
            }

            case "projectDone":
            {
                var name = CollabProtocol.GetString(json, "project");
                _projectDownload?.Cancel();
                _projectDownload = null;
                StatusText = $"Project '{name}' downloaded — restarting to load it...";
                Log.Info($"Collab: project '{name}' downloaded; restarting to load it (session will rejoin automatically)");
                _restartAtTicks = Environment.TickCount64 + 1500;
                break;
            }

            case "projectFail":
                _projectDownload?.Cancel();
                _projectDownload = null;
                ErrorText = CollabProtocol.GetString(json, "reason");
                StatusText = "Project download failed";
                break;
        }
    }

    /// <summary>
    /// Validates the host-supplied project name, which becomes one folder below the local project
    /// directory. A rooted path or a ".." segment would put the downloaded files anywhere this
    /// machine can write.
    /// </summary>
    private static string? SafeProjectFolderName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name is "." or ".." || name.Contains(".."))
            return null;

        if (name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(name))
            return null;

        // Windows would silently strip these (and resolve to a sibling target).
        if (OperatingSystem.IsWindows() && (name[^1] == '.' || name[^1] == ' '))
            return null;

        return name;
    }
}
