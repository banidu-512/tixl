#nullable enable
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace T3.Editor.Collab;

/// <summary>
/// Relay-side transports for online sessions where neither end is publicly reachable. Both talk to
/// a standalone <c>TiXLCollabRelay</c> server:
///
/// - The host registers a room and receives member-join notifications as JSON control lines mixed
///   into its frame stream (detectable unambiguously: frames always start with a length prefix
///   whose first byte cannot be '{').
/// - Each client gets its own relay stream; the relay tags frames between host and members with a
///   slot byte so the host can demultiplex clients over its single relay connection.
/// - Presence runs over the reliable channel here; the UDP path is a direct-connection optimization.
///
/// Handshake, authentication and ops are unchanged end-to-end — the relay only splices bytes.
/// </summary>
internal static class CollabRelayWire
{
    public const byte BroadcastSlot = 0xFF;

    public const int SlotOffsetInHostFrame = 4; // [int32 length][byte slot][byte type][payload]

    public static void WriteHostFrame(Stream stream, object writeLock, byte slot, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        var total = payload.Length + 2;
        if (total > CollabProtocol.MaxFrameSize)
            throw new IOException($"Relay frame of {total} bytes exceeds the limit");

        Span<byte> prefix = stackalloc byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, total);
        prefix[4] = slot;
        prefix[5] = (byte)type;

        lock (writeLock)
        {
            stream.Write(prefix);
            stream.Write(payload);
            stream.Flush();
        }
    }

    public static async Task<(bool Ok, string Detail, byte Slot, JObject Reply)> ReadControlReplyAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var line = await ReadLineAsync(stream, cancellation);
        JObject json;
        try
        {
            json = JObject.Parse(line);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return (false, "relay sent a malformed reply", 0, new JObject());
        }

        var ok = json["ok"]?.Value<bool>() ?? false;
        var detail = json["error"]?.Value<string>() ?? "";
        var slot = json["slot"]?.Value<byte>() ?? 0;
        return (ok, detail, slot, json);
    }

    public static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var sb = new StringBuilder(64);
        var buffer = new byte[1];
        while (sb.Length < 256)
        {
            var read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
                throw new IOException("relay closed the connection during handshake");

            if (buffer[0] == '\n')
                return sb.ToString().TrimEnd('\r');

            sb.Append((char)buffer[0]);
        }

        throw new IOException("relay handshake line too long");
    }
}

/// <summary>
/// Host side: one connection to the relay carries all members. Virtual
/// <see cref="CollabTransport.CollabPeerConnection"/>s are created per relay slot; the rest of the
/// session machinery (handshake, ops, presence, sync) works unchanged against them.
/// </summary>
internal sealed class CollabRelayHostTransport : CollabTransport
{
    public string AssignedRoomCode { get; private set; } = "";

    public override IReadOnlyList<CollabPeerConnection> Peers => _peers;

    private readonly List<CollabPeerConnection> _peers = [];
    private readonly TcpClient _relayClient;
    private readonly NetworkStream _relayStream;
    private readonly object _relayWriteLock = new();
    private readonly CancellationTokenSource _cancellation = new();

    private CollabRelayHostTransport(TcpClient relayClient, string assignedRoomCode)
    {
        _relayClient = relayClient;
        _relayStream = relayClient.GetStream();
        AssignedRoomCode = assignedRoomCode;
        _ = Task.Run(() => ReadRelayLoopAsync(_cancellation.Token));
    }

    /// <summary>Connects to the relay and registers as a room owner. Throws with a readable message on failure.</summary>
    public static async Task<CollabRelayHostTransport> ConnectAsync(string relayHost, int relayPort, string roomCode, CancellationToken cancellation)
    {
        var client = new TcpClient();
        await client.ConnectAsync(relayHost, relayPort, cancellation);
        client.NoDelay = true;
        var stream = client.GetStream();

        var request = string.IsNullOrEmpty(roomCode)
                          ? "{\"role\":\"host\",\"room\":\"\"}\n"
                          : $"{{\"role\":\"host\",\"room\":\"{roomCode.Replace("\"", "")}\"}}\n";
        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes, cancellation);

        var (ok, detail, _, reply) = await CollabRelayWire.ReadControlReplyAsync(stream, cancellation);
        if (!ok)
            throw new Exception(string.IsNullOrEmpty(detail) ? "relay refused the room" : detail);

        var room = reply["room"]?.Value<string>() ?? "";
        return new CollabRelayHostTransport(client, room);
    }    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        if (peer.SessionClosed)
            return;

        CollabRelayWire.WriteHostFrame(_relayStream, _relayWriteLock, peer.PresencePeerId, type, payload);
    }

    /// <summary>Presence for relayed peers always rides the reliable channel.</summary>
    public void SendPresenceTo(CollabPeerConnection peer, ReadOnlySpan<byte> payload)
    {
        SendToPeer(peer, CollabProtocol.FrameType.Presence, payload);
    }

    private async Task ReadRelayLoopAsync(CancellationToken cancellation)
    {
        var buffer = new byte[CollabProtocol.MaxFrameSize];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Control lines and frames share the stream; a frame length prefix can never start
                // with '{' (that would be a >2 GiB length), so the discriminator is unambiguous.
                var first = _relayStream.ReadByte();
                if (first < 0)
                    break;

                if (first == '{')
                {
                    var line = await ReadRestOfLineAsync((byte)first, cancellation);
                    HandleControlLine(line);
                    continue;
                }

                var lengthBytes = new byte[4];
                lengthBytes[0] = (byte)first;
                if (!await CollabProtocol.TryReadExactAsync(_relayStream, lengthBytes.AsMemory(1), cancellation))
                    break;

                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length < 2 || length > CollabProtocol.MaxFrameSize)
                {
                    Log.Warning($"Collab relay: illegal frame length {length}, disconnecting");
                    break;
                }

                if (!await CollabProtocol.TryReadExactAsync(_relayStream, buffer.AsMemory(0, length), cancellation))
                    break;

                DispatchRelayFrame(buffer.AsSpan(0, length));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Debug($"Collab relay: host link ended: {e.Message}");
        }

        _cancellation.Cancel();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.SessionClosed = true;

            _peers.Clear();
        }

        OnPeerDisconnected(new CollabPeerConnection(Stream.Null, int.MaxValue, null));
    }

    private async Task<string> ReadRestOfLineAsync(byte firstByte, CancellationToken cancellation)
    {
        var sb = new StringBuilder(64);
        sb.Append((char)firstByte);
        var buffer = new byte[1];
        while (sb.Length < 256)
        {
            var read = await _relayStream.ReadAsync(buffer, cancellation);
            if (read == 0)
                throw new IOException("relay closed during control line");

            if (buffer[0] == '\n')
                return sb.ToString();

            sb.Append((char)buffer[0]);
        }

        throw new IOException("relay control line too long");
    }

    private void HandleControlLine(string line)
    {
        try
        {
            var json = JObject.Parse(line);
            var type = json["t"]?.Value<string>();
            if (type == "member")
            {
                var slot = json["slot"]?.Value<int>() ?? 0;
                lock (_peers)
                {
                    if (_peers.Any(p => p.AssignedId == slot))
                        return;

                    _peers.Add(new CollabPeerConnection(_relayStream, slot, () => { }));
                }

                OnStatus($"Peer joining via relay (slot {slot})");
            }
        }
        catch (Newtonsoft.Json.JsonException)
        {
            Log.Debug($"Collab relay: ignoring malformed control line '{line}'");
        }
    }

    private void DispatchRelayFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
            return;

        var slot = frame[0];
        var type = (CollabProtocol.FrameType)frame[1];
        var payload = frame[2..];

        CollabPeerConnection peer;
        lock (_peers)
        {
            peer = _peers.FirstOrDefault(p => p.AssignedId == slot);
        }

        if (peer == null)
            return;

        peer.MarkHeardFrom();
        switch (type)
        {
            case CollabProtocol.FrameType.Json:
                if (CollabProtocol.TryDecodeJson(payload, out var json))
                    Inbound.Enqueue(new InboundMessage(peer, json));
                break;

            case CollabProtocol.FrameType.Presence:
                if (PresencePacket.TryDecode(payload, out var presence))
                    Inbound.Enqueue(new InboundMessage(peer, presence));
                break;

            case CollabProtocol.FrameType.Heartbeat:
                // Echo so the client's timeout logic sees life on the relay path.
                CollabRelayWire.WriteHostFrame(_relayStream, _relayWriteLock, slot, CollabProtocol.FrameType.Heartbeat, payload);
                break;
        }
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.SessionClosed = true;

            _peers.Clear();
        }

        try
        {
            _relayClient.Close();
        }
        catch
        {
            // Relay link already gone.
        }
    }

    protected override void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp)
    {
        SendToPeer(peer, CollabProtocol.FrameType.Heartbeat, timestamp);
    }
}

/// <summary>
/// Client side: behaves like the direct client transport, but the single stream goes through the
/// relay and presence rides the reliable channel.
/// </summary>
internal sealed class CollabRelayClientTransport : CollabTransport
{
    public CollabPeerConnection HostPeer => _hostPeer;

    private CollabPeerConnection _hostPeer;
    private readonly TcpClient _relayClient;
    private readonly CancellationTokenSource _cancellation = new();

    private CollabRelayClientTransport(TcpClient relayClient)
    {
        _relayClient = relayClient;
        _hostPeer = new CollabPeerConnection(relayClient.GetStream(), 0, null);
    }

    /// <summary>Connects to the relay and joins the room. Throws with a readable message on failure.</summary>
    public static async Task<CollabRelayClientTransport> ConnectAsync(string relayHost, int relayPort, string roomCode, CancellationToken cancellation)
    {
        var client = new TcpClient();
        await client.ConnectAsync(relayHost, relayPort, cancellation);
        client.NoDelay = true;
        var stream = client.GetStream();

        var request = $"{{\"role\":\"join\",\"room\":\"{roomCode.Trim().Replace("\"", "")}\"}}\n";
        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes, cancellation);

        var (ok, detail, _, _) = await CollabRelayWire.ReadControlReplyAsync(stream, cancellation);
        if (!ok)
            throw new Exception(string.IsNullOrEmpty(detail) ? "could not join room" : detail);

        var transport = new CollabRelayClientTransport(client);
        transport.StartReadLoop();
        return transport;
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    /// <summary>The relay stream carries plain framed traffic once joined — reuse the standard read loop.</summary>
    internal void StartReadLoop() => _ = Task.Run(() => ReadLoop(_hostPeer));

    /// <summary>No UDP pinhole through the relay — presence rides the reliable channel.</summary>
    public void SendPresence(ReadOnlySpan<byte> payload)
    {
        SendToPeer(_hostPeer, CollabProtocol.FrameType.Presence, payload);
    }

    public override IReadOnlyList<CollabPeerConnection> Peers => _hostPeer == null ? [] : [_hostPeer];

    public override void Dispose()
    {
        _cancellation.Cancel();
        _hostPeer?.Close();
        _relayClient?.Close();
    }

    protected override void EchoHeartbeat(CollabPeerConnection peer, ReadOnlySpan<byte> timestamp)
    {
        peer.Write(CollabProtocol.FrameType.Heartbeat, timestamp);
    }
}
