#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.Interaction;

namespace T3.Editor.Collab;

/// <summary>A message received from a peer, handed to the UI thread through the inbound queue.</summary>
internal readonly struct InboundMessage
{
    public InboundMessage(CollabPeerConnection peer, JObject json)
    {
        Peer = peer;
        Json = json;
    }

    public InboundMessage(CollabPeerConnection peer, PresencePacket presence)
    {
        Peer = peer;
        Presence = presence;
    }

    public readonly CollabPeerConnection Peer;
    public readonly JObject Json;
    public readonly PresencePacket Presence;

    public bool IsPresence => Presence != null;
}

/// <summary>
/// One remote participant: framed reader thread, write lock, and authentication state.
/// Instances are created on network threads but only ever listed/removed from the UI thread.
/// Direct transports back this with a TcpClient; the relay transport backs it with the relay
/// stream plus a slot id and a close callback.
/// </summary>
internal sealed class CollabPeerConnection
{
    public CollabPeerConnection(TcpClient client, int assignedId)
    {
        Client = client;
        AssignedId = assignedId;
        Stream = client.GetStream();
    }

    /// <summary>Relay-backed peer: shares the relay stream; closing only detaches this peer.</summary>
    public CollabPeerConnection(Stream sharedStream, int assignedId, Action onDetach)
    {
        Client = null;
        AssignedId = assignedId;
        Stream = sharedStream;
        _onDetach = onDetach;
    }

    public readonly TcpClient? Client;
    public readonly Stream Stream;
    public readonly int AssignedId;
    private readonly Action _onDetach;

    public volatile bool SessionClosed;
    public volatile bool IsAuthenticated;

    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public byte PresencePeerId => (byte)AssignedId;

    /// <summary>Latest presence. Swapped atomically by reference; never mutated in place.</summary>
    public PresencePacket LatestPresence;

    /// <summary>UDP endpoint the peer sends presence from; the reply path for host-to-peer presence. Null for relayed peers.</summary>
    public volatile IPEndPoint UdpEndpoint;

    private long _lastHeardFromTicks = Environment.TickCount64;
    private readonly object _writeLock = new();

    public void MarkHeardFrom() => Interlocked.Exchange(ref _lastHeardFromTicks, Environment.TickCount64);

    public bool IsTimedOut => Environment.TickCount64 - Interlocked.Read(ref _lastHeardFromTicks) > CollabProtocol.PeerTimeoutMs;

    /// <summary>Thread-safe framed write. Callers must pass a payload already encoded for the frame type.</summary>
    public void Write(CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        if (SessionClosed)
            return;

        try
        {
            lock (_writeLock)
            {
                if (SessionClosed)
                    return;

                CollabProtocol.WriteFrame(Stream, type, payload);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            SessionClosed = true;
        }
    }

    public void Close()
    {
        SessionClosed = true;
        try
        {
            Client?.Close();
        }
        catch
        {
            // Closing a dying socket can throw; nothing to do about it.
        }

        try
        {
            _onDetach?.Invoke();
        }
        catch
        {
            // Detach callbacks must not take the connection down with them.
        }
    }

    public override string ToString() => $"{Name}#{AssignedId}";
}

/// <summary>
/// Owns the raw sockets of a collaboration session. All socket I/O runs on dedicated background
/// threads; the only cross-thread artifacts are a ConcurrentQueue of inbound messages and locked
/// synchronous writes. Nothing in here touches editor state.
/// </summary>
internal abstract class CollabTransport : IDisposable
{
    /// <summary>Drained by <see cref="CollabManager"/> on the UI thread.</summary>
    public readonly ConcurrentQueue<InboundMessage> Inbound = new();

    /// <summary>Raised on a network thread when a peer disconnected. The manager removes it on the UI thread.</summary>
    public event Action<CollabPeerConnection> PeerDisconnected;

    public event Action<string> StatusMessage;

    /// <summary>Connected peers. Client transports expose only their host link so heartbeat and
    /// timeout handling covers it; the list may be read from the UI thread at any time.</summary>
    public abstract IReadOnlyList<CollabPeerConnection> Peers { get; }

    public abstract void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload);

    public void SendJson(CollabPeerConnection peer, object message)
    {
        SendToPeer(peer, CollabProtocol.FrameType.Json, CollabProtocol.EncodeJson(message));
    }

    public void BroadcastJson(IEnumerable<CollabPeerConnection> peers, object message, CollabPeerConnection except = null)
    {
        var payload = CollabProtocol.EncodeJson(message);
        foreach (var peer in peers)
        {
            if (peer == except || !peer.IsAuthenticated)
                continue;

            SendToPeer(peer, CollabProtocol.FrameType.Json, payload);
        }
    }

    protected void OnPeerDisconnected(CollabPeerConnection peer) => PeerDisconnected?.Invoke(peer);

    protected void OnStatus(string message) => StatusMessage?.Invoke(message);

    public abstract void Dispose();

    /// <summary>
    /// Shared per-connection receive loop. Reads frames until the socket dies or the session ends.
    /// Frame-size violations kill the connection instead of being logged — a peer sending oversized
    /// frames is either broken or attacking us.
    /// </summary>
    protected void ReadLoop(CollabPeerConnection peer)
    {
        var buffer = new byte[CollabProtocol.MaxFrameSize];
        try
        {
            var stream = peer.Stream;
            while (!peer.SessionClosed)
            {
                if (!CollabProtocol.TryReadFrameLength(stream, out var length))
                    break;

                if (length < 1 || length > CollabProtocol.MaxFrameSize)
                {
                    Log.Warning($"Collab: peer {peer} sent illegal frame length {length}, disconnecting");
                    break;
                }

                if (!CollabProtocol.TryReadExact(stream, buffer.AsSpan(0, length)))
                    break;

                // Any frame counts as a life sign, not just heartbeats.
                peer.MarkHeardFrom();
                DispatchFrame(peer, buffer.AsSpan(0, length));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // Expected during teardown or when a peer drops. Logged at debug level to keep the
            // session log readable when the wifi hiccups.
            Log.Debug($"Collab: connection to {peer} ended: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Warning($"Collab: unexpected error reading from {peer}: {e.Message}");
        }

        peer.SessionClosed = true;
        CleanupPeer(peer);
        OnPeerDisconnected(peer);
    }

    /// <summary>Removes a dead peer from this transport's list. Called from the network thread.</summary>
    protected virtual void CleanupPeer(CollabPeerConnection peer)
    {
    }

    private void DispatchFrame(CollabPeerConnection peer, ReadOnlySpan<byte> frame)
    {
        var type = (CollabProtocol.FrameType)frame[0];
        var payload = frame[1..];
        switch (type)
        {
            case CollabProtocol.FrameType.Json:
                if (CollabProtocol.TryDecodeJson(payload, out var json))
                    Inbound.Enqueue(new InboundMessage(peer, json));
                else
                    Log.Warning($"Collab: dropped malformed JSON frame from {peer}");
                break;

            case CollabProtocol.FrameType.Presence:
                // Presence is advisory display data; silently drop garbage instead of spamming the log.
                if (PresencePacket.TryDecode(payload, out var presence))
                    Inbound.Enqueue(new InboundMessage(peer, presence));
                break;

            case CollabProtocol.FrameType.Heartbeat:
                peer.MarkHeardFrom();
                if (payload.Length == 8)
                    EchoHeartbeat(peer, payload);
                break;

            default:
                Log.Warning($"Collab: peer {peer} sent unknown frame type {type}, disconnecting");
                peer.SessionClosed = true;
                break;
        }
    }

    protected abstract void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp);
}

/// <summary>
/// Host transport for direct LAN/online sessions: a TCP listener plus a UDP socket used for presence
/// telemetry and (optionally) LAN discovery replies. One reader thread per accepted peer.
/// </summary>
internal sealed class CollabHostTransport : CollabTransport
{
    private readonly List<CollabPeerConnection> _peers = [];
    private readonly TcpListener _listener;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cancellation = new();
    private int _nextPeerId = 1;

    public LanDiscoveryResponder DiscoveryResponder { get; }

    public override IReadOnlyList<CollabPeerConnection> Peers => _peers;

    protected override void CleanupPeer(CollabPeerConnection peer)
    {
        lock (_peers)
        {
            _peers.Remove(peer);
        }
    }

    public CollabHostTransport(int tcpPort, bool enableLanDiscovery, Func<JObject> announcementFactory)
    {
        _listener = new TcpListener(IPAddress.Any, tcpPort);
        _listener.Start();
        _udp = new UdpClient(tcpPort);
        _udp.Client.EnableBroadcast = false;

        if (enableLanDiscovery)
            DiscoveryResponder = new LanDiscoveryResponder(tcpPort, announcementFactory);

        _ = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        _ = Task.Run(() => UdpLoopAsync(_cancellation.Token));
        Log.Info($"Collab: hosting on port {tcpPort}");
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    /// <summary>Relays a presence datagram from one peer to all other peers over UDP.</summary>
    public void RelayPresence(CollabPeerConnection source, ReadOnlySpan<byte> payload)
    {
        foreach (var peer in _peers)
        {
            if (peer == source || !peer.IsAuthenticated)
                continue;

            SendPresenceTo(peer, payload);
        }
    }

    public void SendPresenceTo(CollabPeerConnection peer, ReadOnlySpan<byte> payload)
    {
        var endpoint = peer.UdpEndpoint;
        if (endpoint == null)
        {
            // No UDP pinhole (e.g. symmetric NAT or a relayed session) — fall back to the reliable
            // channel. Slightly higher latency, but correctness first.
            peer.Write(CollabProtocol.FrameType.Presence, payload);
            return;
        }

        try
        {
            _udp.Send(payload.ToArray(), payload.Length, endpoint);
        }
        catch (SocketException)
        {
            // ICMP unreachable etc. Presence is best-effort; the next tick retries.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                Log.Warning($"Collab: accept failed: {e.Message}");
                await Task.Delay(500, cancellation).ContinueWith(_ => { });
                continue;
            }

            if (_peers.Count(p => !p.SessionClosed) >= CollabProtocol.MaxPeers)
            {
                Log.Warning("Collab: refused connection, peer limit reached");
                client.Close();
                continue;
            }

            client.NoDelay = true;
            var peer = new CollabPeerConnection(client, Interlocked.Increment(ref _nextPeerId));
            lock (_peers)
            {
                _peers.Add(peer);
            }

            OnStatus($"Peer connecting from {(client.Client.RemoteEndPoint as IPEndPoint)?.Address}");
            _ = Task.Run(() => ReadLoop(peer), cancellation);
        }
    }

    private async Task UdpLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            var payload = result.Buffer;
            if (payload.Length < 27 || payload.Length > CollabProtocol.MaxPresencePayload)
                continue;

            var peerId = payload[0];
            CollabPeerConnection peer;
            lock (_peers)
            {
                peer = _peers.FirstOrDefault(p => p.AssignedId == peerId);
            }

            if (peer == null || !peer.IsAuthenticated)
                continue;

            // Learn the peer's UDP path: datagrams must originate from the address we already know
            // from TCP to prevent third parties from injecting presence into a session.
            var remoteIp = (result.RemoteEndPoint as IPEndPoint)?.Address;
            var tcpIp = (peer.Client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remoteIp == null || tcpIp == null || !remoteIp.Equals(tcpIp))
                continue;

            peer.UdpEndpoint = result.RemoteEndPoint;
            peer.MarkHeardFrom();

            var packet = PresencePacket.TryDecode(payload, out var presence) ? presence : null;
            if (packet == null)
                continue;

            Inbound.Enqueue(new InboundMessage(peer, packet));
            RelayPresence(peer, payload);
        }
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.Close();

            _peers.Clear();
        }

        DiscoveryResponder?.Dispose();
        try
        {
            _listener.Stop();
            _udp.Close();
        }
        catch
        {
            // Listener already dead.
        }
    }

    protected override void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp)
    {
        peer.Write(CollabProtocol.FrameType.Heartbeat, timestamp);
    }
}

/// <summary>
/// Client transport for direct sessions: one TCP connection to the host plus a bound UDP socket for
/// presence. The host's replies keep the NAT mapping open.
/// </summary>
internal sealed class CollabClientTransport : CollabTransport
{
    public CollabPeerConnection HostPeer => _hostPeer;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _hostEndPoint;
    private CollabPeerConnection _hostPeer;
    private readonly CancellationTokenSource _cancellation = new();

    public CollabClientTransport(IPEndPoint hostEndPoint)
    {
        _hostEndPoint = hostEndPoint;
        _udp = new UdpClient();
        _udp.Client.EnableBroadcast = false;
    }

    /// <summary>Connects synchronously; throws on failure so the manager can surface a clean error.</summary>
    public void Connect()
    {
        var client = new TcpClient();
        client.Connect(_hostEndPoint);
        client.NoDelay = true;
        _hostPeer = new CollabPeerConnection(client, 0);
        _ = Task.Run(() => ReadLoop(_hostPeer));
        _ = Task.Run(() => UdpLoopAsync(_cancellation.Token));
        Log.Info($"Collab: connected to {_hostEndPoint}");
    }

    public override IReadOnlyList<CollabPeerConnection> Peers => _hostPeer == null ? [] : [_hostPeer];

    public void SendPresence(ReadOnlySpan<byte> payload)
    {
        try
        {
            _udp.Send(payload.ToArray(), payload.Length, _hostEndPoint);
        }
        catch (SocketException)
        {
            // Best-effort channel.
        }
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    private async Task UdpLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            var payload = result.Buffer;
            if (payload.Length < 27 || payload.Length > CollabProtocol.MaxPresencePayload)
                continue;

            // Accept only datagrams that come back from the address we connected to.
            var remoteIp = (result.RemoteEndPoint as IPEndPoint)?.Address;
            if (remoteIp == null || !remoteIp.Equals(_hostEndPoint.Address))
                continue;

            if (PresencePacket.TryDecode(payload, out var presence))
                Inbound.Enqueue(new InboundMessage(_hostPeer, presence));
        }
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        _hostPeer?.Close();
        _udp.Close();
    }

    protected override void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp)
    {
        peer.Write(CollabProtocol.FrameType.Heartbeat, timestamp);
    }
}

/// <summary>
/// LAN discovery: answers UDP broadcast probes with a small JSON announcement. Runs on its own
/// socket (broadcast port = session TCP port + 1) so it can be enabled and disabled independently.
/// </summary>
internal sealed class LanDiscoveryResponder : IDisposable
{
    public const string ProbePayload = "TIXL-COLLAB-DISCOVER";
    public const int DiscoveryPortOffset = 1;

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<JObject> _announcementFactory;

    public LanDiscoveryResponder(int tcpPort, Func<JObject> announcementFactory)
    {
        _announcementFactory = announcementFactory;
        _udp = new UdpClient(tcpPort + DiscoveryPortOffset);
        _udp.Client.EnableBroadcast = true;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(result.Buffer);
            if (text != ProbePayload)
                continue;

            var announcement = _announcementFactory();
            var payload = CollabProtocol.EncodeJson(announcement);
            try
            {
                await _udp.SendAsync(payload, payload.Length, result.RemoteEndPoint);
            }
            catch (SocketException)
            {
                // The probing client may already be gone.
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _udp.Close();
    }
}

/// <summary>
/// LAN discovery scanner used by the join UI: broadcasts probes and collects announcements for a
/// couple of seconds. Fully independent of any running session.
/// </summary>
internal sealed class LanDiscoveryScanner : IDisposable
{
    public readonly ConcurrentQueue<(JObject Announcement, IPEndPoint Source, DateTime Received)> Results = new();

    private UdpClient _udp;
    private CancellationTokenSource _cancellation;
    private Task _loopTask;

    public void Start(int discoveryPort)
    {
        Stop();
        _cancellation = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.Client.EnableBroadcast = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        _loopTask = Task.Run(() => LoopAsync(discoveryPort, _cancellation.Token));
    }

    private async Task LoopAsync(int discoveryPort, CancellationToken cancellation)
    {
        var probe = Encoding.UTF8.GetBytes(LanDiscoveryResponder.ProbePayload);
        var interval = TimeSpan.FromMilliseconds(700);
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await _udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(interval);
                while (true)
                {
                    var result = await _udp.ReceiveAsync(timeout.Token);
                    var text = Encoding.UTF8.GetString(result.Buffer);
                    if (!text.StartsWith("{\"collab\""))
                        continue;

                    var json = JObject.Parse(text);
                    var source = result.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
                    Results.Enqueue((json, source, DateTime.UtcNow));
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // Receive timeout for this interval — probe again.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                try { await Task.Delay(200, cancellation); } catch (OperationCanceledException) { break; }
            }
            catch (JsonException)
            {
                // Some other tool answered our broadcast; ignore.
            }
        }
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _udp?.Close();
        try { _loopTask?.Wait(200); } catch { /* teardown */ }
        _cancellation?.Dispose();
        _cancellation = null;
        _udp = null;
        _loopTask = null;
    }

    public void Dispose() => Stop();
}
