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

    public override IReadOnlyList<CollabPeerConnection> Peers => _peerSnapshot;

    private readonly List<CollabPeerConnection> _peers = [];

    /** Copy of _peers republished on every change, so the UI thread enumerates without locking. */
    private volatile CollabPeerConnection[] _peerSnapshot = [];

    private readonly TcpClient _relayClient;
    private readonly NetworkStream _relayStream;
    private readonly object _relayWriteLock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Timer _keepAliveTimer;

    /** The relay drops connections idle for 90 s; a host waiting for its first member sends nothing else. */
    private const int KeepAliveIntervalMs = 15_000;

    /** Never assigned to a member: the relay discards frames for it after counting them as activity. */
    private const byte KeepAliveSlot = 0;

    private CollabRelayHostTransport(TcpClient relayClient, string assignedRoomCode)
    {
        _relayClient = relayClient;
        _relayStream = relayClient.GetStream();
        AssignedRoomCode = assignedRoomCode;
        _keepAliveTimer = new Timer(_ => SendKeepAlive(), null, KeepAliveIntervalMs, KeepAliveIntervalMs);
    }

    /// <summary>
    /// Starts reading relay traffic. Must run after <see cref="CollabTransport.PeerDisconnected"/> is
    /// wired, or a link that dies during the connect hand-off reports nothing.
    /// </summary>
    public void StartReading() => _ = Task.Run(() => ReadRelayLoopAsync(_cancellation.Token));

    /// <summary>Connects to the relay and registers as a room owner. Throws with a readable message on failure.</summary>
    public static async Task<CollabRelayHostTransport> ConnectAsync(string relayHost, int relayPort, string roomCode, CancellationToken cancellation)
    {
        var client = new TcpClient();
        try
        {
            // A relay that accepts but never answers must not leave the session stuck in Connecting.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(CollabProtocol.ConnectTimeoutMs);
            await client.ConnectAsync(relayHost, relayPort, timeout.Token);
            client.NoDelay = true;
            client.SendTimeout = CollabProtocol.WriteTimeoutMs;
            var stream = client.GetStream();

            var request = string.IsNullOrEmpty(roomCode)
                              ? "{\"role\":\"host\",\"room\":\"\"}\n"
                              : $"{{\"role\":\"host\",\"room\":\"{roomCode.Replace("\"", "")}\"}}\n";
            var bytes = Encoding.UTF8.GetBytes(request);
            await stream.WriteAsync(bytes, timeout.Token);

            var (ok, detail, _, reply) = await CollabRelayWire.ReadControlReplyAsync(stream, timeout.Token);
            if (!ok)
                throw new Exception(string.IsNullOrEmpty(detail) ? "relay refused the room" : detail);

            var room = reply["room"]?.Value<string>() ?? "";
            return new CollabRelayHostTransport(client, room);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        if (peer.SessionClosed)
            return;

        WriteToRelay(peer.PresencePeerId, type, payload);
    }

    /// <summary>
    /// Writes run on the UI thread and on the keep-alive timer; a dead relay link must not throw
    /// into them. Closing the socket ends the read loop, which reports the lost relay.
    /// </summary>
    private void WriteToRelay(byte slot, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        try
        {
            CollabRelayWire.WriteHostFrame(_relayStream, _relayWriteLock, slot, type, payload);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Log.Debug($"Collab relay: write failed: {e.Message}");
            CloseRelayLink();
        }
    }

    private void SendKeepAlive()
    {
        if (_cancellation.IsCancellationRequested)
            return;

        Span<byte> timestamp = stackalloc byte[CollabProtocol.HeartbeatPingSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(timestamp, Environment.TickCount64);
        WriteToRelay(KeepAliveSlot, CollabProtocol.FrameType.Heartbeat, timestamp);
    }

    private void CloseRelayLink()
    {
        try
        {
            _relayClient.Close();
        }
        catch
        {
            // Relay link already gone.
        }
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
        _keepAliveTimer.Dispose();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.SessionClosed = true;

            _peers.Clear();
            _peerSnapshot = [];
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
            var type = CollabProtocol.GetString(json, "t");
            if (!CollabProtocol.TryGetInt(json, "slot", out var slot) || slot <= 0 || slot >= CollabRelayWire.BroadcastSlot)
                return;

            if (type == "member")
            {
                // A slot the relay hands out again belongs to a new connection: the previous
                // holder is gone, and its authentication must not carry over.
                RemovePeer(FindPeer(slot));

                CollabPeerConnection? member = null;
                member = new CollabPeerConnection(_relayStream, slot, () => RemovePeer(member));
                lock (_peers)
                {
                    _peers.Add(member);
                    _peerSnapshot = _peers.ToArray();
                }

                OnStatus($"Peer joining via relay (slot {slot})");
            }
            else if (type == "left")
            {
                var peer = FindPeer(slot);
                if (peer != null)
                {
                    peer.SessionClosed = true;
                    RemovePeer(peer);
                }
            }
        }
        catch (Newtonsoft.Json.JsonException)
        {
            Log.Debug($"Collab relay: ignoring malformed control line '{line}'");
        }
    }

    private CollabPeerConnection? FindPeer(int slot)
    {
        foreach (var peer in _peerSnapshot)
        {
            if (peer.AssignedId == slot)
                return peer;
        }

        return null;
    }

    /// <summary>Drops a relayed member (left, timed out or rejected) and reports it once.</summary>
    private void RemovePeer(CollabPeerConnection? peer)
    {
        if (peer == null)
            return;

        lock (_peers)
        {
            if (!_peers.Remove(peer))
                return;

            _peerSnapshot = _peers.ToArray();
        }

        peer.SessionClosed = true;
        OnPeerDisconnected(peer);
    }

    private void DispatchRelayFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
            return;

        var slot = frame[0];
        var type = (CollabProtocol.FrameType)frame[1];
        var payload = frame[2..];

        var peer = FindPeer(slot);
        if (peer == null || peer.SessionClosed)
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
                // Answer pings so the client's timeout logic sees life on the relay path; answers
                // are one byte longer and never answered, or heartbeats would bounce forever.
                if (payload.Length == CollabProtocol.HeartbeatPingSize)
                {
                    Span<byte> answer = stackalloc byte[CollabProtocol.HeartbeatPingSize + 1];
                    payload.CopyTo(answer);
                    answer[CollabProtocol.HeartbeatPingSize] = 0; // explicit: stackalloc is not guaranteed zeroed
                    WriteToRelay(slot, CollabProtocol.FrameType.Heartbeat, answer);
                }

                break;
        }
    }

    public override void Dispose()
    {
        _cancellation.Cancel();
        _keepAliveTimer.Dispose();
        lock (_peers)
        {
            foreach (var peer in _peers)
                peer.SessionClosed = true;

            _peers.Clear();
            _peerSnapshot = [];
        }

        CloseRelayLink();
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
        try
        {
            // A relay that accepts but never answers must not leave the session stuck in Connecting.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(CollabProtocol.ConnectTimeoutMs);
            await client.ConnectAsync(relayHost, relayPort, timeout.Token);
            client.NoDelay = true;
            client.SendTimeout = CollabProtocol.WriteTimeoutMs;
            var stream = client.GetStream();

            var request = $"{{\"role\":\"join\",\"room\":\"{roomCode.Trim().Replace("\"", "")}\"}}\n";
            var bytes = Encoding.UTF8.GetBytes(request);
            await stream.WriteAsync(bytes, timeout.Token);

            var (ok, detail, _, _) = await CollabRelayWire.ReadControlReplyAsync(stream, timeout.Token);
            if (!ok)
                throw new Exception(string.IsNullOrEmpty(detail) ? "could not join room" : detail);

            var transport = new CollabRelayClientTransport(client);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public override void SendToPeer(CollabPeerConnection peer, CollabProtocol.FrameType type, ReadOnlySpan<byte> payload)
    {
        peer.Write(type, payload);
    }

    /// <summary>
    /// The relay stream carries plain framed traffic once joined — the standard read loop. Start it
    /// only after PeerDisconnected is wired, so an immediately dying link is reported.
    /// </summary>
    public void StartReading() => _ = Task.Run(() => ReadLoop(_hostPeer));

    /// <summary>No UDP pinhole through the relay — presence rides the reliable channel.</summary>
    public void SendPresence(ReadOnlySpan<byte> payload)
    {
        SendToPeer(_hostPeer, CollabProtocol.FrameType.Presence, payload);
    }

    public override IReadOnlyList<CollabPeerConnection> Peers => _hostPeer == null ? [] : [_hostPeer];

    public override CollabPeerConnection? HostLink => _hostPeer;

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
