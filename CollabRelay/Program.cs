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
using System.Text;

namespace T3.CollabRelay;

internal static class Program
{
    private const int DefaultPort = 44710;
    private const int MaxMembersPerRoom = 16;
    private const int MaxFrameSize = 4 * 1024 * 1024;
    private const byte BroadcastSlot = 0xFF;

    private static readonly ConcurrentDictionary<string, Room> Rooms = new(StringComparer.OrdinalIgnoreCase);

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
            var line = await ReadLineAsync(client.GetStream(), CancellationToken.None);
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
                room = CreateRoom(roomCode);
                await WriteLineAsync(client, $"{{\"ok\":true,\"room\":\"{room.Code}\"}}");
                Console.WriteLine($"room {room.Code} registered");
                room.Owner = client;
            }
            else if (role == "join")
            {
                var joined = TryJoinRoom(roomCode, client, out room, out slot);
                if (!joined)
                {
                    var reason = room == null ? "room not found" : "room full";
                    room = null;
                    await WriteLineAsync(client, $"{{\"ok\":false,\"error\":\"{reason}\"}}");
                    client.Close();
                    return;
                }

                await WriteLineAsync(client, $"{{\"ok\":true,\"room\":\"{room!.Code}\",\"slot\":{slot}}}");

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
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // Normal disconnect path.
        }
        catch (Exception e)
        {
            Console.WriteLine($"connection error: {e.Message}");
        }
        finally
        {
            if (room != null)
                RemoveClient(room, client, role == "host", slot);

            client.Close();
        }
    }

    private static Room CreateRoom(string requestedCode)
    {
        var code = SanitizeCode(requestedCode);
        while (true)
        {
            var room = Rooms.GetOrAdd(code, _ => new Room(code));
            if (room.Owner == null)
                return room;

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

    private static void RemoveClient(Room room, TcpClient client, bool isOwner, byte slot)
    {
        lock (room.Gate)
        {
            if (isOwner && room.Owner == client)
            {
                room.Owner = null;
                Console.WriteLine($"room {room.Code} closed (host left)");
                Rooms.TryRemove(room.Code, out _);
            }
            else
            {
                room.Members.Remove(slot);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Frame routing
    // ---------------------------------------------------------------------------------------

    private static async Task RouteFramesAsync(TcpClient client, Room room, byte slot, bool isHost)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        while (true)
        {
            // On the host connection, a frame may be interleaved with control lines; a length
            // prefix can never start with '{', so read the discriminator byte first.
            if (isHost)
            {
                var first = stream.ReadByte();
                if (first < 0)
                    return;

                if (first == '{')
                {
                    // Control lines are only ever sent TO the host; the host never sends them.
                    // Drain and ignore (join announcements for already-known slots).
                    continue;
                }

                lengthBuffer[0] = (byte)first;
                if (!await TryReadExactAsync(stream, lengthBuffer.AsMemory(1)))
                    return;
            }
            else if (!await TryReadExactAsync(stream, lengthBuffer))
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
            if (!await TryReadExactAsync(stream, frame))
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
                        if (member.Value.Connected)
                            await WriteFrameAsync(member.Value, payloadLength, payload);
                    }
                }
                else if (room.TryGetMember(targetSlot, out var target) && target.Connected)
                {
                    await WriteFrameAsync(target, payloadLength, payload);
                }
            }
            else
            {
                // Member -> host: retag with the slot so the host can demultiplex.
                var owner = room.Owner;
                if (owner is { Connected: true })
                    await WriteTaggedFrameAsync(owner, slot, length, frame);
            }
        }
    }

    private static async Task WriteFrameAsync(TcpClient target, int length, byte[] frameWithType)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        var stream = target.GetStream();
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(frameWithType.AsMemory(0, length));
    }

    private static async Task WriteTaggedFrameAsync(TcpClient owner, byte slot, int length, byte[] frameWithType)
    {
        // [length + 1][slot][original frame bytes]
        var prefix = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length + 1);
        prefix[4] = slot;
        var stream = owner.GetStream();
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(frameWithType);
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
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
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await client.GetStream().WriteAsync(bytes);
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

        public byte AllocateSlot()
        {
            var slot = _nextSlot;
            _nextSlot = (byte)((_nextSlot + 1) % 0xFF);
            if (_nextSlot == 0)
                _nextSlot = 1;

            return slot;
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
