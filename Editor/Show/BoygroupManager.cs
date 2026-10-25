#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using T3.Core.Animation;
using T3.Core.Output;
using T3.Editor.Collab;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Show;

/// <summary>
/// Boygrouping: one TiXL instance (server) distributes a show to render-node clients, vvvv-style.
/// The server deploys project files and live output state, drives playback, and each client renders
/// only the outputs assigned to it in the Output Setup (OutputDefinition.MachineName).
///
/// Threading contract matches CollabManager: network threads only enqueue inbound messages; every
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

    private long _nextHeartbeatTicks;
    private long _nextTimeSyncTicks;
    private long _autoJoinCheckTicks;

    // Client-side deploy receiver
    private readonly BoygroupDeployer.FileReceiver _fileReceiver = new();

    private BoygroupManager()
    {
    }

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
            _transport = new BoygroupServerTransport(port, UserSettings.Config.BoygroupGroups.ToArray());
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

            _transport = BoygroupClientTransport.Connect(new IPEndPoint(address, port));
            _transport.PeerDisconnected += OnPeerDisconnected;
            State = BoygroupState.Client;
            StatusText = $"Joining '{group}' on {host}:{port}...";
            SendJson(((_transport as BoygroupClientTransport)!).ServerPeer,
                     new
                     {
                         t = "hello",
                         name = ClientDisplayName(),
                         group,
                     });
            PersistJoinTarget(host, group);
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
        // Auto-rejoin shortly after startup (give the project time to load first).
        if (State == BoygroupState.Off)
        {
            if (_autoJoinCheckTicks == 0)
            {
                _autoJoinCheckTicks = Environment.TickCount64 + 8000;
            }
            else if (Environment.TickCount64 > _autoJoinCheckTicks && !_autoJoinAttempted)
            {
                _autoJoinAttempted = true;
                TryAutoJoin();
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
        SendHeartbeats();
        SendTimeSync();
        ProcessPendingRestart();
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
                break;

            case "authFail":
                ErrorText = "Rejected by server: " + CollabProtocol.GetString(json, "reason", "authentication failed");
                Leave();
                break;

            case "inventory" when State == BoygroupState.Client:
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
        var client = _clients.FirstOrDefault(c => ReferenceEquals(c.Peer, peer));
        if (client == null)
        {
            Log.Warning("Boygroup: hello from unknown connection");
            return;
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
        Log.Info($"Boygroup: client authenticated: {client.Name} ({client.Address}) group '{client.Group}'");
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
        var inventory = BoygroupDeployer.BuildInventory(projectFolder);

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

    /// <summary>Runs one deploy step per client per frame; returns true when a frame budget is spent.</summary>
    private void PumpDeploy()
    {
        if (State != BoygroupState.Server)
            return;

        var projectFolder = GetProjectFolder();
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
        SendJson(client.Peer, new { t = "deployDone", restart = true });
        Log.Info($"Boygroup: deployment to {client.Name} complete");
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
        // Client answers the server's request with what it has on disk.
        var projectFolder = GetProjectFolder();
        var inventory = string.IsNullOrEmpty(projectFolder)
                            ? []
                            : BoygroupDeployer.BuildInventory(projectFolder);
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

        if (!shouldRestart || !UserSettings.Config.BoygroupAutoJoin)
            return;

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
        _discoveryScanner.Start(UserSettings.Config.BoygroupPort + LanDiscoveryResponder.DiscoveryPortOffset);
    }

    public void StopDiscovery()
    {
        _discoveryScanner?.Stop();
    }

    /// <summary>Moves scan results into the visible list; called from Update and the panel.</summary>
    public void DrainDiscovery()
    {
        if (_discoveryScanner == null)
            return;

        while (_discoveryScanner.Results.TryDequeue(out var result))
        {
            var announcement = result.Announcement;
            if (announcement["boygroup"] == null)
                continue;

            var server = new DiscoveredServer
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
