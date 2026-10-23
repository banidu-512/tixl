#nullable enable
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace T3.Editor.Collab;

/// <summary>
/// Wire protocol for TiXL collaboration sessions.
///
/// Transport framing:
/// - Reliable channel (TCP or relay): [int32 length][byte type][payload], length includes the type
///   byte, all multi-byte values little-endian. Frames larger than <see cref="MaxFrameSize"/> are a
///   protocol violation and drop the connection (guards against memory-exhaustion from bad peers).
/// - Unreliable channel (UDP, direct LAN/online connections only): raw presence datagrams without a
///   length prefix, same payload layout as the reliable presence frame.
///
/// Payload types:
/// - <see cref="FrameType.Json">: UTF-8 JSON document, first property "t" discriminates the message.
/// - <see cref="FrameType.Presence">: fixed-layout binary cursor telemetry (see <see cref="PresencePacket"/>).
/// - <see cref="FrameType.Heartbeat">: 8-byte Environment.TickCount64 echo.
///
/// All graph mutations travel as JSON "op" messages and are applied on the UI thread only; presence
/// is display-only and never touches the graph.
/// </summary>
internal static class CollabProtocol
{
    public const int ProtocolVersion = 1;
    public const int DefaultTcpPort = 44700;
    public const int DefaultRelayPort = 44710;

    /// <summary>Hard upper bound for one reliable frame including the type byte.</summary>
    public const int MaxFrameSize = 4 * 1024 * 1024;

    /// <summary>Hard upper bound for one JSON payload. Ops are tiny; anything bigger is hostile or buggy.</summary>
    public const int MaxJsonPayload = 1024 * 1024;

    /// <summary>Expected size of one presence datagram with a full selection list (connId, flags, guid, 2 floats, count, 15 guids).</summary>
    public const int MaxPresencePayload = 2 + 16 + 8 + 1 + 15 * 16;

    public const int MaxPeers = 16;
    public const int MaxSelectionIdsPerPresence = 15;
    public const int HeartbeatIntervalMs = 1000;
    public const int PeerTimeoutMs = 10_000;
    public const int PresenceSendIntervalMs = 50;

    public enum FrameType : byte
    {
        Json = 1,
        Presence = 2,
        Heartbeat = 3,
    }

    /// <summary>
    /// Reads the length prefix and returns it, or false when the stream ended. The caller then reads
    /// <paramref name="length"/> payload bytes.
    /// </summary>
    public static bool TryReadFrameLength(Stream stream, out int length)
    {
        length = 0;
        Span<byte> buffer = stackalloc byte[4];
        if (!TryReadExact(stream, buffer))
            return false;

        length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }

    public static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0)
                return false;

            offset += read;
        }

        return true;
    }

    public static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellation)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellation);
            if (read <= 0)
                return false;

            offset += read;
        }

        return true;
    }

    /// <summary>Writes one framed message to a reliable stream. Single writer enforced by caller locking.</summary>
    public static void WriteFrame(Stream stream, FrameType type, ReadOnlySpan<byte> payload)
    {
        var total = payload.Length + 1;
        if (total > MaxFrameSize)
            throw new IOException($"Collab frame of {total} bytes exceeds the {MaxFrameSize} byte limit");

        Span<byte> prefix = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, total);
        prefix[4] = (byte)type;
        stream.Write(prefix);
        stream.Write(payload);
        stream.Flush();
    }

    public static byte[] EncodeJson(object message)
    {
        var json = JsonConvert.SerializeObject(message);
        return Encoding.UTF8.GetBytes(json);
    }

    public static bool TryDecodeJson(ReadOnlySpan<byte> payload, out JObject json)
    {
        json = null!;
        if (payload.Length > MaxJsonPayload)
            return false;

        try
        {
            json = JObject.Parse(Encoding.UTF8.GetString(payload));
            return json.TryGetValue("t", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string GetString(JObject json, string property, string fallback = "")
    {
        return json.TryGetValue(property, out var value) ? value.Value<string>() ?? fallback : fallback;
    }

    public static bool TryGetGuid(JObject json, string property, out Guid guid)
    {
        guid = Guid.Empty;
        return json.TryGetValue(property, out var value)
               && Guid.TryParse(value.Value<string>(), out guid);
    }

    public static bool TryGetInt(JObject json, string property, out int value)
    {
        value = 0;
        return json.TryGetValue(property, out var token)
               && int.TryParse(token.Value<string>(), out value);
    }
}

/// <summary>
/// Immutable snapshot of one peer's editor presence. Written by the UI thread, shipped over the wire,
/// decoded on the receiving network thread and consumed on the receiving UI thread. Passed by
/// reference so a swap replaces the whole packet without locking.
/// </summary>
internal sealed class PresencePacket
{
    public byte PeerId;
    public byte Flags;
    public Guid CompositionId;
    public Vector2 CursorOnCanvas;
    public Guid[] SelectedChildIds = [];

    public const byte FlagHoveringCanvas = 0x01;

    public bool IsHoveringCanvas => (Flags & FlagHoveringCanvas) != 0;

    public int Encode(Span<byte> target)
    {
        var selectionCount = Math.Min(SelectedChildIds.Length, CollabProtocol.MaxSelectionIdsPerPresence);
        var size = 2 + 16 + 8 + 1 + selectionCount * 16;
        if (target.Length < size)
            return 0;

        target[0] = PeerId;
        target[1] = Flags;
        CompositionId.TryWriteBytes(target.Slice(2, 16));
        BinaryPrimitives.WriteSingleLittleEndian(target.Slice(18, 4), CursorOnCanvas.X);
        BinaryPrimitives.WriteSingleLittleEndian(target.Slice(22, 4), CursorOnCanvas.Y);
        target[26] = (byte)selectionCount;
        for (var i = 0; i < selectionCount; i++)
        {
            SelectedChildIds[i].TryWriteBytes(target.Slice(27 + i * 16, 16));
        }

        return size;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out PresencePacket packet)
    {
        packet = null;
        if (payload.Length < 27)
            return false;

        var selectionCount = payload[26];
        if (selectionCount > CollabProtocol.MaxSelectionIdsPerPresence)
            return false;

        if (payload.Length < 27 + selectionCount * 16)
            return false;

        packet = new PresencePacket
                     {
                         PeerId = payload[0],
                         Flags = payload[1],
                         CompositionId = new Guid(payload.Slice(2, 16)),
                         CursorOnCanvas = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(18, 4)),
                                                      BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(22, 4))),
                         SelectedChildIds = new Guid[selectionCount],
                     };

        for (var i = 0; i < selectionCount; i++)
        {
            packet.SelectedChildIds[i] = new Guid(payload.Slice(27 + i * 16, 16));
        }

        return true;
    }
}
