#nullable enable
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using T3.Editor.Collab;

namespace T3.Editor.Show;

/// <summary>
/// Transport for boygroup sessions, reusing the collab framing/auth primitives: a TCP server (the
/// show master) plus UDP discovery that announces the server's groups. Same threading contract as
/// the collab transports — socket threads only enqueue inbound messages; the manager consumes them
/// on the UI thread.
/// </summary>
internal abstract class BoygroupTransport : CollabTransport
{
}

/// <summary>The show master: accepts clients and announces groups via LAN discovery.</summary>
internal sealed class BoygroupServerTransport : BoygroupTransport
{
    public override IReadOnlyList<CollabPeerConnection> Peers => _peers;

    private readonly List<CollabPeerConnection> _peers = [];
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private int _nextPeerId = 1;

    public LanDiscoveryResponder DiscoveryResponder { get; }

    public BoygroupServerTransport(int tcpPort, string[] groups)
    {
        _listener = new TcpListener(IPAddress.Any, tcpPort);
        _listener.Start();
        DiscoveryResponder = new LanDiscoveryResponder(tcpPort + LanDiscoveryResponder.DiscoveryPortOffset,
                                                       () => new JObject
                                                                 {
                                                                     ["boygroup"] = true,
                                                                     ["server"] = Environment.MachineName,
                                                                     ["port"] = tcpPort,
                                                                     ["groups"] = new JArray(groups),
                                                                 });
        _ = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        Log.Info($"Boygroup: server listening on port {tcpPort}");
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    protected override void CleanupPeer(CollabPeerConnection peer)
    {
        lock (_peers)
        {
            _peers.Remove(peer);
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
            catch (SocketException)
            {
                await Task.Delay(500, CancellationToken.None);
                continue;
            }

            client.NoDelay = true;
            var peer = new CollabPeerConnection(client, Interlocked.Increment(ref _nextPeerId));
            lock (_peers)
            {
                _peers.Add(peer);
            }

            _ = Task.Run(() => ReadLoop(peer), CancellationToken.None);
        }
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        DiscoveryResponder?.Dispose();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.Close();

            _peers.Clear();
        }

        try
        {
            _listener.Stop();
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

/// <summary>A boygroup client's connection to the show master.</summary>
internal sealed class BoygroupClientTransport : BoygroupTransport
{
    public CollabPeerConnection ServerPeer => _serverPeer;

    private readonly CollabPeerConnection _serverPeer;
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _cancellation = new();

    private BoygroupClientTransport(TcpClient client)
    {
        _client = client;
        _serverPeer = new CollabPeerConnection(client.GetStream(), 0, null);
    }

    public static BoygroupClientTransport Connect(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        client.Connect(endPoint);
        client.NoDelay = true;
        var transport = new BoygroupClientTransport(client);
        _ = Task.Run(() => transport.ReadLoop(transport._serverPeer), CancellationToken.None);
        return transport;
    }

    public override IReadOnlyList<CollabPeerConnection> Peers => [_serverPeer];

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        _serverPeer.Close();
        try
        {
            _client.Close();
        }
        catch
        {
            // Socket already dead.
        }
    }

    protected override void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp)
    {
        peer.Write(CollabProtocol.FrameType.Heartbeat, timestamp);
    }
}
