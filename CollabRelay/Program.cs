// TiXL Collab Relay — a tiny standalone room relay for online collaboration sessions.
//
// Why this exists: direct connections need the host to be reachable (open port, VPN). This relay
// lets a host and clients meet behind NATs: the host registers a room code, clients join with it,
// and the relay carries their framed traffic. All collaboration traffic (including password
// authentication) stays between the TiXL instances; the relay only routes frames and never
// interprets them.
//
// Wire protocol (TiXL side frames are [int32 length][byte type][payload], length includes type):
// - Member connections: pure TiXL framing both ways. The relay reads member frames and re-tags
//   them toward the host as [length][slot][type][payload] (slot = member id, length includes it).
// - Host connection: receives tagged frames plus JSON control lines ({"t":"member","slot":N})
//   announcing joins. Control lines and frames are distinguishable unambiguously: a frame length
//   prefix can never start with '{'. Host sends are addressed to one slot; 0xFF broadcasts to all.
//
// Trust model: the relay operator can observe traffic, like the operator of any game server.
// Run your own relay (this builds into a single self-contained executable) for sensitive sessions.
//
// Usage: dotnet run --project CollabRelay [-- <port>]   (default port 44710)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace T3.CollabRelay;

internal static class Program
{
    private const int DefaultPort = 44710;
    private const int MaxMembersPerRoom = 16;
    private const int MaxFrameSize = 4 * 1024 * 1024;
    private const int HandshakeTimeoutMs = 10_000;
    private const int IdleTimeoutMs = 90_000;
    private const int WriteTimeoutMs = 10_000;
    private const byte BroadcastSlot = 0xFF;

    private static readonly ConcurrentDictionary<string, Room> Rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One writer at a time per socket. The host socket is written by every member's routing task
    /// plus join announcements; unserialized, two frames interleave and corrupt the host's stream.
    /// </summary>
    private static readonly ConditionalWeakTable<TcpClient, SemaphoreSlim> WriteGates = new();

    private static long _lastActivityTicks = Environment.TickCount64;

    private static async Task Main(string[] args)
    {
        var port = DefaultPort;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed))
            port = parsed;

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"TiXL collab relay listening on port {port}");
        Console.WriteLine("Note: the relay routes but cannot authenticate collaboration sessions; password protection is end-to-end between the TiXL instances.");

        _ = Task.Run(ReaperLoopAsync);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (SocketException e)
            {
                Console.WriteLine($"accept failed: {e.Message}");
                continue;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Connection handshake: one newline-delimited JSON control line, then routed frames.
    // ---------------------------------------------------------------------------------------

    private static async Task HandleConnectionAsync(TcpClient client)
    {
        string? role = null;
        Room? room = null;
        var slot = (byte)0;
        try
        {
            // A connection that opens and then says nothing must not hold a task and a socket forever.
            using var handshake = new CancellationTokenSource(HandshakeTimeoutMs);
            var line = await ReadLineAsync(client.GetStream(), handshake.Token);
            var request = ParseRequest(line);
            if (request == null)
            {
                await WriteLineAsync(client, "{\"ok\":false,\"error\":\"malformed request\"}");
                client.Close();
                return;
            }

            role = request.Value.role;
            var roomCode = request.Value.room;

            if (role == "host")
            {
                // The reply must reach the host before any member announcement: the socket's write
                // gate is held from claiming the room until the reply is written.
                var gate = WriteGateOf(client);
                await gate.WaitAsync();
                try
                {
                    room = CreateRoom(roomCode, client);
                    await WriteRawLineAsync(client, $"{{\"ok\":true,\"room\":\"{room.Code}\"}}");
                }
                finally
                {
                    gate.Release();
                }

                Console.WriteLine($"room {room.Code} registered");
            }
            else if (role == "join")
            {
                // Likewise a member is routable as soon as it is in the room; hold its write gate
                // so no host frame overtakes the join reply.
                var gate = WriteGateOf(client);
                await gate.WaitAsync();
                try
                {
                    var joined = TryJoinRoom(roomCode, client, out room, out slot);
                    if (!joined)
                    {
                        var reason = room == null ? "room not found" : "room full";
                        room = null;
                        await WriteRawLineAsync(client, $"{{\"ok\":false,\"error\":\"{reason}\"}}");
                        client.Close();
                        return;
                    }

                    await WriteRawLineAsync(client, $"{{\"ok\":true,\"room\":\"{room!.Code}\",\"slot\":{slot}}}");
                }
                finally
                {
                    gate.Release();
                }

                // Tell the host about the new member before any of its frames arrive.
                var owner = room.Owner;
                if (owner != null)
                    await WriteLineAsync(owner, $"{{\"t\":\"member\",\"slot\":{slot}}}");

                Console.WriteLine($"member {slot} joined {room.Code} ({room.MemberCount}/{MaxMembersPerRoom})");
            }
            else
            {
                await WriteLineAsync(client, "{\"ok\":false,\"error\":\"unknown role\"}");
                client.Close();
                return;
            }

            TouchActivity();
            await RouteFramesAsync(client, room, slot, role == "host");
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Normal disconnect path (including a handshake or read that timed out).
        }
        catch (Exception e)
        {
            Console.WriteLine($"connection error: {e.Message}");
        }
        finally
        {
            if (room != null)
            {
                // A departing member keeps its slot until its "left" line has been queued on the
                // host's serialized write gate. Freeing the slot first would let a newcomer take
                // it and announce itself before the stale "left", and the host would drop the
                // newcomer when that line arrived. Holding the entry (not the room gate) for the
                // duration keeps joins and routing moving while preserving the order.
                if (role == "join" && IsRoomMember(room, slot, client))
                    await NotifyMemberLeftAsync(room, slot);

                RemoveClient(room, client, role == "host", slot);
            }

            client.Close();
        }
    }

    /// <summary>Tells the host a member is gone so it drops the slot instead of waiting for a timeout.</summary>
    private static async Task NotifyMemberLeftAsync(Room room, byte slot)
    {
        var owner = room.Owner;
        if (owner is not { Connected: true })
            return;

        try
        {
            await WriteLineAsync(owner, $"{{\"t\":\"left\",\"slot\":{slot}}}");
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The host link is failing too; its own connection task cleans up.
        }
    }

    private static Room CreateRoom(string requestedCode, TcpClient owner)
    {
        var code = SanitizeCode(requestedCode);
        while (true)
        {
            var room = Rooms.GetOrAdd(code, _ => new Room(code));

            // Claimed under the room's gate: two hosts asking for the same code must not both own it,
            // and a room whose host just left (and was removed) must not be revived.
            lock (room.Gate)
            {
                if (room.Owner == null && Rooms.TryGetValue(code, out var current) && current == room)
                {
                    room.Owner = owner;
                    return room;
                }
            }

            // Code collision with a live room — mint a fresh one for the new host.
            code = GenerateRoomCode();
        }
    }

    private static bool TryJoinRoom(string roomCode, TcpClient client, out Room? room, out byte slot)
    {
        room = null;
        slot = 0;
        if (!Rooms.TryGetValue(SanitizeCode(roomCode), out var found) || found.Owner == null || !found.Owner.Connected)
            return false;

        lock (found.Gate)
        {
            if (found.Members.Count >= MaxMembersPerRoom)
                return false;

            room = found;
            slot = found.AllocateSlot();
            found.Members.Add(slot, client);
        }

        return true;
    }

    private static string SanitizeCode(string code)
    {
        var cleaned = new string((code ?? "").Where(char.IsLetterOrDigit).Take(12).ToArray());
        return cleaned.Length == 0 ? GenerateRoomCode() : cleaned.ToUpperInvariant();
    }

    /// <summary>True while this connection still owns the slot; checked before the left announcement.</summary>
    private static bool IsRoomMember(Room room, byte slot, TcpClient client)
    {
        lock (room.Gate)
        {
            return room.Members.TryGetValue(slot, out var member) && member == client;
        }
    }

    /// <summary>Returns true when a member left (as opposed to the host).</summary>
    private static bool RemoveClient(Room room, TcpClient client, bool isOwner, byte slot)
    {
        lock (room.Gate)
        {
            if (isOwner)
            {
                if (room.Owner == client)
                {
                    room.Owner = null;
                    Console.WriteLine($"room {room.Code} closed (host left)");
                    Rooms.TryRemove(room.Code, out _);
                }

                return false;
            }

            return room.Members.TryGetValue(slot, out var member) && member == client && room.Members.Remove(slot);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Frame routing
    // ---------------------------------------------------------------------------------------

    private static async Task RouteFramesAsync(TcpClient client, Room room, byte slot, bool isHost)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        // A peer that stops sending mid-frame is gone; without a read deadline its socket would
        // stay open (and its room slot occupied) until the process ends.
        using var idle = new CancellationTokenSource();
        var token = idle.Token;

        while (true)
        {
            idle.CancelAfter(IdleTimeoutMs);

            // On the host connection, a frame may be interleaved with control lines; a length
            // prefix can never start with '{', so read the discriminator byte first.
            if (isHost)
            {
                if (!await TryReadExactAsync(stream, lengthBuffer.AsMemory(0, 1), token))
                    return;

                if (lengthBuffer[0] == (byte)'{')
                {
                    // Control lines are only ever sent TO the host; the host never sends them.
                    // Drain and ignore (join announcements for already-known slots).
                    continue;
                }

                if (!await TryReadExactAsync(stream, lengthBuffer.AsMemory(1), token))
                    return;
            }
            else if (!await TryReadExactAsync(stream, lengthBuffer, token))
            {
                return;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length < 1 || length > MaxFrameSize)
            {
                Console.WriteLine($"illegal frame length {length} from {(isHost ? "host" : $"slot {slot}")}, dropping connection");
                return;
            }

            var frame = new byte[length];
            if (!await TryReadExactAsync(stream, frame, token))
                return;

            TouchActivity();

            if (isHost)
            {
                // Host -> member: [slot][type][payload]. Route or broadcast.
                if (length < 2)
                    continue;

                var targetSlot = frame[0];
                var payload = frame[1..];
                var payloadLength = length - 1;

                if (targetSlot == BroadcastSlot)
                {
                    foreach (var member in room.MembersSnapshot())
                    {
                        if (!member.Value.Connected)
                            continue;

                        // One member that stopped reading (suspended laptop, dead Wi-Fi) must not
                        // stall the broadcast for everyone else - drop it and let it rejoin.
                        try
                        {
                            await WriteFrameAsync(member.Value, payloadLength, payload);
                        }
                        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                        {
                            Console.WriteLine($"slot {member.Key} stalled ({e.GetType().Name}), dropping");
                            member.Value.Close();
                        }
                    }
                }
                else if (room.TryGetMember(targetSlot, out var target) && target.Connected)
                {
                    try
                    {
                        await WriteFrameAsync(target, payloadLength, payload);
                    }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        Console.WriteLine($"slot {targetSlot} stalled ({e.GetType().Name}), dropping");
                        target.Close();
                    }
                }
            }
            else
            {
                // Member -> host: retag with the slot so the host can demultiplex.
                var owner = room.Owner;
                if (owner is { Connected: true })
                {
                    try
                    {
                        await WriteTaggedFrameAsync(owner, slot, length, frame);
                    }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        // A frame write that failed (or timed out) may have left part of a frame
                        // on the host stream; the host's framing cannot recover from that, so
                        // the host link has to be re-established instead of silently desynced.
                        Console.WriteLine($"host write failed ({e.GetType().Name}), closing the host link");
                        owner.Close();
                        return;
                    }
                }
            }
        }
    }

    private static async Task WriteFrameAsync(TcpClient target, int length, byte[] frameWithType)
    {
        using var timeout = new CancellationTokenSource(WriteTimeoutMs);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        var gate = WriteGateOf(target);
        await gate.WaitAsync(timeout.Token);
        try
        {
            var stream = target.GetStream();
            await stream.WriteAsync(prefix, timeout.Token);
            await stream.WriteAsync(frameWithType.AsMemory(0, length), timeout.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteTaggedFrameAsync(TcpClient owner, byte slot, int length, byte[] frameWithType)
    {
        using var timeout = new CancellationTokenSource(WriteTimeoutMs);
        // [length + 1][slot][original frame bytes]
        var prefix = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length + 1);
        prefix[4] = slot;
        var gate = WriteGateOf(owner);
        await gate.WaitAsync(timeout.Token);
        try
        {
            var stream = owner.GetStream();
            await stream.WriteAsync(prefix, timeout.Token);
            await stream.WriteAsync(frameWithType, timeout.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim WriteGateOf(TcpClient client) => WriteGates.GetValue(client, _ => new SemaphoreSlim(1, 1));

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read <= 0)
                return false;

            offset += read;
        }

        return true;
    }

    private static void TouchActivity() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    private static async Task ReaperLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks) < TimeSpan.FromMinutes(30).TotalMilliseconds)
                continue;

            Console.WriteLine("relay idle past timeout, clearing rooms");
            Rooms.Clear();
            TouchActivity();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Control channel helpers
    // ---------------------------------------------------------------------------------------

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var sb = new StringBuilder(64);
        var buffer = new byte[1];
        while (sb.Length < 256)
        {
            var read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
                throw new IOException("closed before handshake");

            if (buffer[0] == '\n')
                return sb.ToString().TrimEnd('\r');

            sb.Append((char)buffer[0]);
        }

        throw new IOException("handshake line too long");
    }

    private static async Task WriteLineAsync(TcpClient client, string line)
    {
        var gate = WriteGateOf(client);
        using var timeout = new CancellationTokenSource(WriteTimeoutMs);
        try
        {
            await gate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // No bytes were written while waiting for the gate.
            return;
        }

        try
        {
            await WriteRawLineAsync(client, line, timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException)
        {
            // A line write cancelled or failed mid-way leaves a partial line; every later line
            // would be appended to it and be unparseable, so the stream cannot be used any more.
            client.Close();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Writes a control line; the caller holds the socket's write gate.</summary>
    private static async Task WriteRawLineAsync(TcpClient client, string line, CancellationToken cancellation = default)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await client.GetStream().WriteAsync(bytes, cancellation);
    }

    private static (string role, string room)? ParseRequest(string line)
    {
        // Minimal parse for {"role":"host|join","room":"..."} without pulling in JSON deps.
        if (string.IsNullOrEmpty(line))
            return null;

        string? Extract(string key)
        {
            var needle = $"\"{key}\"";
            var keyIndex = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return null;

            var colonIndex = line.IndexOf(':', keyIndex + needle.Length);
            if (colonIndex < 0)
                return null;

            var quoteStart = line.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0)
                return null;

            var quoteEnd = line.IndexOf('"', quoteStart + 1);
            return quoteEnd < 0 ? null : line[(quoteStart + 1)..quoteEnd];
        }

        var role = Extract("role");
        if (role == null)
            return null;

        return (role, Extract("room") ?? "");
    }

    private static string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = alphabet[Random.Shared.Next(0, alphabet.Length)];
        }

        return new string(code);
    }

    internal sealed class Room(string code)
    {
        public readonly string Code = code;
        public readonly object Gate = new();
        public readonly Dictionary<byte, TcpClient> Members = [];
        public TcpClient? Owner;
        private byte _nextSlot = 1;

        public int MemberCount => Members.Count;

        /// <summary>Next free slot in 1..254; the caller holds <see cref="Gate"/> and has checked capacity.</summary>
        public byte AllocateSlot()
        {
            // Slots wrap after 254 joins; skip ones a long-lived member still holds.
            while (true)
            {
                var slot = _nextSlot;
                _nextSlot = (byte)((_nextSlot + 1) % 0xFF);
                if (_nextSlot == 0)
                    _nextSlot = 1;

                if (!Members.ContainsKey(slot))
                    return slot;
            }
        }

        public bool TryGetMember(byte slot, out TcpClient member)
        {
            lock (Gate)
            {
                return Members.TryGetValue(slot, out member!);
            }
        }

        public List<KeyValuePair<byte, TcpClient>> MembersSnapshot()
        {
            lock (Gate)
            {
                return Members.ToList();
            }
        }
    }
}
