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
// Since protocol 2 the relay also:
// - keeps a directory of rooms whose host asked to be listed ({"role":"list"} returns it),
// - verifies a Discord access token a peer sends along and tells the room who that peer is
//   (name, language, Discord id), so participants cannot pick someone else's name,
// - lets a participant who knows the room key re-open the room of a host that went silent
//   (host handover), and closes the members of a room whose host left so they notice at once.
// The token is used for one request to Discord's /users/@me and is not stored or logged.
//
// Usage: dotnet run --project CollabRelay [-- <port>] [--require-discord]   (default port 44710)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace T3.CollabRelay;

internal static class Program
{
    private const int DefaultPort = 44710;
    private const int MaxMembersPerRoom = 16;
    private const int MaxFrameSize = 4 * 1024 * 1024;
    private const int HandshakeTimeoutMs = 10_000;
    private const int HandshakeLineLimit = 4096;

    /// <summary>A host that sent nothing for this long may be replaced by a peer holding the room key.
    /// A host with members sends heartbeats twice a second, an idle one a keep-alive every 15 s —
    /// so this only ever evicts a host that is gone.</summary>
    private const int OwnerStaleMs = 20_000;

    /// <summary>Error text the editor matches on: the room it tried to take over still has its host.</summary>
    private const string RoomHasLiveHostError = "room has a live host";

    private const int MaxListedRooms = 200;
    private const int DiscordCacheMinutes = 10;

    /// <summary>With --require-discord, peers without a valid Discord token are turned away.</summary>
    private static bool _requireDiscord;

    private static readonly HttpClient DiscordHttp = CreateDiscordClient();

    /// <summary>Verified identities by token hash, so a reconnecting peer does not cost a Discord request each time.</summary>
    private static readonly ConcurrentDictionary<string, (Identity Identity, long ExpiresTicks)> DiscordCache = new();
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

        _requireDiscord = args.Contains("--require-discord");
        if (_requireDiscord)
            Console.WriteLine("Discord login is required to host or join on this relay.");

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

            role = request.Role;
            var roomCode = request.Room;

            if (role == "list")
            {
                await WriteLineAsync(client, BuildRoomList());
                client.Close();
                return;
            }

            var identity = role is "host" or "join" ? await ResolveIdentityAsync(request) : Identity.Unknown;
            if (_requireDiscord && !identity.Verified && role is "host" or "join")
            {
                await WriteLineAsync(client, "{\"ok\":false,\"error\":\"this relay requires a Discord login\"}");
                client.Close();
                return;
            }

            if (role == "host")
            {
                // The reply must reach the host before any member announcement: the socket's write
                // gate is held from claiming the room until the reply is written.
                var gate = WriteGateOf(client);
                await gate.WaitAsync();
                try
                {
                    room = CreateRoom(request, client, identity, out var refusal);
                    if (room == null)
                    {
                        await WriteRawLineAsync(client, $"{{\"ok\":false,\"error\":\"{refusal}\"}}");
                        client.Close();
                        return;
                    }

                    var hostRoom = room;
                    await WriteRawLineAsync(client, request.Extended
                                                        ? Json(w =>
                                                               {
                                                                   w.WriteBoolean("ok", true);
                                                                   w.WriteString("room", hostRoom.Code);
                                                                   WriteIdentity(w, "you", identity);
                                                               })
                                                        : $"{{\"ok\":true,\"room\":\"{room.Code}\"}}");
                }
                finally
                {
                    gate.Release();
                }

                Console.WriteLine($"room {room.Code} registered{(room.Public ? " (listed)" : "")}");
            }
            else if (role == "join")
            {
                // Likewise a member is routable as soon as it is in the room; hold its write gate
                // so no host frame overtakes the join reply.
                var gate = WriteGateOf(client);
                await gate.WaitAsync();
                try
                {
                    var joined = TryJoinRoom(roomCode, client, identity, out room, out slot);
                    if (!joined)
                    {
                        var reason = room == null ? "room not found" : "room full";
                        room = null;
                        await WriteRawLineAsync(client, $"{{\"ok\":false,\"error\":\"{reason}\"}}");
                        client.Close();
                        return;
                    }

                    var joinedRoom = room!;
                    var joinedSlot = slot;
                    await WriteRawLineAsync(client, request.Extended
                                                        ? Json(w =>
                                                               {
                                                                   w.WriteBoolean("ok", true);
                                                                   w.WriteString("room", joinedRoom.Code);
                                                                   w.WriteNumber("slot", joinedSlot);
                                                                   WriteIdentity(w, "you", identity);
                                                                   WriteIdentity(w, "host", joinedRoom.Host);
                                                               })
                                                        : $"{{\"ok\":true,\"room\":\"{room!.Code}\",\"slot\":{slot}}}");
                }
                finally
                {
                    gate.Release();
                }

                // Tell the host about the new member before any of its frames arrive. A host that
                // registered the old way gets the short line it can parse.
                var announcedRoom = room!;
                var owner = announcedRoom.Owner;
                if (owner != null)
                {
                    var memberSlot = slot;
                    await WriteLineAsync(owner, announcedRoom.Extended
                                                    ? Json(w =>
                                                           {
                                                               w.WriteString("t", "member");
                                                               w.WriteNumber("slot", memberSlot);
                                                               w.WriteString("name", identity.Name);
                                                               w.WriteString("lang", identity.Lang);
                                                               w.WriteBoolean("verified", identity.Verified);
                                                               w.WriteString("did", identity.DiscordId);
                                                               w.WriteString("avatar", identity.Avatar);
                                                           })
                                                    : $"{{\"t\":\"member\",\"slot\":{slot}}}");
                }

                Console.WriteLine($"member {slot} joined {announcedRoom.Code} ({announcedRoom.MemberCount}/{MaxMembersPerRoom})");
            }
            else
            {
                await WriteLineAsync(client, "{\"ok\":false,\"error\":\"unknown role\"}");
                client.Close();
                return;
            }

            TouchActivity();
            await RouteFramesAsync(client, room!, slot, role == "host");
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

    /// <summary>
    /// Registers a room for a host. A requested code that is free is granted. A code that is taken
    /// yields a fresh one — unless the request carries that room's key: then this is the session
    /// itself coming back (its host after a network drop, or a participant taking over), and the
    /// room is handed over when its host has gone silent, or refused while the host is alive.
    /// </summary>
    private static Room? CreateRoom(Request request, TcpClient owner, Identity identity, out string refusal)
    {
        refusal = "";
        var code = SanitizeCode(request.Room);
        while (true)
        {
            var room = Rooms.GetOrAdd(code, _ => new Room(code));
            TcpClient? evictedOwner = null;
            List<TcpClient>? evictedMembers = null;
            var claimed = false;

            // Claimed under the room's gate: two hosts asking for the same code must not both own it,
            // and a room whose host just left (and was removed) must not be revived.
            lock (room.Gate)
            {
                var isCurrent = Rooms.TryGetValue(code, out var current) && current == room;
                if (isCurrent && room.Owner == null)
                {
                    claimed = true;
                }
                else if (isCurrent && room.Owner != null && request.Key.Length > 0
                         && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(room.Key), Encoding.UTF8.GetBytes(request.Key)))
                {
                    // The same editor instance asking again is the host itself, back after its link
                    // dropped: whatever still holds the room is its own dead connection.
                    var sameHost = request.Instance.Length > 0 && room.OwnerInstance == request.Instance;
                    var currentOwner = room.Owner;
                    var silentForMs = Environment.TickCount64 - Interlocked.Read(ref room.LastOwnerActivityTicks);
                    if (!sameHost && currentOwner.Connected && silentForMs < OwnerStaleMs)
                    {
                        refusal = RoomHasLiveHostError;
                        return null;
                    }

                    // The old host and whoever is still attached to it are cut loose; they find
                    // the room again under the same code.
                    evictedOwner = currentOwner;
                    evictedMembers = room.Members.Values.ToList();
                    room.Members.Clear();
                    room.MemberIdentities.Clear();
                    claimed = true;
                }

                if (claimed)
                {
                    room.Owner = owner;
                    room.Key = request.Key;
                    room.OwnerInstance = request.Instance;
                    room.Extended = request.Extended;
                    room.Host = identity;
                    room.Title = request.Title;
                    room.Purpose = request.Purpose;
                    room.Project = request.Project;
                    room.Public = request.Public;
                    room.PasswordProtected = request.PasswordProtected;
                    Interlocked.Exchange(ref room.LastOwnerActivityTicks, Environment.TickCount64);
                }
            }

            if (claimed)
            {
                if (evictedOwner != null)
                {
                    Console.WriteLine($"room {room.Code}: silent host replaced");
                    evictedOwner.Close();
                    foreach (var member in evictedMembers!)
                        member.Close();
                }

                return room;
            }

            // Code collision with a live room — mint a fresh one for the new host.
            code = GenerateRoomCode();
        }
    }

    private static bool TryJoinRoom(string roomCode, TcpClient client, Identity identity, out Room? room, out byte slot)
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
            found.MemberIdentities[slot] = identity;
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

                    // Without a host nothing these connections send goes anywhere. Closing them
                    // tells the members now, instead of after their silence timeout, so they can
                    // start finding the session again.
                    foreach (var orphan in room.Members.Values)
                        orphan.Close();

                    room.Members.Clear();
                    room.MemberIdentities.Clear();
                }

                return false;
            }

            if (!room.Members.TryGetValue(slot, out var member) || member != client)
                return false;

            room.MemberIdentities.Remove(slot);
            return room.Members.Remove(slot);
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
                // A replaced host must not reach the members of its successor.
                if (room.Owner != client)
                    return;

                Interlocked.Exchange(ref room.LastOwnerActivityTicks, Environment.TickCount64);

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
        var line = new List<byte>(128);
        var buffer = new byte[1];
        while (line.Count < HandshakeLineLimit)
        {
            var read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
                throw new IOException("closed before handshake");

            if (buffer[0] == '\n')
                return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');

            line.Add(buffer[0]);
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

    /// <summary>What a peer says about itself and, for a host, about its room.</summary>
    private sealed record Request(string Role,
                                  string Room,
                                  bool Extended,
                                  string Token,
                                  string Name,
                                  string Lang,
                                  string Key,
                                  string Instance,
                                  string Title,
                                  string Purpose,
                                  string Project,
                                  bool Public,
                                  bool PasswordProtected);

    /// <summary>Who a peer is. Verified means Discord confirmed it for the token the peer sent;
    /// otherwise name and language are just what the peer claims.</summary>
    internal sealed record Identity(string Name, string Lang, bool Verified, string DiscordId, string Avatar)
    {
        public static readonly Identity Unknown = new("", "", false, "", "");
    }

    private static Request? ParseRequest(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string Text(string name, int maxLength)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                    return "";

                return Clean(value.GetString() ?? "", maxLength);
            }

            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

            var role = Text("role", 16);
            if (role.Length == 0)
                return null;

            // "v":2 marks an editor that reads the longer replies; older ones get the short lines.
            var extended = root.TryGetProperty("v", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() >= 2;
            return new Request(role,
                               Text("room", 32),
                               extended,
                               Text("token", 512),
                               Text("name", 48),
                               Text("lang", 12),
                               Text("key", 64),
                               Text("inst", 64),
                               Text("title", 64),
                               Text("purpose", 24),
                               Text("project", 64),
                               Flag("public"),
                               Flag("pwd"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Trims, cuts to length and drops control characters; these strings end up in other people's UI.</summary>
    private static string Clean(string value, int maxLength)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    // ---------------------------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------------------------

    private static HttpClient CreateDiscordClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("User-Agent", "TiXLCollabRelay");
        return client;
    }

    private static async Task<Identity> ResolveIdentityAsync(Request request)
    {
        if (request.Token.Length > 0)
        {
            var verified = await VerifyDiscordAsync(request.Token);
            if (verified != null)
                return verified;
        }

        return new Identity(request.Name, request.Lang, false, "", "");
    }

    /// <summary>Asks Discord whose token this is. Null when Discord does not accept it or cannot be reached.</summary>
    private static async Task<Identity?> VerifyDiscordAsync(string token)
    {
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = Environment.TickCount64;
        if (DiscordCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresTicks > now)
            return cached.Identity;

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await DiscordHttp.SendAsync(message);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var user = document.RootElement;

            string Field(string name) => user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                                             ? value.GetString() ?? ""
                                             : "";

            var id = Field("id");
            if (id.Length == 0)
                return null;

            var name = Field("global_name");
            if (name.Length == 0)
                name = Field("username");

            var identity = new Identity(Clean(name, 48), Clean(Field("locale"), 12), true, Clean(id, 32), Clean(Field("avatar"), 64));
            if (DiscordCache.Count > 5000)
                DiscordCache.Clear();

            DiscordCache[cacheKey] = (identity, now + DiscordCacheMinutes * 60_000L);
            return identity;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Discord check failed: {e.GetType().Name}");
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Replies. Built with a JSON writer that escapes everything outside ASCII, so a line is
    // plain ASCII whatever a name or title contains.
    // ---------------------------------------------------------------------------------------

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string property, Identity identity)
    {
        writer.WriteStartObject(property);
        writer.WriteString("name", identity.Name);
        writer.WriteString("lang", identity.Lang);
        writer.WriteBoolean("verified", identity.Verified);
        writer.WriteString("did", identity.DiscordId);
        writer.WriteString("avatar", identity.Avatar);
        writer.WriteEndObject();
    }

    /// <summary>The rooms whose hosts asked to be listed and are still answering.</summary>
    private static string BuildRoomList()
    {
        var now = Environment.TickCount64;
        return Json(writer =>
                    {
                        writer.WriteBoolean("ok", true);
                        writer.WriteStartArray("rooms");
                        var listed = 0;
                        foreach (var room in Rooms.Values)
                        {
                            if (listed >= MaxListedRooms)
                                break;

                            if (!room.Public || room.Owner is not { Connected: true }
                                || now - Interlocked.Read(ref room.LastOwnerActivityTicks) > 60_000)
                                continue;

                            listed++;
                            writer.WriteStartObject();
                            writer.WriteString("code", room.Code);
                            writer.WriteString("title", room.Title);
                            writer.WriteString("purpose", room.Purpose);
                            writer.WriteString("project", room.Project);
                            writer.WriteString("host", room.Host.Name);
                            writer.WriteString("lang", room.Host.Lang);
                            writer.WriteBoolean("verified", room.Host.Verified);
                            writer.WriteNumber("members", room.MemberCount + 1);
                            writer.WriteNumber("max", MaxMembersPerRoom + 1);
                            writer.WriteBoolean("pwd", room.PasswordProtected);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    });
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
        public readonly Dictionary<byte, Identity> MemberIdentities = [];
        public TcpClient? Owner;

        /// <summary>Secret the host chose; whoever presents it may re-open the room once the host is silent.</summary>
        public string Key = "";

        /// <summary>Which editor instance hosts the room; lets the host reclaim it from its own dead connection.</summary>
        public string OwnerInstance = "";

        /// <summary>The host's editor reads the longer control lines.</summary>
        public bool Extended;

        public Identity Host = Identity.Unknown;
        public string Title = "";
        public string Purpose = "";
        public string Project = "";
        public bool Public;
        public bool PasswordProtected;

        /// <summary>TickCount64 of the last frame from the host.</summary>
        public long LastOwnerActivityTicks = Environment.TickCount64;
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
