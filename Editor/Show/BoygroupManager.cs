#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NAudio.Midi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rug.Osc;
using T3.Core.Animation;
using T3.Core.IO;
using T3.Core.Output;
using T3.Editor.Collab;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.ProjectHandling;
using T3.IoServices;

namespace T3.Editor.Show;

/// <summary>
/// Boygrouping: one TiXL instance (server) distributes a show to render-node clients, vvvv-style.
/// The server deploys project files and live output state, drives playback, and each client renders
/// only the outputs assigned to it in the Output Setup (OutputDefinition.MachineName).
///
/// Threading contract matches the collab session manager: network threads only enqueue inbound messages; every
/// state change and every graph/file write happens in <see cref="Update"/> on the UI thread. File
/// transfers run per client in small steps per frame so large deployments never stall rendering.
/// </summary>
internal sealed class BoygroupManager
{
    public static BoygroupManager Instance { get; } = new();

    public enum BoygroupState
    {
        Off,
        Server,
        Client,
    }

    public BoygroupState State { get; private set; } = BoygroupState.Off;
    public string StatusText { get; private set; } = "Boygrouping is off";
    public string ErrorText { get; private set; } = "";
    public string GroupName { get; private set; } = "";
    public string TimeFollowSummary { get; private set; } = "Time follow off";
    public string DeploySummary { get; private set; } = "";
    public bool RestartPending { get; private set; }

    public sealed class ClientInfo
    {
        public required CollabPeerConnection Peer;
        public string Name = "";
        public string Address = "";
        public string Group = "";
        public string StatusText = "connected";
        public bool IsDeployed;

        // Pending auth challenge (server side)
        public byte[]? PendingSalt;
        public byte[]? PendingNonce;

        // Deploy pipeline state (server side)
        public Dictionary<string, BoygroupDeployer.InventoryEntry>? PendingInventory;
        public List<string>? PendingFiles;
        public int FilesDone;
        public BoygroupDeployer.FileSender? Sender;
        public bool Mirror;
    }

    private readonly List<ClientInfo> _clients = [];
    public IReadOnlyList<ClientInfo> Clients => _clients;

    public sealed class DiscoveredServer
    {
        public string ServerName = "";
        public string Address = "";
        public int Port;
        public List<string> Groups = [];
        public DateTime Seen = DateTime.UtcNow;
    }

    private readonly List<DiscoveredServer> _discoveredServers = [];
    public IReadOnlyList<DiscoveredServer> DiscoveredServers => _discoveredServers;

    private LanDiscoveryScanner? _discoveryScanner;
    private BoygroupTransport? _transport;
    private string _password = "";

    /// <summary>The active link while a session is running; null when boygrouping is off. The peer
    /// camera service tracks it so cameras flow between the machines of the session.</summary>
    public CollabTransport? PeerSessionTransport => _transport;

    public bool PeerSessionIsHost => State == BoygroupState.Server;

    private long _nextHeartbeatTicks;
    private long _nextTimeSyncTicks;
    private long _autoJoinCheckTicks;
    private long _nextServerRestartTicks;
    private long _nextSlaveJoinAttemptTicks;

    // Input replication. Hardware callbacks enqueue from their own threads; the UI thread batches them
    // into one message per frame.
    private const int MaxQueuedInputEvents = 8192;
    private readonly ConcurrentQueue<RemoteMidiEvent> _outgoingMidi = new();
    private readonly ConcurrentQueue<RemoteOscPacket> _outgoingOsc = new();
    private readonly List<RemoteMidiEvent> _outgoingMidiBatch = [];
    private readonly List<RemoteOscPacket> _outgoingOscBatch = [];

    // Client-side deploy receiver
    private readonly BoygroupDeployer.FileReceiver _fileReceiver = new();

    private BoygroupManager()
    {
        RemoteInputBus.MidiCaptured += OnMidiCaptured;
        RemoteInputBus.OscCaptured += OnOscCaptured;
        RemoteInputBus.ErrorReported += message => Log.Warning(message);
    }

    /// <summary>Adds the machines this instance is currently linked to: the render nodes on a
    /// master, the master on a render node — the settings panel's network check compares them
    /// against the selected adapter.</summary>
    public void AddLinkedPeers(List<NetworkPeer> peers)
    {
        if (State == BoygroupState.Client)
        {
            var address = (_transport as BoygroupClientTransport)?.ServerPeer.RemoteAddress;
            if (address != null)
                peers.Add(new NetworkPeer("Boygroup master", address));
        }
        else if (State == BoygroupState.Server)
        {
            foreach (var client in _clients)
            {
                var address = client.Peer.RemoteAddress;
                if (address != null)
                    peers.Add(new NetworkPeer($"Boygroup node {client.Name}", address));
            }
        }
    }

    /// <summary>What the input replication is doing, for the settings panel and the debug bridge.</summary>
    public string InputRelaySummary => State switch
                                           {
                                               BoygroupState.Server => "MIDI/OSC from this machine",
                                               BoygroupState.Client => "Receiving MIDI/OSC",
                                               _                    => "",
                                           };

    /// <summary>True while this master forwards its hardware input to at least one render node.</summary>
    public bool IsCapturingInputs => RemoteInputBus.IsCapturing;

    private void OnMidiCaptured(RemoteMidiEvent ev)
    {
        if (State != BoygroupState.Server)
            return;

        if (_outgoingMidi.Count >= MaxQueuedInputEvents)
        {
            Interlocked.Increment(ref _droppedInputEvents);
            return;
        }

        _outgoingMidi.Enqueue(ev);
    }

    private void OnOscCaptured(RemoteOscPacket packet)
    {
        if (State != BoygroupState.Server)
            return;

        if (_outgoingOsc.Count >= MaxQueuedInputEvents)
        {
            Interlocked.Increment(ref _droppedInputEvents);
            return;
        }

        _outgoingOsc.Enqueue(packet);
    }

    private long _droppedInputEvents;

    // ---------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------

    public void StartServer(int port, string password, string group)
    {
        if (State != BoygroupState.Off)
            Leave();

        ErrorText = "";
        try
        {
            _password = password ?? "";
            GroupName = group;
            _transport = new BoygroupServerTransport(port, UserSettings.Config.BoygroupGroups.Distinct().ToArray(), UserSettings.Config.NetworkInterface);
            _transport.PeerDisconnected += OnPeerDisconnected;
            State = BoygroupState.Server;
            StatusText = $"Serving group '{group}' on port {port}";
            PersistJoinTarget("127.0.0.1", group);
            Log.Info($"Boygroup: server started (group '{group}', port {port})");
        }
        catch (SocketException e)
        {
            ErrorText = $"Could not open port {port}: {e.Message}";
            _transport = null;
            State = BoygroupState.Off;
        }
    }

    public void JoinServer(string host, int port, string group, string password)
    {
        if (State != BoygroupState.Off)
            Leave();

        ErrorText = "";
        try
        {
            _password = password ?? "";
            GroupName = group;
            var address = ResolveHost(host);
            if (address == null)
                throw new ArgumentException($"'{host}' is not a valid address");

            var transport = BoygroupClientTransport.Connect(new IPEndPoint(address, port));
            _transport = transport;
            transport.PeerDisconnected += OnPeerDisconnected;
            State = BoygroupState.Client;
            StatusText = $"Joining '{group}' on {host}:{port}...";
            // Subscribe first, then start reading: a link that dies right now must report.
            transport.StartReading();
            SendJson(transport.ServerPeer,
                     new
                     {
                         t = "hello",
                         name = ClientDisplayName(),
                         group,
                     });
            PersistJoinTarget(host, group);
            StopDiscovery(); // joined: no need to keep probing (the wait loop restarts it if we drop)
        }
        catch (Exception e)
        {
            ErrorText = $"Boygroup join failed: {e.Message}";
            _transport?.Dispose();
            _transport = null;
            State = BoygroupState.Off;
        }
    }

    public void Leave()
    {
        if (_transport != null)
        {
            _transport.PeerDisconnected -= OnPeerDisconnected;
            _transport.Dispose();
            _transport = null;
        }

        foreach (var client in _clients)
        {
            client.Sender?.Dispose();
        }

        _clients.Clear();
        _fileReceiver.Cancel();
        while (_pendingDisconnects.TryDequeue(out _))
        {
            // Drop events from the dead session: replaying them into the next one would end it
            // immediately (the collab manager drains its queue for the same reason).
        }

        _password = "";
        GroupName = "";
        RestartPending = false;
        State = BoygroupState.Off;
        StatusText = "Boygrouping is off";
    }

    /// <summary>The name this machine is known by in output assignments (OutputDefinition.MachineName).</summary>
    public static string LocalNodeName =>
        string.IsNullOrWhiteSpace(UserSettings.Config.BoygroupClientName) ? Environment.MachineName : UserSettings.Config.BoygroupClientName;

    private static string ClientDisplayName() => LocalNodeName;

    private void PersistJoinTarget(string host, string group)
    {
        var config = UserSettings.Config;
        if (config.BoygroupLastServer == host && config.BoygroupLastGroup == group)
            return;

        config.BoygroupLastServer = host;
        config.BoygroupLastGroup = group;
        UserSettings.Save();
    }

    /// <summary>Auto-rejoin after a deployment restart (or normal launch) when enabled.</summary>
    private void TryAutoJoin()
    {
        var config = UserSettings.Config;
        if (!config.BoygroupAutoJoin || string.IsNullOrEmpty(config.BoygroupLastServer) || string.IsNullOrEmpty(config.BoygroupLastGroup))
            return;

        Log.Info($"Boygroup: auto-joining {config.BoygroupLastServer} group '{config.BoygroupLastGroup}'");
        var port = config.BoygroupPort;
        JoinServer(config.BoygroupLastServer, port, config.BoygroupLastGroup, config.CollabPassword);
    }

    /// <summary>
    /// Applies the enable switch from the settings: starts the server when this machine is a
    /// master, joins the last server when it is a slave, leaves when disabled. Called from the
    /// panel toggle and once per launch (after the settle delay), so a show machine comes up
    /// in its role without anyone clicking.
    /// </summary>
    public void ApplyEnabledSetting()
    {
        var config = UserSettings.Config;
        if (!config.BoygroupEnabled)
        {
            if (State != BoygroupState.Off)
                Leave();
            StopDiscovery();
            return;
        }

        // A role flip while running means leave + re-apply on the next call.
        if (State != BoygroupState.Off && (State == BoygroupState.Server) != config.BoygroupIsMaster)
            Leave();

        if (State != BoygroupState.Off)
            return; // Already running in the configured role.

        if (config.BoygroupIsMaster)
        {
            StartServer(config.BoygroupPort, config.CollabPassword, config.BoygroupGroups.FirstOrDefault() ?? "Main");
        }
        else
        {
            TryAutoJoin();
        }
    }

    /// <summary>
    /// An enabled slave that has never joined a master keeps scanning and joins the first master
    /// that appears, like the master role keeps serving: the pair finds each other after a reboot
    /// in any order. Join attempts are throttled so a dead candidate can never flap the frame
    /// loop; a failed attempt clears the candidate list and the scanner restarts on its own.
    /// </summary>
    private void WaitAndAutoJoinDiscoveredMaster()
    {
        if (_discoveryScanner == null)
            StartDiscovery();

        DrainDiscovery();

        if (Environment.TickCount64 < _nextSlaveJoinAttemptTicks)
            return;

        var config = UserSettings.Config;
        var preferredGroup = config.BoygroupLastGroup is { Length: > 0 } last ? last : null;

        // Prefer a master that hosts the group we want; otherwise take the only / first one.
        var candidate = preferredGroup != null
                            ? _discoveredServers.FirstOrDefault(s => s.Groups.Contains(preferredGroup))
                              ?? _discoveredServers.FirstOrDefault()
                            : _discoveredServers.FirstOrDefault();
        if (candidate == null)
            return;

        _nextSlaveJoinAttemptTicks = Environment.TickCount64 + 3_000;
        Log.Info($"Boygroup: enabled slave joining discovered master {candidate.ServerName} ({candidate.Address}:{candidate.Port})");
        StopDiscovery(); // clears the candidate list; the scanner restarts if this attempt fails
        JoinServer(candidate.Address, candidate.Port,
                   preferredGroup ?? config.BoygroupGroups.FirstOrDefault() ?? candidate.Groups.FirstOrDefault() ?? "Main",
                   config.CollabPassword);
    }

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
        // Apply the enable switch shortly after startup (give the project time to load first):
        // an enabled master starts its server, an enabled slave auto-joins the last server.
        if (State == BoygroupState.Off)
        {
            if (_autoJoinCheckTicks == 0)
            {
                _autoJoinCheckTicks = Environment.TickCount64 + 8000;
            }
            else if (Environment.TickCount64 > _autoJoinCheckTicks && !_autoJoinAttempted)
            {
                _autoJoinAttempted = true;
                if (UserSettings.Config.BoygroupEnabled)
                {
                    ApplyEnabledSetting();
                }
                else
                {
                    TryAutoJoin();
                }
            }
            else if (_autoJoinAttempted && UserSettings.Config.BoygroupEnabled && !UserSettings.Config.BoygroupIsMaster)
            {
                // Enabled slave still off: keep looking for a master.
                WaitAndAutoJoinDiscoveredMaster();
            }
            else if (_autoJoinAttempted && UserSettings.Config.BoygroupEnabled && UserSettings.Config.BoygroupIsMaster
                     && Environment.TickCount64 >= _nextServerRestartTicks)
            {
                // Enabled master whose server dropped (port conflict, transport fault): bring it
                // back. Throttled so a persistent failure cannot spin the frame loop.
                _nextServerRestartTicks = Environment.TickCount64 + 5_000;
                ApplyEnabledSetting();
            }

            return;
        }

        ProcessPendingDisconnects();

        // Disconnects and inbound messages can both end the session (server loss, restart
        // request), disposing the transport — the remaining steps must not run against it.
        if (State == BoygroupState.Off)
            return;

        DrainInbound();

        if (State == BoygroupState.Off)
            return;

        PumpDeploy();
        RunScheduledAutoDeploy();
        SendHeartbeats();
        SendTimeSync();
        ProcessPendingRestart();

        if (State == BoygroupState.Server)
        {
            PumpInputReplication();
            ReRequestPendingInventories();
            BroadcastPlayStateOnChange();
        }

        RefreshDiscoveredServers();
    }

    private static bool _autoJoinAttempted;

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
                client.Sender?.Dispose();
                _clients.Remove(client);
                Log.Info($"Boygroup: client left: {client.Name}");
                continue;
            }

            if (State == BoygroupState.Client)
            {
                ErrorText = "Disconnected from boygroup server";
                Leave();
            }
        }
    }

    // ---------------------------------------------------------------------------------------


    // ---------------------------------------------------------------------------------------
    // Input replication and video relay
    // ---------------------------------------------------------------------------------------

    private void PumpInputReplication()
    {
        if (_outgoingMidi.IsEmpty && _outgoingOsc.IsEmpty)
            return;

        _outgoingMidiBatch.Clear();
        _outgoingOscBatch.Clear();

        for (var i = 0; i < BoygroupInputWire.MaxEventsPerBatch && _outgoingMidi.TryDequeue(out var midi); i++)
            _outgoingMidiBatch.Add(midi);

        for (var i = 0; i < BoygroupInputWire.MaxEventsPerBatch && _outgoingOsc.TryDequeue(out var osc); i++)
            _outgoingOscBatch.Add(osc);

        if (_outgoingMidiBatch.Count == 0 && _outgoingOscBatch.Count == 0)
            return;

        BroadcastToGroup(BoygroupInputWire.Encode(_outgoingMidiBatch, _outgoingOscBatch));

        // A producer faster than the link must not make the queue grow without bound.
        var dropped = Interlocked.Exchange(ref _droppedInputEvents, 0);
        if (dropped > 0)
            Log.Warning($"Boygroup: dropped {dropped} input events because the replication queue overflowed");
    }

    /// <summary>
    /// Master side: keeps one OMT sender per VideoDeviceInput, sends its frame and tells the render nodes
    /// which source to receive. The source list is re-broadcast when it changes and again periodically so
    /// a node that joins later picks it up.
    /// </summary>

    private static void ApplyRemoteInput(JObject json)
    {
        if (!BoygroupInputWire.TryDecode(json, out var midi, out var osc))
            return;

        foreach (var remote in midi)
        {
            try
            {
                var midiEvent = MidiEvent.FromRawMessage(remote.RawMessage);
                if (midiEvent == null)
                    continue;

                SimulatedIoBus.DispatchMidi(new SimulatedIoBus.SimulatedMidiEvent(remote.DeviceProductName, midiEvent));
                RemoteInputBus.MarkRemoteMidiReceived();
            }
            catch (Exception e)
            {
                Log.Debug($"Boygroup: dropping a malformed MIDI event: {e.Message}");
            }
        }

        foreach (var remote in osc)
        {
            try
            {
                if (OscPacket.Read(remote.Packet, 0, remote.Packet.Length) is not OscMessage message)
                    continue;

                SimulatedIoBus.DispatchOscPacket(new SimulatedIoBus.SimulatedOscPacket(remote.Port, message));
                RemoteInputBus.MarkRemoteOscReceived();
            }
            catch (Exception e)
            {
                Log.Debug($"Boygroup: dropping a malformed OSC packet: {e.Message}");
            }
        }
    }

    /// <summary>Client side: applies the master's video input list to the remote camera bus.</summary>
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

            case "io" when State == BoygroupState.Client:
                ApplyRemoteInput(json);
                break;

            case "welcome":
                HandleWelcome(json);
                break;

            case "auth" when State == BoygroupState.Server:
                HandleAuth(peer, json);
                break;

            case "authOk":
                StatusText = "Joined group";
                break;

            case "authFail":
                ErrorText = "Rejected by server: " + CollabProtocol.GetString(json, "reason", "authentication failed");
                Leave();
                break;

            case "inventory" when State == BoygroupState.Server:
            {
                // The client answered our requestInventory with what it has on disk.
                var requestingClient = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
                if (requestingClient != null)
                    HandleClientInventory(requestingClient, json);
                break;
            }

            case "requestInventory" when State == BoygroupState.Client:
                HandleServerInventory(json);
                break;

            case "deployStart" when State == BoygroupState.Client:
                _deployFilesTotal = json["count"]?.Value<int>() ?? 0;
                _deployFilesDone = 0;
                _deployErrors = 0;
                DeploySummary = $"Receiving {_deployFilesTotal} file(s)...";
                break;

            case "fileBegin" when State == BoygroupState.Client:
                StartReceivingFile(json);
                break;

            case "fileChunk" when State == BoygroupState.Client:
                _fileReceiver.Chunk(json);
                break;

            case "fileEnd" when State == BoygroupState.Client:
                FinishReceivingFile(json);
                break;

            case "deleteFile" when State == BoygroupState.Client:
                DeleteClientFile(json);
                break;

            case "deployDone" when State == BoygroupState.Client:
                HandleDeployDone(json);
                break;

            case "setupJson" when State == BoygroupState.Client:
                WriteSetupFile(json);
                break;

            case "snapshot" when State == BoygroupState.Client:
                if (json["op"] is JObject op)
                    CollabCommandWire.ApplyOp(op);

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

    private int _deployFilesTotal;
    private int _deployFilesDone;
    private int _deployErrors;

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
        SendJson(client.Peer, new { t = "authOk", serverName = Environment.MachineName });

        // Late joiners must see the show as it runs: the play broadcast only fires when the
        // master's playback state changes, so a client that joined mid-show stayed frozen.
        var playback = Playback.Current;
        SendJson(client.Peer, new
                               {
                                   t = "play",
                                   playing = (playback?.PlaybackSpeed ?? 0) > 0,
                                   bars = playback?.TimeInBars ?? 0,
                               });

        Log.Info($"Boygroup: client authenticated: {client.Name} ({client.Address}) group '{client.Group}'");

        // A node that appears is served: schedule a deploy shortly after the handshake (the delay
        // debounces a burst of joins into one pass; clients mid-deploy keep their own transfer).
        if (UserSettings.Config.BoygroupAutoDeployOnJoin)
            _autoDeployAtTicks = Environment.TickCount64 + 1500;
    }

    /// <summary>Due time for the deploy-on-join pass; 0 while nothing is scheduled.</summary>
    private long _autoDeployAtTicks;

    private void RunScheduledAutoDeploy()
    {
        if (_autoDeployAtTicks == 0 || Environment.TickCount64 < _autoDeployAtTicks)
            return;

        _autoDeployAtTicks = 0;
        if (State != BoygroupState.Server || string.IsNullOrEmpty(GetProjectFolder()))
            return; // No project open: a manual Deploy & Sync will report it properly.

        Log.Info("Boygroup: node joined - running deploy & sync");
        DeployAndSync();
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

    /// <summary>Server action: push project files, the output setup, and live graph state to the group.</summary>
    public void DeployAndSync()
    {
        if (State != BoygroupState.Server)
        {
            ErrorText = "Only the boygroup server can deploy";
            return;
        }

        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
        {
            ErrorText = "No project open — open a project before deploying";
            return;
        }

        var mirror = UserSettings.Config.BoygroupDeployMirror;
        var setupsFolderRelative = Path.GetRelativePath(projectFolder, SetupFiles.FolderIn(projectFolder));
        var inventory = BoygroupDeployer.BuildInventory(projectFolder, setupsFolderRelative);

        foreach (var client in _clients.Where(c => c.Peer.IsAuthenticated && c.Group == GroupName).ToList())
        {
            if (client.PendingFiles != null)
                continue; // A deploy is already in flight for this client.

            client.Mirror = mirror;
            client.PendingInventory = inventory;
            // The client's own inventory arrives first (it reported it in its hello); request it via
            // deployStart and let the client answer with its inventory for diffing.
            client.StatusText = "requesting inventory";
            SendJson(client.Peer, new { t = "requestInventory" });
        }

        PushSetupToGroup();
        PushGraphSnapshotToGroup();
    }

    private void HandleClientInventory(ClientInfo client, JObject json)
    {
        if (client.PendingInventory == null)
            return;

        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        var serverInventory = client.PendingInventory;
        var clientInventory = BoygroupDeployer.ParseInventory(json);
        var missing = BoygroupDeployer.ComputeMissingFiles(serverInventory, clientInventory);
        var mirror = client.Mirror;

        if (mirror)
        {
            foreach (var extra in BoygroupDeployer.ComputeExtraFiles(serverInventory, clientInventory))
            {
                SendJson(client.Peer, new { t = "deleteFile", path = extra });
            }
        }

        client.PendingFiles = missing;
        client.FilesDone = 0;
        client.Sender = null;
        client.StatusText = missing.Count == 0 ? "up to date" : $"sending {missing.Count} file(s)...";
        client.IsDeployed = false;
        SendJson(client.Peer, new { t = "deployStart", count = missing.Count, mirror });

        if (missing.Count == 0)
        {
            FinishDeployToClient(client);
        }
    }

    /// <summary>
    /// Clients still loading their project stay silent when asked for their inventory (see
    /// <see cref="HandleServerInventory"/>), so a deploy request would otherwise dangle. Re-ask
    /// every few seconds until the client answers or the session ends.
    /// </summary>
    private void ReRequestPendingInventories()
    {
        var now = Environment.TickCount64;
        foreach (var client in _clients)
        {
            if (!client.Peer.IsAuthenticated || client.PendingInventory == null)
                continue;

            if (_inventoryRequestedAt.TryGetValue(client.Peer.AssignedId, out var requestedAt)
                && now - requestedAt < ReRequestInventoryIntervalMs)
                continue;

            _inventoryRequestedAt[client.Peer.AssignedId] = now;
            client.StatusText = "waiting for project to load...";
            SendJson(client.Peer, new { t = "requestInventory" });
        }
    }

    private const int ReRequestInventoryIntervalMs = 4000;
    private readonly Dictionary<int, long> _inventoryRequestedAt = [];

    /// <summary>
    /// The play broadcast must follow the master's playback however it is driven - timeline
    /// space bar, panel buttons or an external agent. Broadcast when the running state changes.
    /// </summary>
    private void BroadcastPlayStateOnChange()
    {
        var playing = (Playback.Current?.PlaybackSpeed ?? 0) > 0;
        if (playing == _lastBroadcastPlaying)
            return;

        _lastBroadcastPlaying = playing;
        BroadcastToGroup(new { t = "play", playing, bars = Playback.Current?.TimeInBars ?? 0 });
    }

    private bool _lastBroadcastPlaying;

    /// <summary>Runs one deploy step per client per frame; returns true when a frame budget is spent.</summary>
    private void PumpDeploy()
    {
        if (State != BoygroupState.Server)
            return;

        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return; // Nothing to deploy from; the deploy was already reported as failed on request.

        foreach (var client in _clients)
        {
            if (client.PendingFiles == null)
                continue;

            // A few chunk messages per client per frame keep transfers moving without hurting the
            // render frame budget.
            for (var step = 0; step < 24; step++)
            {
                if (client.Sender == null)
                {
                    if (client.FilesDone >= client.PendingFiles.Count)
                    {
                        FinishDeployToClient(client);
                        break;
                    }

                    var relativePath = client.PendingFiles[client.FilesDone];
                    try
                    {
                        client.Sender = new BoygroupDeployer.FileSender(projectFolder, relativePath);
                        SendJson(client.Peer, new { t = "fileBegin", path = relativePath });
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"Boygroup: can't read {relativePath}: {e.Message}");
                        client.FilesDone++;
                        continue;
                    }
                }

                var message = client.Sender.NextMessage();
                if (message == null)
                    continue;

                SendJson(client.Peer, message);
                if (client.Sender.Completed)
                {
                    client.FilesDone++;
                    client.Sender.Dispose();
                    client.Sender = null;
                    client.StatusText = $"sending {client.FilesDone}/{client.PendingFiles.Count}";
                }
                else
                {
                    break; // Spend the rest of this frame's budget on the next client.
                }
            }
        }
    }

    private void FinishDeployToClient(ClientInfo client)
    {
        client.PendingFiles = null;
        client.PendingInventory = null;
        client.IsDeployed = true;
        client.StatusText = "deployed";
        // A deploy that transferred nothing (the node was already current) must not reboot it:
        // the auto-deploy on join runs against every join, up-to-date nodes stay up.
        var transferred = client.FilesDone > 0;
        SendJson(client.Peer, new { t = "deployDone", restart = transferred });
        Log.Info($"Boygroup: deployment to {client.Name} complete ({client.FilesDone} file(s){(transferred ? ", restart requested" : ", no changes")})");
    }

    /// <summary>Pushes the active output setup file so clients render the master's output config.</summary>
    private void PushSetupToGroup()
    {
        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        var setupsFolder = SetupFiles.FolderIn(projectFolder);
        if (!Directory.Exists(setupsFolder))
            return;

        foreach (var filePath in Directory.EnumerateFiles(setupsFolder, "*" + Setup.FileSuffix))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var name = Path.GetFileName(filePath);
                BroadcastToGroup(new { t = "setupJson", name, json });
            }
            catch (IOException e)
            {
                Log.Warning($"Boygroup: can't read setup {filePath}: {e.Message}");
            }
        }
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
    // Client: deploy reception
    // ---------------------------------------------------------------------------------------

    private void HandleServerInventory(JObject json)
    {
        // Client answers the server's request with what it has on disk. The setups folder is
        // excluded on both sides: it travels as live setupJson, and mirror mode would otherwise
        // flag the client's applied setups as extra and delete them. While the project is still
        // loading there is nothing to answer with: staying silent makes the server re-request
        // instead of deploying the whole project into a folder that does not exist yet.
        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        var inventory = BoygroupDeployer.BuildInventory(projectFolder,
                                                        Path.GetRelativePath(projectFolder, SetupFiles.FolderIn(projectFolder)));
        SendJson(((_transport as BoygroupClientTransport)!).ServerPeer, BoygroupDeployer.BuildInventoryMessage(inventory));
    }

    private void StartReceivingFile(JObject json)
    {
        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
        {
            ErrorText = "No project open — can't receive deployment";
            return;
        }

        try
        {
            _fileReceiver.Begin(projectFolder, json);
        }
        catch (Exception e)
        {
            ErrorText = $"Deploy: {e.Message}";
        }
    }

    private void FinishReceivingFile(JObject json)
    {
        var error = _fileReceiver.Finish(json);
        _deployFilesDone++;
        if (error != null)
        {
            _deployErrors++;
            Log.Warning($"Boygroup deploy: {error}");
        }

        DeploySummary = $"Receiving {_deployFilesDone}/{_deployFilesTotal} files" + (_deployErrors > 0 ? $" ({_deployErrors} failed)" : "");
    }

    private void DeleteClientFile(JObject json)
    {
        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        try
        {
            var relativePath = json["path"]?.Value<string>() ?? "";
            var fullPath = BoygroupDeployer.SafeProjectPath(projectFolder, relativePath);
            if (File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
                File.Delete(fullPath);
                Log.Debug($"Boygroup deploy: removed {relativePath}");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Boygroup deploy: can't delete {CollabProtocol.GetString(json, "path")}: {e.Message}");
        }
    }

    private void WriteSetupFile(JObject json)
    {
        var projectFolder = GetProjectFolder();
        if (string.IsNullOrEmpty(projectFolder))
            return;

        try
        {
            var name = json["name"]?.Value<string>() ?? "";
            if (name.Contains("..") || !name.EndsWith(Setup.FileSuffix, StringComparison.Ordinal))
                throw new IOException($"unsafe setup file name '{name}'");

            var setupsFolder = SetupFiles.FolderIn(projectFolder);
            Directory.CreateDirectory(setupsFolder);
            var fullPath = Path.Combine(setupsFolder, name);
            File.WriteAllText(fullPath, json["json"]?.Value<string>() ?? "");
            Log.Info($"Boygroup: setup updated: {name}");
        }
        catch (Exception e)
        {
            Log.Warning($"Boygroup: can't write setup: {e.Message}");
        }
    }

    private void HandleDeployDone(JObject json)
    {
        var shouldRestart = json["restart"]?.Value<bool>() ?? true;
        DeploySummary = $"Deployment complete ({_deployFilesDone} files" + (_deployErrors > 0 ? $", {_deployErrors} failed" : "") + ")";

        // A deploy that transferred no files (the node was already current) leaves this machine up:
        // only a real update restarts into the deployed state.
        if (_deployFilesTotal == 0)
        {
            Log.Info("Boygroup: deploy had no file changes - staying up");
            return;
        }

        if (!shouldRestart || !UserSettings.Config.BoygroupAutoJoin)
            return;

        // Flood breaker, persisted across the restarts it guards: a file that perpetually diffs
        // (should be impossible now that setups are excluded) may never loop a show machine.
        var config = UserSettings.Config;
        var now = Environment.TickCount64;
        if (now < config.BoygroupDeployRestartBlockedUntilTicks)
        {
            Log.Warning($"Boygroup: deploy restart blocked for another {(config.BoygroupDeployRestartBlockedUntilTicks - now) / 1000}s (restart-loop guard) - staying up");
            return;
        }

        if (config.BoygroupDeployRestartWindowStartTicks == 0 || now - config.BoygroupDeployRestartWindowStartTicks > 120_000)
        {
            config.BoygroupDeployRestartWindowStartTicks = now;
            config.BoygroupDeployRestartCount = 1;
        }
        else
        {
            config.BoygroupDeployRestartCount++;
            if (config.BoygroupDeployRestartCount > 4)
            {
                config.BoygroupDeployRestartBlockedUntilTicks = now + 300_000;
                config.BoygroupDeployRestartCount = 0;
                Log.Warning("Boygroup: more than four deploy restarts in two minutes - blocking further deploy restarts for five minutes (restart-loop guard)");
            }
        }

        // A render node must come back into the show it was deployed, not into the Hub: pin the
        // deployed project as the startup project so the restart reopens it (StartupProjectLoader).
        // Only for the slave role - a master's editor restarts are the operator's business.
        if (State == BoygroupState.Client && config.BoygroupEnabled && !config.BoygroupIsMaster)
        {
            if (GetProjectFolder() is { Length: > 0 } deployedProject)
                config.StartupProjectFolder = deployedProject;
        }

        UserSettings.Save();

        RestartPending = true;
        // Brief delay so the panel shows what happened before the process exits.
        _restartAtTicks = Environment.TickCount64 + 1500;
    }

    private long _restartAtTicks;

    private void ProcessPendingRestart()
    {
        if (!RestartPending || _restartAtTicks == 0 || Environment.TickCount64 < _restartAtTicks)
            return;

        _restartAtTicks = 0;
        Log.Info("Boygroup: restarting to apply deployed files");
        EditorRestart.TryRestart();
    }

    // ---------------------------------------------------------------------------------------
    // Playback control and time sync
    // ---------------------------------------------------------------------------------------

    public void SendPlayControl(bool play)
    {
        if (State != BoygroupState.Server)
            return;

        BroadcastToGroup(new { t = "play", playing = play, bars = Playback.Current?.TimeInBars ?? 0 });
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

        BroadcastToGroup(new
                             {
                                 t = "time",
                                 bars = playback.TimeInBars,
                                 speed = playback.PlaybackSpeed,
                                 bpm = playback.Bpm,
                                 at = Environment.TickCount64,
                             });
    }

    private long _lastSyncWallMs = -1;
    private double _lastSyncBars;
    private double _lastSyncSpeed;
    private double _lastSyncBpm = 120;

    private void ApplyTimeSync(JObject json)
    {
        var playback = Playback.Current;
        if (playback == null)
            return;

        _lastSyncWallMs = Environment.TickCount64;
        _lastSyncBars = json["bars"]?.Value<double>() ?? 0;
        _lastSyncSpeed = json["speed"]?.Value<double>() ?? 0;
        _lastSyncBpm = json["bpm"]?.Value<double>() ?? 120;

        playback.PlaybackSpeed = _lastSyncSpeed;
        playback.Bpm = _lastSyncBpm;
        ApplyIntegratedTime();

        var playing = _lastSyncSpeed != 0;
        TimeFollowSummary = playing
                                ? $"Following master: {_lastSyncBars:0.00} bars @ {_lastSyncBpm:0} bpm"
                                : "Following master (paused)";
    }

    /// <summary>Dead-reckons between master updates so the time cursor moves every frame, not in 10 Hz steps.</summary>
    private void ApplyIntegratedTime()
    {
        var playback = Playback.Current;
        if (playback == null || _lastSyncWallMs < 0)
            return;

        // Monotonic host clock: TimeOfDay would wrap at midnight and throw the time cursor away.
        var elapsedSecs = (Environment.TickCount64 - _lastSyncWallMs) / 1000.0;
        playback.TimeInBars = _lastSyncBars + _lastSyncSpeed * elapsedSecs * _lastSyncBpm / 240.0;
    }

    // ---------------------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------------------

    public void StartDiscovery()
    {
        _discoveryScanner ??= new LanDiscoveryScanner();
        _discoveredServers.Clear();

        // A master whose address is already known is also probed by unicast, so the scan still
        // finds it on networks that swallow broadcast packets.
        List<IPAddress>? directTargets = null;
        var lastServer = UserSettings.Config.BoygroupLastServer;
        if (!string.IsNullOrWhiteSpace(lastServer))
        {
            var resolved = ResolveHost(lastServer);
            if (resolved != null)
                directTargets = [resolved];
        }

        _discoveryScanner.Start(UserSettings.Config.BoygroupPort + LanDiscoveryResponder.DiscoveryPortOffset, directTargets, UserSettings.Config.NetworkInterface);
    }

    public void StopDiscovery()
    {
        _discoveryScanner?.Stop();
        _discoveryScanner = null;
        _discoveredServers.Clear();
    }

    /// <summary>Moves scan results into the visible list; called from Update and the panel.</summary>
    public void DrainDiscovery()
    {
        if (_discoveryScanner == null)
            return;

        while (_discoveryScanner.Results.TryDequeue(out var result))
        {
            DiscoveredServer server;
            try
            {
                var announcement = result.Announcement;
                if (announcement["boygroup"] == null)
                    continue;

                server = new DiscoveredServer
                             {
                                 ServerName = CollabProtocol.GetString(announcement, "server", "boygroup server"),
                                 Address = result.Source.Address.ToString(),
                                 Port = announcement["port"]?.Value<int>() ?? UserSettings.Config.BoygroupPort,
                             };

                if (announcement["groups"] is JArray groups)
                {
                    foreach (var group in groups)
                    {
                        var name = group.Value<string>();
                        if (!string.IsNullOrEmpty(name))
                            server.Groups.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                // A malformed announcement (hostile or from another tool) must never reach the
                // frame loop: drop it and keep draining.
                Log.Debug($"Boygroup: dropping a malformed discovery announcement: {e.Message}");
                continue;
            }

            if (_discoveredServers.Any(d => d.Address == server.Address && d.Port == server.Port))
                continue;

            _discoveredServers.Add(server);
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
