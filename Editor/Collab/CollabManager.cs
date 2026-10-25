#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.UiHelpers;
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

    public IReadOnlyList<DiscoveredSession> DiscoveredSessions => _discovered;

    private CollabUserIdentity _localIdentity = new();

    private CollabTransport _transport;
    private LanDiscoveryScanner _discoveryScanner;
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

    public void StartHost(int tcpPort, string password, bool enableLanDiscovery, string? projectName = null)
    {
        if (IsSessionActive)
            Leave();

        ErrorText = "";
        try
        {
            _password = password ?? "";
            _sessionLog.Clear();
            var hostName = TruncateName(_localIdentity.DisplayName);
            _transport = new CollabHostTransport(tcpPort,
                                                 enableLanDiscovery,
                                                 () => new JObject
                                                           {
                                                               ["collab"] = true,
                                                               ["name"] = hostName,
                                                               ["host"] = Environment.MachineName,
                                                               ["port"] = tcpPort,
                                                               ["pwd"] = !string.IsNullOrEmpty(_password),
                                                               ["project"] = projectName
                                                                             ?? ProjectView.Focused?.OpenedProject.Package.DisplayName
                                                                             ?? "",
                                                           });
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
        }
    }

    /// <summary>
    /// Hosts through a relay so participants behind NATs can join by room code without port
    /// forwarding. Runs async: state goes Connecting until the relay confirms the room.
    /// </summary>
    public void StartHostViaRelay(string relayHost, int relayPort)
    {
        if (IsSessionActive)
            Leave();

        ErrorText = "";
        State = SessionState.Connecting;
        StatusText = $"Registering room at {relayHost}:{relayPort}...";

        _ = Task.Run(async () =>
                {
                    try
                    {
                        _password = UserSettings.Config.CollabPassword ?? "";
                        _sessionLog.Clear();
                        var hostName = TruncateName(_localIdentity.DisplayName);
                        var transport = await CollabRelayHostTransport.ConnectAsync(relayHost, relayPort, "", CancellationToken.None);
                        transport.PeerDisconnected += OnPeerDisconnectedTransport;
                        _transport = transport;
                        RoomCode = transport.AssignedRoomCode;
                        State = SessionState.Hosting;
                        StatusText = $"Hosting via relay, room {RoomCode}";
                        Log.Info($"Collab: hosting via relay {relayHost}:{relayPort}, room {RoomCode}");
                    }
                    catch (Exception e)
                    {
                        ErrorText = $"Relay hosting failed: {e.Message}";
                        StatusText = "Failed to host";
                        State = SessionState.Off;
                        Log.Warning($"Collab: relay hosting failed: {e.Message}");
                    }
                });
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

        _ = Task.Run(async () =>
                {
                    try
                    {
                        _password = password ?? "";
                        _sessionLog.Clear();
                        var transport = await CollabRelayClientTransport.ConnectAsync(relayHost, relayPort, roomCode, CancellationToken.None);
                        _transport = transport;
                        _transport.PeerDisconnected += OnPeerDisconnectedTransport;
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
                    catch (Exception e)
                    {
                        ErrorText = $"Relay join failed: {e.Message}";
                        StatusText = "Connection failed";
                        State = SessionState.Off;
                        Log.Warning($"Collab: relay join failed: {e.Message}");
                    }
                });
    }

    public void JoinDirect(string hostAddress, int tcpPort, string password)
    {
        if (IsSessionActive)
            Leave();

        ErrorText = "";
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
            _localAwaitedAuth = new PendingHandshake
                                    {
                                        Salt = null,
                                        Nonce = null,
                                    };
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

    private static IPAddress ResolveHostName(string host)
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
        if (_transport != null)
        {
            _transport.PeerDisconnected -= OnPeerDisconnectedTransport;
            _transport.Dispose();
            _transport = null;
        }

        _sessionLog.Clear();
        _opsReceivedThisSecond.Clear();
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
    public CollabPeerDisplay GetPresence(int peerId)
    {
        PresencePacket presence = null;
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

        SendHeartbeatsAndPrune();
        SendLocalPresence();
        RefreshDiscoveredSessions();
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
            DropLocksOwnedBy(peer.AssignedId);

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

            if (message.IsPresence)
                HandlePresence(message.Peer, message.Presence);
            else
                HandleJson(message.Peer, message.Json);
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
                _transport.SendToPeer(other, CollabProtocol.FrameType.Presence, buffer.AsSpan(0, size));
        }
    }

    private void HandleJson(CollabPeerConnection peer, JObject json)
    {
        var type = CollabProtocol.GetString(json, "t");
        switch (type)
        {
            case "hello" when State == SessionState.Hosting:
                HandleHostHello(peer, json);
                break;

            case "welcome":
                HandleClientWelcome(json);
                break;

            case "auth" when State == SessionState.Hosting:
                HandleHostAuth(peer, json);
                break;

            case "authOk":
                HandleClientAuthOk(peer, json);
                break;

            case "authFail":
                ErrorText = "Rejected by host: " + CollabProtocol.GetString(json, "reason", "authentication failed");
                Leave();
                break;

            case "op" when State == SessionState.Connected:
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

            case "syncStart":
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

            case "syncEnd":
                Log.Info($"Collab: replay complete ({_syncBatch} ops)");
                break;

            case "peerJoined":
            {
                var name = CollabProtocol.GetString(json, "name", "?");
                if (CollabProtocol.TryGetInt(json, "id", out var joinedId))
                {
                    _roster[joinedId] = name;
                    if (CollabProtocol.TryGetGuid(json, "userId", out var joinedUserId))
                        _rosterUserIds[joinedId] = joinedUserId;
                }

                Log.Info($"Collab: {name} joined");
                StatusText = State == SessionState.Hosting
                                 ? $"Hosting{(!string.IsNullOrEmpty(RoomCode) ? $" (room {RoomCode})" : "")}, {_roster.Count} participant(s)"
                                 : StatusText;
                break;
            }

            case "peerLeft":
            {
                var name = CollabProtocol.GetString(json, "name", "?");
                if (CollabProtocol.TryGetInt(json, "id", out var leftId))
                    _roster.Remove(leftId);

                Log.Info($"Collab: {name} left");
                break;
            }

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
        public byte[] Salt;
        public byte[] Nonce;
    }

    private PendingHandshake _localAwaitedAuth;

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
        _localAwaitedAuth = new PendingHandshake { Salt = salt, Nonce = nonce };
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
        var pending = _localAwaitedAuth;
        if (pending?.Salt == null)
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
        _localAwaitedAuth = null;
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
        SendJson(peer, new { t = "authFail", reason });
        peer.SessionClosed = true;
        peer.Close();
    }

    private void HandleClientWelcome(JObject json)
    {
        if (State != SessionState.Connecting || _transport is not CollabClientTransport client)
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
        SendJson(client.HostPeer, new { t = "auth", mac = CollabAuth.ToHexString(mac) });
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
        else if (State == SessionState.Connected && _transport is CollabClientTransport client)
        {
            SendJson(client.HostPeer, envelope);
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

        if (State == SessionState.Connected && _transport is CollabClientTransport client)
        {
            SendJson(client.HostPeer, new { t = "undoRequest" });
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

        if (State == SessionState.Connected && _transport is CollabClientTransport client)
        {
            SendJson(client.HostPeer, new { t = "redoRequest" });
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
        var ownerPeerId = acquire ? peer.AssignedId : json["id"]?.Value<int>() ?? peer.AssignedId;

        if (acquire)
        {
            _activeLocks[key] = ownerPeerId;
        }
        else
        {
            // Only the holder (or the host on their behalf) may clear a lock.
            if (_activeLocks.TryGetValue(key, out var currentOwner) && currentOwner == ownerPeerId)
                _activeLocks.Remove(key);
        }

        if (State == SessionState.Hosting)
        {
            var envelope = (JObject)json.DeepClone();
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
                _transport.SendToPeer(peer, CollabProtocol.FrameType.Heartbeat, timestamp);
        }
    }

    public void StartDiscovery()
    {
        _discoveryScanner ??= new LanDiscoveryScanner();
        _discovered.Clear();
        _discoveryScanner.Start(DefaultDiscoveryPort);
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
            _hubScanner.Start(DefaultDiscoveryPort); // re-probe; Start restarts its loop cleanly
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

    private void BroadcastSystemMessage(object message, CollabPeerConnection except) => _transport?.BroadcastJson(Peers, message, except);

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
}
