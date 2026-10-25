#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Core.Animation;
using T3.Core.Output;
using T3.Editor.Collab;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.OutputSetup;
using T3.Editor.SystemUi;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Show;

/// <summary>
/// Boygrouping: one TiXL instance is the master and shares its output setup and live show state with
/// render nodes, vvvv-style; each slave presents the outputs bound on that machine in its own
/// Output Setup → Local Bindings. Deployment is setup-based: slaves receive and apply the master's
/// setups, never its project files.
///
/// There is no manual address or password: every enabled instance announces itself on the LAN, the
/// master lists the discovered nodes, and adding one invites that node to connect. Threading follows
/// CollabManager — network threads only enqueue, every state change happens in <see cref="Update"/>.
/// </summary>
internal sealed class BoygroupManager
{
    public static BoygroupManager Instance { get; } = new();

    public enum BoygroupState
    {
        Off,
        WaitingForMaster,
        Server,
        Client,
    }

    public BoygroupState State { get; private set; } = BoygroupState.Off;
    public string StatusText { get; private set; } = "Boygrouping is off";
    public string ErrorText { get; private set; } = "";
    public string GroupName { get; private set; } = "";
    public string TimeFollowSummary { get; private set; } = "Time follow off";
    public string DeploySummary { get; private set; } = "";

    public sealed class ClientInfo
    {
        public required CollabPeerConnection Peer;
        public string Name = "";
        public string Address = "";
        public string Group = "";
        public string StatusText = "connected";
        public bool IsDeployed;

        /// <summary>The client's machine-local output bindings (outputs.machine.json), reported over the
        /// session so the master can see where each render node presents.</summary>
        public MachineConfig? MachineConfig;

        /// <summary>The client's displays, reported with its bindings. The master lists them under Local
        /// Bindings so an output can be routed to that machine's screen.</summary>
        public List<RemoteScreen> Screens = [];

        // Pending auth challenge (server side)
        public byte[]? PendingSalt;
        public byte[]? PendingNonce;
    }

    /// <summary>One display of a connected render node, as that machine enumerated it.</summary>
    public sealed record RemoteScreen(int Index, string Name, int Width, int Height);

    private readonly List<ClientInfo> _clients = [];
    public IReadOnlyList<ClientInfo> Clients => _clients;

    public sealed class DiscoveredServer
    {
        public string Name = "";
        public string Address = "";
        public int Port;
        public string Role = "";
        public List<string> Groups = [];
        public DateTime Seen = DateTime.UtcNow;

        public bool IsMaster => string.Equals(Role, "master", StringComparison.OrdinalIgnoreCase);
        public bool IsSlave => string.Equals(Role, "slave", StringComparison.OrdinalIgnoreCase);
    }

    private readonly List<DiscoveredServer> _discoveredServers = [];
    public IReadOnlyList<DiscoveredServer> DiscoveredServers => _discoveredServers;

    private LanDiscoveryScanner? _discoveryScanner;
    private LanDiscoveryResponder? _nodeResponder;
    private BoygroupTransport? _transport;
    private string _password = "";

    private long _nextHeartbeatTicks;
    private long _nextTimeSyncTicks;
    private long _autoEnableCheckTicks;

    private const string DefaultGroup = "Main";
    private readonly ConcurrentQueue<(string Host, int Port)> _pendingInvites = new();

    private BoygroupManager()
    {
    }

    // ---------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------

    /// <summary>The settings toggle: enabled instances are discoverable on the network.</summary>
    public void SetEnabled(bool enabled)
    {
        UserSettings.Config.BoygroupEnabled = enabled;
        UserSettings.Save();

        if (enabled)
            ApplyRole();
        else
            Leave();
    }

    /// <summary>Applies the configured role: master listens and scans, slave waits to be added.</summary>
    public void ApplyRole()
    {
        TearDownSession();
        ErrorText = "";

        if (UserSettings.Config.BoygroupRole == BoygroupRole.Master)
            StartMaster();
        else
            StartSlave();
    }

    private void StartMaster()
    {
        var config = UserSettings.Config;
        var port = config.BoygroupPort;
        GroupName = DefaultGroup;
        _serverProjectName = "";
        _serverProjectFolder = "";

        try
        {
            _transport = new BoygroupServerTransport(port, [DefaultGroup], config.NetworkInterface);
            _transport.PeerDisconnected += OnPeerDisconnected;
            _password = "";
            State = BoygroupState.Server;
            StatusText = $"Master on port {port} — waiting for render nodes";
            StartDiscovery();
            Log.Info($"Boygroup: master started on port {port}");
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not open port {port}: {e.Message}";
            _transport = null;
            State = BoygroupState.Off;
            StatusText = "Boygrouping failed";
        }
    }

    private void StartSlave()
    {
        GroupName = DefaultGroup;
        try
        {
            StartNodeResponder();
            State = BoygroupState.WaitingForMaster;
            StatusText = "Slave — waiting for a master to add this instance";
            Log.Info("Boygroup: slave enabled, waiting for a master");
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not listen for invitations on port {UserSettings.Config.BoygroupPort + LanDiscoveryResponder.DiscoveryPortOffset}: {e.Message}";
            State = BoygroupState.Off;
            StatusText = "Boygrouping failed";
        }
    }

    /// <summary>Announces this slave and receives master invitations on the discovery port.</summary>
    private void StartNodeResponder()
    {
        if (_nodeResponder != null)
            return;

        _nodeResponder = new LanDiscoveryResponder(UserSettings.Config.BoygroupPort + LanDiscoveryResponder.DiscoveryPortOffset,
                                                    () => new JObject
                                                              {
                                                                  ["boygroup"] = true,
                                                                  ["role"] = "slave",
                                                                  ["name"] = LocalNodeName,
                                                                  ["port"] = UserSettings.Config.BoygroupPort,
                                                              },
                                                    OnDiscoveryMessage);
    }

    private void StopNodeResponder()
    {
        _nodeResponder?.Dispose();
        _nodeResponder = null;
    }

    /// <summary>Responder thread: an invitation is handed to the UI thread, which connects.</summary>
    private void OnDiscoveryMessage(JObject json, IPEndPoint source)
    {
        if (json["t"]?.Value<string>() != "boygroup-invite")
            return;

        if (State is not (BoygroupState.WaitingForMaster or BoygroupState.Off))
            return;

        var port = json["port"]?.Value<int>() ?? UserSettings.Config.BoygroupPort;
        _pendingInvites.Enqueue((source.Address.ToString(), port));
    }

    /// <summary>Master action: ask a discovered node to connect to this master.</summary>
    public void InviteClient(DiscoveredServer node)
    {
        if (State != BoygroupState.Server)
            return;

        if (!IPAddress.TryParse(node.Address, out var address))
            return;

        try
        {
            using var udp = new UdpClient();
            var payload = CollabProtocol.EncodeJson(new JObject
                                                        {
                                                            ["t"] = "boygroup-invite",
                                                            ["port"] = UserSettings.Config.BoygroupPort,
                                                            ["project"] = ProjectView.Focused?.OpenedProject.Package.DisplayName ?? "",
                                                        });
            udp.Send(payload, payload.Length,
                     new IPEndPoint(address, node.Port + LanDiscoveryResponder.DiscoveryPortOffset));
            StatusText = $"Invited {node.Name} — waiting for it to connect";
            Log.Info($"Boygroup: invited '{node.Name}' at {node.Address}");
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not reach {node.Name}: {e.Message}";
        }
    }

    /// <summary>Slave action: connect to the master that invited this node.</summary>
    private void JoinMaster(string host, int port)
    {
        StopNodeResponder();
        ErrorText = "";
        GroupName = DefaultGroup;
        _serverProjectName = "";
        _serverProjectFolder = "";
        _lastBindingsSent = ""; // the master needs this machine's bindings
        DeploySummary = "";
        ResetClockSync();

        try
        {
            var address = ResolveHost(host);
            if (address == null)
                throw new ArgumentException($"'{host}' is not a valid address");

            var transport = BoygroupClientTransport.Connect(new IPEndPoint(address, port));
            _transport = transport;
            transport.PeerDisconnected += OnPeerDisconnected;
            State = BoygroupState.Client;
            StatusText = $"Connecting to master at {host}:{port}...";
            // Subscribe first, then start reading: a link that dies right now must report.
            transport.StartReading();
            SendJson(transport.ServerPeer,
                     new
                     {
                         t = "hello",
                         name = ClientDisplayName(),
                         group = DefaultGroup,
                     });
            _password = "";
        }
        catch (Exception e)
        {
            ErrorText = $"Boygroup connection failed: {e.Message}";
            _transport?.Dispose();
            _transport = null;
            ReturnToWaiting();
        }
    }

    /// <summary>Public entry used by the debug bridge and tests; the UI adds nodes by invitation.</summary>
    public void JoinServer(string host, int port, string group, string password)
    {
        JoinMaster(host, port);
    }

    /// <summary>Disables boygrouping and ends the current session.</summary>
    public void Leave()
    {
        TearDownSession();
        UserSettings.Config.BoygroupEnabled = false;
        UserSettings.Save();
        StatusText = "Boygrouping is off";
    }

    /// <summary>Releases everything a role/session owns without changing the enabled setting.</summary>
    private void TearDownSession()
    {
        if (_transport != null)
        {
            _transport.PeerDisconnected -= OnPeerDisconnected;
            _transport.Dispose();
            _transport = null;
        }

        StopDiscovery();
        StopNodeResponder();
        while (_pendingInvites.TryDequeue(out _))
        {
            // Invitations belong to the session that was just torn down.
        }

        _clients.Clear();
        while (_pendingDisconnects.TryDequeue(out _))
        {
            // Drop events from the dead session: replaying them into the next one would end it
            // immediately (the collab manager drains its queue for the same reason).
        }

        _password = "";
        GroupName = "";
        _setupPushPending = false;
        _serverProjectName = "";
        _serverProjectFolder = "";
        State = BoygroupState.Off;
        StatusText = "Boygrouping is off";
    }

    /// <summary>A slave whose master vanished stays enabled and discoverable for the next master.</summary>
    private void ReturnToWaiting()
    {
        TearDownSession();
        if (!UserSettings.Config.BoygroupEnabled || UserSettings.Config.BoygroupRole != BoygroupRole.Slave)
            return;

        try
        {
            StartNodeResponder();
            State = BoygroupState.WaitingForMaster;
            StatusText = "Master disconnected — waiting to be added again";
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not listen for invitations: {e.Message}";
        }
    }

    /// <summary>The name this machine is known by in the client list and its local output bindings.</summary>
    public static string LocalNodeName =>
        string.IsNullOrWhiteSpace(UserSettings.Config.BoygroupClientName) ? Environment.MachineName : UserSettings.Config.BoygroupClientName;

    private static string ClientDisplayName() => LocalNodeName;

    private static IPAddress? ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        try
        {
            return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Per-frame update (UI thread)
    // ---------------------------------------------------------------------------------------

    private const int MaxInboundPerFrame = 256;

    public void Update()
    {
        // Auto-enable shortly after startup (give the project time to load first).
        if (State == BoygroupState.Off)
        {
            if (_autoEnableCheckTicks == 0)
            {
                _autoEnableCheckTicks = Environment.TickCount64 + 5000;
            }
            else if (Environment.TickCount64 > _autoEnableCheckTicks && !_autoEnableAttempted)
            {
                _autoEnableAttempted = true;
                if (UserSettings.Config.BoygroupEnabled)
                    ApplyRole();
            }

            return;
        }

        while (_pendingInvites.TryDequeue(out var invite))
            JoinMaster(invite.Host, invite.Port);

        ProcessPendingDisconnects();

        // A disconnect or an inbound message can end the session, disposing the transport — the
        // remaining steps must not run against it.
        if (State == BoygroupState.Off)
            return;

        DrainInbound();

        if (State == BoygroupState.Off)
            return;

        PumpPendingSetupPush();
        SendHeartbeats();
        SendTimeSync();
        MaybeSendBindings();

        if (State == BoygroupState.Client)
        {
            SendClockPings();
            ApplyPendingAnchor();
            ApplyIntegratedTime();
        }

        if (State == BoygroupState.Server)
            RefreshDiscoveredServers();
    }

    private static bool _autoEnableAttempted;

    private void DrainInbound()
    {
        var processed = 0;
        // A message handler may end the session (server loss, restart), nulling the transport
        // mid-loop — the null-conditional keeps the loop condition safe on the next iteration.
        while (processed < MaxInboundPerFrame && _transport?.Inbound.TryDequeue(out var message) == true)
        {
            processed++;
            if (message.IsPresence || message.Json == null)
                continue;

            HandleMessage(message.Peer, message.Json);
        }
    }

    private readonly ConcurrentQueue<CollabPeerConnection> _pendingDisconnects = [];

    private void OnPeerDisconnected(CollabPeerConnection peer)
    {
        _pendingDisconnects.Enqueue(peer);
    }

    private void ProcessPendingDisconnects()
    {
        while (_pendingDisconnects.TryDequeue(out var peer))
        {
            var client = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
            if (client != null)
            {
                _clients.Remove(client);
                Log.Info($"Boygroup: client left: {client.Name}");
                UpdateMasterStatus();
                continue;
            }

            if (State == BoygroupState.Client)
            {
                ErrorText = "Disconnected from the boygroup master";
                ReturnToWaiting();
            }
        }
    }

    /// <summary>Keeps the status line in step with how many render nodes are actually connected.</summary>
    private void UpdateMasterStatus()
    {
        if (State != BoygroupState.Server)
            return;

        var count = _clients.Count(c => c.Peer.IsAuthenticated);
        StatusText = count == 0
                         ? $"Master on port {UserSettings.Config.BoygroupPort} — waiting for render nodes"
                         : $"{count} render node{(count == 1 ? "" : "s")} connected";
    }

    // ---------------------------------------------------------------------------------------
    // Messages
    // ---------------------------------------------------------------------------------------

    private void HandleMessage(CollabPeerConnection peer, JObject json)
    {
        var type = CollabProtocol.GetString(json, "t");
        switch (type)
        {
            case "hello" when State == BoygroupState.Server:
                HandleHello(peer, json);
                break;

            case "welcome":
                HandleWelcome(json);
                break;

            case "auth" when State == BoygroupState.Server:
                HandleAuth(peer, json);
                break;

            case "authOk":
                StatusText = "Joined group";
                _nextBindingsSendTicks = 0; // report the project's output bindings right away
                // A report attempted before the handshake completed was dropped by the server; the
                // file hasn't changed since, so clear the dedupe marker to send it again.
                _lastBindingsSent = "";
                TryOpenServerProject(json);
                break;

            case "authFail":
                ErrorText = "Rejected by server: " + CollabProtocol.GetString(json, "reason", "authentication failed");
                Leave();
                break;

            case "deployDone" when State == BoygroupState.Client:
                HandleDeployDone(json);
                break;

            case "setupJson" when State == BoygroupState.Client:
                WriteSetupFile(json);
                break;

            case "activeSetup" when State == BoygroupState.Client:
                ActivateSetup(json);
                break;

            case "snapshot" when State == BoygroupState.Client:
                if (json["op"] is JObject op)
                    CollabCommandWire.ApplyOp(op);

                break;

            case "op" when State == BoygroupState.Client:
                // Live edits from the master, encoded exactly like a collaboration op.
                if (json["op"] is JObject liveOp)
                    CollabCommandWire.ApplyOp(liveOp);

                break;

            case "bindings" when State == BoygroupState.Server:
                HandleClientBindings(peer, json);
                break;

            // NTP-style clock offset: the client measures the master's UTC clock so a play-at anchor
            // lands on the same instant everywhere, no matter how late its packet arrived.
            case "clockPing" when State == BoygroupState.Server:
                SendJson(peer, new
                               {
                                   t = "clockPong",
                                   id = json["id"]?.Value<long>() ?? 0,
                                   c1 = json["c1"]?.Value<long>() ?? 0,
                                   m2 = DateTime.UtcNow.Ticks,
                                   m3 = DateTime.UtcNow.Ticks,
                               });
                break;

            case "clockPong" when State == BoygroupState.Client:
                HandleClockPong(json);
                break;

            case "bindOutput" when State == BoygroupState.Client:
                HandleRemoteBinding(json, bind: true);
                break;

            case "unbindOutput" when State == BoygroupState.Client:
                HandleRemoteBinding(json, bind: false);
                break;

            case "play" when State == BoygroupState.Client:
                ApplyPlayControl(json);
                break;

            case "time" when State == BoygroupState.Client:
                ApplyTimeSync(json);
                break;

            default:
                Log.Debug($"Boygroup: ignoring message '{type}' in state {State}");
                break;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Server: setup broadcast
    // ---------------------------------------------------------------------------------------

    private bool _setupPushPending;
    private long _nextSetupPushTicks;

    /// <summary>
    /// Called after an output setup edit is saved. The server broadcasts it a moment later, so live
    /// setup edits reach the render nodes the same way collaboration edits do.
    /// </summary>
    public void OnSetupSaved()
    {
        if (State == BoygroupState.Server)
            _setupPushPending = true;
    }

    private void PumpPendingSetupPush()
    {
        if (!_setupPushPending
            || State != BoygroupState.Server
            || Environment.TickCount64 < _nextSetupPushTicks)
            return;

        _setupPushPending = false;
        _nextSetupPushTicks = Environment.TickCount64 + 150;
        PushSetupToGroup();
    }

    // ---------------------------------------------------------------------------------------
    // Server: handshake
    // ---------------------------------------------------------------------------------------

    private void HandleHello(CollabPeerConnection peer, JObject json)
    {
        // A hello from an unregistered connection is simply a new client: the transport accepts
        // peers on its own, so this is where the server first learns who connected.
        var client = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
        if (client == null)
        {
            client = new ClientInfo { Peer = peer };
            _clients.Add(client);
        }

        client.Name = CollabProtocol.GetString(json, "name", "client");
        client.Group = CollabProtocol.GetString(json, "group", GroupName);
        client.Address = (peer.Client?.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

        if (string.IsNullOrEmpty(_password))
        {
            CompleteAuth(client);
            return;
        }

        var salt = CollabAuth.CreateSalt();
        var nonce = CollabAuth.CreateNonce();
        client.PendingSalt = salt;
        client.PendingNonce = nonce;
        SendJson(peer, new
                           {
                               t = "welcome",
                               pwd = true,
                               salt = CollabAuth.ToHexString(salt),
                               nonce = CollabAuth.ToHexString(nonce),
                           });
    }

    private void HandleAuth(CollabPeerConnection peer, JObject json)
    {
        var client = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
        if (client?.PendingSalt == null)
        {
            Reject(peer, "no challenge pending");
            return;
        }

        if (!CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "mac"), out var mac)
            || mac.Length != CollabAuth.MacSizeBytes)
        {
            Reject(peer, "malformed authentication response");
            return;
        }

        var key = CollabAuth.DeriveKey(_password, client.PendingSalt);
        var expected = CollabAuth.ComputeMac(key, client.PendingNonce!);
        if (!CollabAuth.MacsEqual(expected, mac))
        {
            Reject(peer, "wrong password");
            return;
        }

        CompleteAuth(client);
    }

    private void CompleteAuth(ClientInfo client)
    {
        client.Peer.IsAuthenticated = true;
        SendJson(client.Peer,
                 new
                 {
                     t = "authOk",
                     serverName = Environment.MachineName,
                     // Only the name: clients open their own copy. No project files are transferred.
                     project = ProjectView.Focused?.OpenedProject.Package.DisplayName ?? "",
                 });
        Log.Info($"Boygroup: client authenticated: {client.Name} ({client.Address}) group '{client.Group}'");
        UpdateMasterStatus();

        // Late joiners get the current setups and graph without waiting for a deploy; after this they
        // receive live ops like everyone else.
        var setups = PushSetupToGroup();
        PushGraphSnapshotToGroup();
        SendJson(client.Peer, new { t = "deployDone", setups });
    }

    /// <summary>
    /// Opens (or focuses) the master's project locally if it is installed. Only the name crosses the
    /// wire — the setups need the same graph to bind their content to, but the project itself is
    /// never deployed. The folder is remembered so setup writes and bindings stay with that project
    /// even when another graph window takes the UI focus.
    /// </summary>
    private void TryOpenServerProject(JObject json)
    {
        _serverProjectName = CollabProtocol.GetString(json, "project");
        if (string.IsNullOrEmpty(_serverProjectName))
            return;

        _serverProjectFolder = CollabManager.FindProjectFolder(_serverProjectName) ?? "";
        Log.Debug($"Boygroup: server project '{_serverProjectName}' resolves to '{_serverProjectFolder}'");
        if (string.IsNullOrEmpty(_serverProjectFolder))
        {
            ErrorText = $"The master's project '{CollabManager.ShortProjectName(_serverProjectName)}' is not installed on this machine — "
                        + "the received output setups can't bind their content without it.";
            Log.Warning($"Boygroup: the server's project '{_serverProjectName}' is not installed here");
            return;
        }

        var focusedName = ProjectView.Focused?.OpenedProject.Package.DisplayName;
        if (string.Equals(focusedName, _serverProjectName, StringComparison.OrdinalIgnoreCase))
            return;

        if (CollabManager.TryOpenProjectByName(_serverProjectName))
            Log.Info($"Boygroup: opened project '{_serverProjectName}'");
        else
            Log.Warning($"Boygroup: project '{_serverProjectName}' is installed but could not be opened");
    }

    private string _serverProjectName = "";
    private string _serverProjectFolder = "";

    /// <summary>
    /// The project received setups and bindings belong to: the master's project on a client, the
    /// focused project otherwise. Deliberately independent of UI focus, so a second graph window
    /// can't redirect a deployment into another project.
    /// </summary>
    private string? GetSetupTargetFolder()
    {
        if (State == BoygroupState.Client && !string.IsNullOrEmpty(_serverProjectName))
            return string.IsNullOrEmpty(_serverProjectFolder) ? null : _serverProjectFolder;

        return GetProjectFolder();
    }

    /// <summary>The client's machine-local output bindings, so the master can see where each node presents.</summary>
    private void HandleClientBindings(CollabPeerConnection peer, JObject json)
    {
        var client = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
        if (client == null || !peer.IsAuthenticated)
            return;

        var configJson = json["config"]?.Value<string>();
        if (string.IsNullOrEmpty(configJson))
            return;

        try
        {
            client.MachineConfig = MachineConfig.ReadFromJson(JObject.Parse(configJson));
            client.Screens = ReadScreens(json["screens"]);
            Log.Info($"Boygroup: bindings from {client.Name}: {DescribeBindings(client.MachineConfig, ActiveSetup.Current)}");
            UpdateMasterStatus(); // clears a "Binding ..." message once the node applied it
        }
        catch (JsonException e)
        {
            Log.Warning($"Boygroup: malformed bindings from {client.Name}: {e.Message}");
        }
    }

    private static List<RemoteScreen> ReadScreens(JToken? token)
    {
        var screens = new List<RemoteScreen>();
        if (token is not JArray array)
            return screens;

        foreach (var screen in array)
        {
            var index = screen["index"]?.Value<int>() ?? screens.Count;
            screens.Add(new RemoteScreen(index,
                                         screen.Value<string>("name") ?? $"Display {index + 1}",
                                         screen["width"]?.Value<int>() ?? 0,
                                         screen["height"]?.Value<int>() ?? 0));
        }

        return screens;
    }

    /// <summary>The master's list of a client's displays; the id is stable across reconnects and frames.</summary>
    public static Guid RemoteDisplayPlugId(ClientInfo client, int displayIndex)
    {
        var key = $"boygroup:{client.Address}:{client.Name}:{displayIndex}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Resolves an outliner/board plug id back to the render node display it stands for.</summary>
    public bool TryGetRemoteDisplayPlug(Guid plugId, out ClientInfo client, out int displayIndex)
    {
        foreach (var candidate in _clients)
        {
            foreach (var screen in candidate.Screens)
            {
                if (RemoteDisplayPlugId(candidate, screen.Index) != plugId)
                    continue;

                client = candidate;
                displayIndex = screen.Index;
                return true;
            }
        }

        client = null!;
        displayIndex = -1;
        return false;
    }

    /// <summary>Master side: asks a render node to present an output on one of its own displays.</summary>
    public void BindRemoteOutput(ClientInfo client, Guid outputId, int displayIndex)
    {
        if (State != BoygroupState.Server || !client.Peer.IsAuthenticated)
            return;

        SendJson(client.Peer, new { t = "bindOutput", output = outputId.ToString(), display = displayIndex });
        StatusText = $"Binding {client.Name}'s Display {displayIndex + 1}...";
    }

    public void UnbindRemoteOutput(ClientInfo client, Guid outputId, int displayIndex)
    {
        if (State != BoygroupState.Server || !client.Peer.IsAuthenticated)
            return;

        SendJson(client.Peer, new { t = "unbindOutput", output = outputId.ToString(), display = displayIndex });
    }

    /// <summary>Output → plug pairs from one machine's config, resolved against the active setup's output names.</summary>
    public static string DescribeBindings(MachineConfig? machineConfig, Setup? setup)
    {
        if (machineConfig == null || machineConfig.Bindings.Count == 0)
            return "no display bindings";

        var parts = new List<string>();
        foreach (var binding in machineConfig.Bindings)
        {
            var outputName = setup?.FindOutput(binding.OutputId)?.Name ?? "?";
            var plug = binding.IsLaser
                           ? machineConfig.FindLaserPlug(binding.PlugId)?.Name ?? "missing laser"
                           : binding.IsStream
                               ? machineConfig.FindStreamPlug(binding.PlugId)?.Name ?? "missing stream"
                               : $"Display {binding.DisplayIndex + 1}";
            parts.Add($"{outputName} → {plug}");
        }

        return string.Join(" · ", parts);
    }

    private long _nextBindingsSendTicks;
    private string _lastBindingsSent = "";

    /// <summary>Client side: sends the project's machine config when it changed (every couple of seconds).</summary>
    private void MaybeSendBindings()
    {
        if (State != BoygroupState.Client)
            return;

        var now = Environment.TickCount64;
        if (now < _nextBindingsSendTicks)
            return;

        _nextBindingsSendTicks = now + 2000;

        var projectFolder = GetSetupTargetFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        var path = Path.Combine(SetupFiles.FolderIn(projectFolder), MachineConfig.FileName);
        try
        {
            // The master also needs this machine's displays before anything is bound, and a fresh
            // client may not have written a machine config yet.
            var json = File.Exists(path) ? File.ReadAllText(path) : new MachineConfig().ToJsonString();
            var screens = DescribeScreens();
            var key = json + "|" + screens.ToString(Formatting.None);
            if (key == _lastBindingsSent)
                return;

            _lastBindingsSent = key;
            Log.Debug($"Boygroup: reporting output bindings from '{path}'");
            if (_transport is BoygroupClientTransport clientTransport)
                SendJson(clientTransport.ServerPeer, new { t = "bindings", config = json, screens });
        }
        catch (IOException e)
        {
            Log.Debug($"Boygroup: can't read machine bindings: {e.Message}");
        }
    }

    /// <summary>This machine's displays, sent so a master can offer them as binding targets.</summary>
    private static JArray DescribeScreens()
    {
        var result = new JArray();
        var screens = EditorUi.Instance.AllScreens;
        for (var i = 0; i < screens.Count; i++)
        {
            result.Add(new JObject
                           {
                               ["index"] = i,
                               ["name"] = screens[i].DeviceName,
                               ["width"] = screens[i].Bounds.Width,
                               ["height"] = screens[i].Bounds.Height,
                           });
        }

        return result;
    }

    /// <summary>Client side: applies a binding the master routed to one of this machine's displays.</summary>
    private void HandleRemoteBinding(JObject json, bool bind)
    {
        if (!Guid.TryParse(CollabProtocol.GetString(json, "output"), out var outputId))
            return;

        var displayIndex = json["display"]?.Value<int>() ?? -1;
        if (displayIndex < 0 || displayIndex >= EditorUi.Instance.AllScreens.Count)
            return;

        if (!OutputSetupHandling.TryGetActiveSetup(out _, out var machineConfig))
            return;

        var plugId = Plugs.DisplayPlugId(displayIndex);
        if (bind)
            Plugs.BindOutput(machineConfig, outputId, plugId);
        else
            Plugs.UnbindOutput(machineConfig, outputId, plugId);

        // Report right away: the master's binding column is driven by what this machine says it presents.
        _lastBindingsSent = "";
        _nextBindingsSendTicks = 0;
        MaybeSendBindings();
    }

    /// <summary>
    /// Server side: every local graph edit is broadcast as a collaboration op, so clients follow the
    /// master live after a deployment (the same wire format the collaboration session uses).
    /// </summary>
    public void OnLocalCommandAdded(ICommand command)
    {
        if (State != BoygroupState.Server || _clients.Count == 0)
            return;

        var op = CollabCommandWire.EncodeCommand(command);
        if (op == null)
            return;

        BroadcastToGroup(new { t = "op", op });
    }

    private void Reject(CollabPeerConnection peer, string reason)
    {
        Log.Info($"Boygroup: rejected client: {reason}");
        SendJson(peer, new { t = "authFail", reason });
        peer.SessionClosed = true;
        peer.Close();
    }

    // ---------------------------------------------------------------------------------------
    // Client: handshake
    // ---------------------------------------------------------------------------------------

    private byte[]? _pendingSalt;
    private byte[]? _pendingNonce;

    private void HandleWelcome(JObject json)
    {
        if (_transport is not BoygroupClientTransport client)
            return;

        var passwordRequired = json["pwd"]?.Value<bool>() ?? false;
        if (!passwordRequired)
            return;

        if (!CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "salt"), out var salt)
            || !CollabAuth.TryFromHexString(CollabProtocol.GetString(json, "nonce"), out var nonce))
        {
            ErrorText = "Server sent a malformed challenge";
            Leave();
            return;
        }

        _pendingSalt = salt;
        _pendingNonce = nonce;
        var key = CollabAuth.DeriveKey(_password, salt);
        var mac = CollabAuth.ComputeMac(key, nonce);
        SendJson(client.ServerPeer, new { t = "auth", mac = CollabAuth.ToHexString(mac) });
    }

    // ---------------------------------------------------------------------------------------
    // Deploy & Sync (server side)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Server action: share the output setup with the group. The project itself is not transferred —
    /// a client runs its own copy and only receives the setups, so deployment no longer restarts it.
    /// </summary>
    public void DeployAndSync()
    {
        if (State != BoygroupState.Server)
        {
            ErrorText = "Only the boygroup server can deploy";
            return;
        }

        if (ProjectView.Focused == null)
        {
            ErrorText = "Open a project with an output setup before deploying";
            return;
        }

        if (!OutputSetupHandling.TryGetActiveSetup(out _, out _))
        {
            ErrorText = "No output setup is active — create one in the Output Setup window before deploying";
            return;
        }

        // Persist pending setup edits first: what clients receive must be what the master sees.
        OutputSetupHandling.SaveActive();
        _setupPushPending = false; // This deploy sends what SaveActive just wrote.

        var setups = PushSetupToGroup();
        PushGraphSnapshotToGroup();
        BroadcastToGroup(new { t = "deployDone", setups });

        foreach (var client in _clients.Where(c => c.Peer.IsAuthenticated && c.Group == GroupName).ToList())
        {
            client.IsDeployed = true;
            client.StatusText = setups == 0 ? "no setup files" : $"setup synced ({setups})";
        }

        DeploySummary = $"Sent {setups} output setup file(s)";
        Log.Info($"Boygroup: sent {setups} output setup file(s) to group '{GroupName}'");
    }

    /// <summary>
    /// Broadcasts every setup file and then the active one. Returns how many setup files were sent;
    /// no project files are involved. Called on deploy, on a late join, and after every setup save.
    /// </summary>
    private int PushSetupToGroup()
    {
        if (State != BoygroupState.Server)
            return 0;

        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return 0;

        var setupsFolder = SetupFiles.FolderIn(projectFolder);
        if (!Directory.Exists(setupsFolder))
            return 0;

        var count = 0;
        foreach (var filePath in Directory.EnumerateFiles(setupsFolder, "*" + Setup.FileSuffix))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var name = Path.GetFileName(filePath);
                BroadcastToGroup(new { t = "setupJson", name, json });
                count++;
            }
            catch (IOException e)
            {
                Log.Warning($"Boygroup: can't read setup {filePath}: {e.Message}");
            }
        }

        // After the files, so the client can switch to it without reading a stale copy.
        var activeName = ActiveSetup.Current?.Name;
        if (!string.IsNullOrEmpty(activeName))
            BroadcastToGroup(new { t = "activeSetup", name = activeName });

        return count;
    }

    private void PushGraphSnapshotToGroup()
    {
        var focused = ProjectView.Focused;
        var compositionUi = focused?.InstView?.Symbol.GetSymbolUi();
        if (compositionUi == null)
        {
            Log.Info("Boygroup: no open composition to snapshot");
            return;
        }

        var snapshotOp = CollabCommandWire.BuildSnapshotOp(compositionUi);
        BroadcastToGroup(new { t = "snapshot", op = snapshotOp });
    }

    private static string? GetProjectFolder()
    {
        return ProjectView.Focused?.OpenedProject.Package.Folder;
    }

    // ---------------------------------------------------------------------------------------
    // Client: setup reception
    // ---------------------------------------------------------------------------------------

    private void WriteSetupFile(JObject json)
    {
        var projectFolder = GetSetupTargetFolder();
        if (string.IsNullOrEmpty(projectFolder))
        {
            ErrorText = "No project open — can't apply the master's output setup";
            return;
        }

        try
        {
            var name = json["name"]?.Value<string>() ?? "";
            if (name.Contains("..") || !name.EndsWith(Setup.FileSuffix, StringComparison.Ordinal))
                throw new IOException($"unsafe setup file name '{name}'");

            var setupsFolder = SetupFiles.FolderIn(projectFolder);
            Directory.CreateDirectory(setupsFolder);
            var fullPath = Path.Combine(setupsFolder, name);
            File.WriteAllText(fullPath, json["json"]?.Value<string>() ?? "");

            // The setup cache is per project and would otherwise keep presenting the old file.
            OutputSetupHandling.OnSetupFilesChanged(projectFolder);
            Log.Info($"Boygroup: setup updated: {name}");
        }
        catch (Exception e)
        {
            Log.Warning($"Boygroup: can't write setup: {e.Message}");
        }
    }

    /// <summary>Switches the client to the setup the master is presenting.</summary>
    private void ActivateSetup(JObject json)
    {
        var name = json["name"]?.Value<string>() ?? "";
        if (string.IsNullOrEmpty(name))
            return;

        var projectFolder = GetSetupTargetFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        if (OutputSetupHandling.TryApplySetupFile(projectFolder, name))
        {
            DeploySummary = $"Using setup '{name}'";
        }
        else
        {
            // The file may have arrived with the same batch but switch failed (unreadable), so
            // reload whatever is there instead of keeping stale state.
            OutputSetupHandling.OnSetupFilesChanged(projectFolder);
            Log.Warning($"Boygroup: can't switch to setup '{name}'");
        }
    }

    private void HandleDeployDone(JObject json)
    {
        var setups = json["setups"]?.Value<int>() ?? 0;
        DeploySummary = setups == 0
                            ? "No output setup received"
                            : $"Output setup synced ({setups} file{(setups == 1 ? "" : "s")})";
    }

    // ---------------------------------------------------------------------------------------
    // Playback control and time sync
    // ---------------------------------------------------------------------------------------

    public void SendPlayControl(bool play)
    {
        if (State != BoygroupState.Server)
            return;

        // "Play all" includes the master: otherwise its anchors keep reporting a stopped timeline
        // and the clients pause again right after starting.
        if (Playback.Current != null)
            Playback.Current.PlaybackSpeed = play ? 1.0 : 0.0;

        BroadcastToGroup(new { t = "play", playing = play, bars = Playback.Current?.TimeInBars ?? 0 });
        SendTimeSyncNow();
    }

    private void ApplyPlayControl(JObject json)
    {
        var playing = json["playing"]?.Value<bool>() ?? false;
        if (Playback.Current != null)
            Playback.Current.PlaybackSpeed = playing ? 1.0 : 0.0;
    }

    public void SendTimeSyncNow()
    {
        _nextTimeSyncTicks = 0; // Next Update() sends immediately.
    }

    private void SendHeartbeats()
    {
        var now = Environment.TickCount64;
        if (now < _nextHeartbeatTicks)
            return;

        _nextHeartbeatTicks = now + CollabProtocol.HeartbeatIntervalMs;

        if (_transport is BoygroupClientTransport clientTransport)
        {
            // The server pings every client every second, so silence means the master is gone
            // (or partitioned). Without this check the client kept dead-reckoning time forever
            // from the last sync and reported itself as connected.
            var serverPeer = clientTransport.ServerPeer;
            if (serverPeer.IsTimedOut)
            {
                Log.Info("Boygroup: server timed out");
                serverPeer.SessionClosed = true;
                serverPeer.Close();
            }

            return;
        }

        var timestamp = BitConverter.GetBytes(Environment.TickCount64);
        foreach (var client in _clients)
        {
            if (client.Peer.IsTimedOut)
            {
                Log.Info($"Boygroup: client {client.Name} timed out");
                client.Peer.SessionClosed = true;
                client.Peer.Close();
                continue;
            }

            client.Peer.Write(CollabProtocol.FrameType.Heartbeat, timestamp);
        }
    }

    private void SendTimeSync()
    {
        if (State != BoygroupState.Server)
            return;

        var now = Environment.TickCount64;
        if (now < _nextTimeSyncTicks)
            return;

        _nextTimeSyncTicks = now + 100; // 10 Hz
        var playback = Playback.Current;
        if (playback == null)
            return;

        // Play-at anchor: the bars the master will have at an instant slightly in the future, sent
        // with that instant on the master's UTC clock. Clients convert it with their measured clock
        // offset and switch when their own monotonic clock reaches it, so a late or jittery packet
        // does not shift the phase.
        var lead = TimeSpan.FromMilliseconds(AnchorLeadMs);
        var anchorBars = playback.TimeInBars + playback.PlaybackSpeed * lead.TotalSeconds * playback.Bpm / 240.0;
        BroadcastToGroup(new
                             {
                                 t = "time",
                                 bars = anchorBars,
                                 speed = playback.PlaybackSpeed,
                                 bpm = playback.Bpm,
                                 at = (DateTime.UtcNow + lead).Ticks,
                             });
    }

    /// <summary>How far ahead a play-at anchor is scheduled: enough for LAN jitter, shorter than the send cadence.</summary>
    private const int AnchorLeadMs = 80;

    // Clock offset (master UTC - local UTC), taken from the sample with the least round trip.
    private long _clockOffsetTicks;
    private long _clockBestRttTicks = long.MaxValue;
    private long _clockBestSampleUtc;
    private long _nextClockPingTicks;
    private long _clockPingId;
    private int _clockSamples;

    private void SendClockPings()
    {
        if (State != BoygroupState.Client)
            return;

        var now = Environment.TickCount64;
        if (now < _nextClockPingTicks)
            return;

        // A burst establishes the offset quickly; after that a slow refresh tracks clock drift.
        _nextClockPingTicks = now + (_clockSamples < 8 ? 1000 : 10_000);
        if (_transport is not BoygroupClientTransport clientTransport)
            return;

        SendJson(clientTransport.ServerPeer,
                 new
                 {
                     t = "clockPing",
                     id = ++_clockPingId,
                     c1 = DateTime.UtcNow.Ticks,
                 });
    }

    private void HandleClockPong(JObject json)
    {
        if (json["id"]?.Value<long>() != _clockPingId)
            return;

        var c1 = json["c1"]?.Value<long>() ?? 0;
        var m2 = json["m2"]?.Value<long>() ?? 0;
        var m3 = json["m3"]?.Value<long>() ?? 0;
        var c4 = DateTime.UtcNow.Ticks;
        if (c1 == 0 || m2 == 0 || m3 == 0)
            return;

        var roundTrip = (c4 - c1) - (m3 - m2);
        if (roundTrip < 0)
            return;

        // NTP offset: the master's clock minus this machine's, at the middle of the round trip.
        var offset = ((m2 - c1) + (m3 - c4)) / 2;
        _clockSamples++;

        // The least-queueing sample is the most symmetric. A sample older than a minute is replaced
        // even when it is not better, so a clock that was adjusted does not pin the offset forever.
        if (roundTrip <= _clockBestRttTicks || c4 - _clockBestSampleUtc > TimeSpan.TicksPerMinute)
        {
            _clockOffsetTicks = offset;
            _clockBestRttTicks = roundTrip;
            _clockBestSampleUtc = c4;
        }
    }

    private void ResetClockSync()
    {
        _clockOffsetTicks = 0;
        _clockBestRttTicks = long.MaxValue;
        _clockBestSampleUtc = 0;
        _clockSamples = 0;
        _nextClockPingTicks = 0;
        _hasAnchor = false;
        _lastSyncStopwatch = -1;
    }

    private bool _hasAnchor;
    private double _anchorLocalStopwatch = -1;
    private double _anchorBars;
    private double _anchorSpeed;
    private double _anchorBpm = 120;

    /// <summary>Client side: converts the master's play-at instant into this machine's monotonic clock.</summary>
    private void ApplyTimeSync(JObject json)
    {
        var playback = Playback.Current;
        var masterUtc = json["at"]?.Value<long>() ?? 0;
        if (playback == null || masterUtc == 0)
            return;

        var localUtc = masterUtc - _clockOffsetTicks;
        var deltaSecs = (localUtc - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerSecond;
        _anchorLocalStopwatch = Stopwatch.GetTimestamp() + deltaSecs * Stopwatch.Frequency;
        _anchorBars = json["bars"]?.Value<double>() ?? 0;
        _anchorSpeed = json["speed"]?.Value<double>() ?? 0;
        _anchorBpm = json["bpm"]?.Value<double>() ?? 120;
        _hasAnchor = true;
    }

    /// <summary>Client side: switches to the anchor once its instant arrives, then dead-reckons from there.</summary>
    private void ApplyPendingAnchor()
    {
        if (!_hasAnchor || Stopwatch.GetTimestamp() < _anchorLocalStopwatch)
            return;

        var playback = Playback.Current;
        if (playback == null)
            return;

        // A late anchor still carries the right instant: advance its bars by the lateness rather than
        // snapping back to where the master was when it sent the packet.
        var lateSecs = Math.Max(0, (Stopwatch.GetTimestamp() - _anchorLocalStopwatch) / Stopwatch.Frequency);
        _lastSyncStopwatch = Stopwatch.GetTimestamp();
        _lastSyncBars = _anchorBars + _anchorSpeed * lateSecs * _anchorBpm / 240.0;
        _lastSyncSpeed = _anchorSpeed;
        _lastSyncBpm = _anchorBpm;

        playback.PlaybackSpeed = _anchorSpeed;
        playback.Bpm = _anchorBpm;
        ApplyIntegratedTime();

        _hasAnchor = false;
        TimeFollowSummary = _anchorSpeed != 0
                                ? $"Following master: {_lastSyncBars:0.00} bars @ {_anchorBpm:0} bpm"
                                : "Following master (paused)";
    }

    private double _lastSyncStopwatch = -1;
    private double _lastSyncBars;
    private double _lastSyncSpeed;
    private double _lastSyncBpm = 120;

    /// <summary>Dead-reckons between master anchors so the time cursor moves every frame, not in 10 Hz steps.</summary>
    private void ApplyIntegratedTime()
    {
        var playback = Playback.Current;
        if (playback == null || _lastSyncStopwatch < 0)
            return;

        // Monotonic host clock: wall time could jump under an NTP correction.
        var elapsedSecs = (Stopwatch.GetTimestamp() - _lastSyncStopwatch) / Stopwatch.Frequency;
        playback.TimeInBars = _lastSyncBars + _lastSyncSpeed * elapsedSecs * _lastSyncBpm / 240.0;
    }

    // ---------------------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------------------

    /// <summary>Starts continuous discovery of the TiXL instances on the network (master only).</summary>
    public void StartDiscovery()
    {
        _discoveryScanner ??= new LanDiscoveryScanner();
        _discoveredServers.Clear();
        _discoveryScanner.Start(UserSettings.Config.BoygroupPort + LanDiscoveryResponder.DiscoveryPortOffset,
                                UserSettings.Config.NetworkInterface);
    }

    public void StopDiscovery()
    {
        _discoveryScanner?.Stop();
    }

    /// <summary>Moves scan results into the visible list; called from Update.</summary>
    public void DrainDiscovery()
    {
        if (_discoveryScanner == null)
            return;

        while (_discoveryScanner.Results.TryDequeue(out var result))
        {
            var announcement = result.Announcement;
            if (announcement["boygroup"] == null)
                continue;

            var node = new DiscoveredServer
                           {
                               Name = CollabProtocol.GetString(announcement, "name",
                                                               CollabProtocol.GetString(announcement, "server", "TiXL")),
                               Address = result.Source.Address.ToString(),
                               Port = announcement["port"]?.Value<int>() ?? UserSettings.Config.BoygroupPort,
                               Role = CollabProtocol.GetString(announcement, "role", "master"),
                           };

            // The master's own announcement comes back through the broadcast.
            if (node.IsMaster && string.Equals(node.Name, LocalNodeName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (announcement["groups"] is JArray groups)
            {
                foreach (var group in groups)
                {
                    var name = group.Value<string>();
                    if (!string.IsNullOrEmpty(name))
                        node.Groups.Add(name);
                }
            }

            var existing = _discoveredServers.FirstOrDefault(d => d.Address == node.Address && d.Port == node.Port);
            if (existing != null)
            {
                existing.Name = node.Name;
                existing.Role = node.Role;
                existing.Seen = DateTime.UtcNow;
                continue;
            }

            _discoveredServers.Add(node);
        }

        _discoveredServers.RemoveAll(d => d.Seen < DateTime.UtcNow - TimeSpan.FromSeconds(15));
    }

    private void RefreshDiscoveredServers() => DrainDiscovery();

    // ---------------------------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------------------------

    private void SendJson(CollabPeerConnection peer, object message) => _transport?.SendJson(peer, message);

    private void BroadcastToGroup(object message)
    {
        if (_transport == null)
            return;

        var payload = CollabProtocol.EncodeJson(message);
        foreach (var client in _clients)
        {
            if (client.Peer.IsAuthenticated && client.Group == GroupName)
                client.Peer.Write(CollabProtocol.FrameType.Json, payload);
        }
    }
}
