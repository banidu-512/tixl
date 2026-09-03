#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.laser.input;

[Guid("B2C3D4E5-F6A7-8901-ABCD-EF1234567890")]
internal sealed class PONKInput : Instance<PONKInput>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    #region PONK Protocol Constants (must match PONKOutput and the C++ reference implementation)
    // Header layout (52 bytes, little-endian):
    //  0-7:  "PONK-UDP" (8 ASCII bytes)
    //  8:    protocolVersion (1 byte, currently 0)
    //  9-12: senderIdentifier (4 bytes, LE int32)
    // 13-44: senderName (32 bytes, null-padded ASCII)
    // 45:   frameNumber (1 byte, mod-256)
    // 46:   chunkCount (1 byte)
    // 47:   chunkNumber (1 byte)
    // 48-51: dataCrc (4 bytes, LE uint32 - sum of all frame data bytes)
    // The crc field occupies offsets 48-51, so the payload starts at 52 - the header is NOT 48 bytes.
    private const string HeaderString = "PONK-UDP";
    private const byte ProtocolVersion = 0;
    private const int HeaderSize = 52;

    // Data formats (PONK_DATA_FORMAT_*)
    private const byte DataFormatXyRgbU16 = 0;          // 2x u16 XY + 3x u16 RGB = 10 bytes/point
    private const byte DataFormatXyF32RgbU8 = 1;        // 2x f32 XY + 3x u8 RGB = 11 bytes/point (mandatory receivers)

    // Default transport
    private static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.10.24");
    private const int DefaultPort = 5583;

    // PONK stores XY in [-1, 1]; LaserPoint X/Y are signed 16-bit ILDA-style (range ±32767).
    private const int LaserPointCoordMax = 32767;
    #endregion

    #region Inputs
    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567801")]
    public readonly InputSlot<bool> Active = new(true);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567802")]
    public readonly InputSlot<string> LocalIpAddress = new();

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567803")]
    public readonly InputSlot<int> Port = new(DefaultPort);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567804")]
    public readonly InputSlot<bool> PrintToLog = new();

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567805")]
    public readonly InputSlot<float> Timeout = new(1.2f);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567806")]
    public readonly InputSlot<int> ExpectedSenderId = new(0);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567807")]
    public readonly InputSlot<float> MinX = new(-1.0f);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567808")]
    public readonly InputSlot<float> MaxX = new(1.0f);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF1234567809")]
    public readonly InputSlot<float> MinY = new(-1.0f);

    [Input(Guid = "F1E2D3C4-B5A6-7890-ABCD-EF123456780A")]
    public readonly InputSlot<float> MaxY = new(1.0f);
    #endregion

    #region Outputs
    [Output(Guid = "E1D2C3B4-A5F6-7890-ABCD-EF1234567890", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<StructuredList> LaserPoints = new();

    [Output(Guid = "F2E3D4C5-B6A7-8901-ABCD-EF1234567891", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> StatusMessage = new();

    [Output(Guid = "A3B4C5D6-C7D8-9012-ABCD-EF1234567892", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> FrameCount = new();

    [Output(Guid = "B4C5D6E7-D8E9-0123-ABCD-EF1234567893", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> SenderName = new();

    [Output(Guid = "C5D6E7F8-E9F0-1234-ABCD-EF1234567894", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> SenderId = new();
    #endregion

    #region Private Fields
    // Listener state
    private Thread? _listenerThread;
    private UdpClient? _udpClient;
    private volatile bool _runListener;
    private bool _printToLog;
    private string _lastLocalIp = string.Empty;
    private int _lastPort = DefaultPort;
    private bool _wasActive;
    private double _lastRetryTime;
    private volatile bool _disposed;

    // Frame reconstruction - PONK frames are split into chunks; we reassemble per frame number.
    private readonly ConcurrentDictionary<byte, FrameBuffer> _frameBuffers = new();
    private readonly object _frameResetLock = new();
    private byte _lastCompletedFrame;
    private int _lastCompletedSenderId;
    private string _lastCompletedSenderName = string.Empty;
    private long _lastFrameTicks;

    // Stats
    private long _totalFramesReceived;
    private long _totalPointsReceived;
    private long _packetsReceived;
    private long _invalidPackets;
    private long _chunksOrphaned;

    // Converted point buffer (re-used to avoid per-frame allocations on the listener thread)
    private readonly List<LaserPoint> _pointAccumulator = new(2048);

    // Status
    private string _statusMessage = "Inactive";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;

    // Output snapshot
    private StructuredList? _currentPoints;
    #endregion

    #region Frame Buffer
    private sealed class FrameBuffer
    {
        public byte FrameNumber;
        public byte ChunkCount;
        public uint DataCrc;
        public int SenderId;
        public DateTime FirstChunkTime;
        public readonly Dictionary<byte, byte[]> Chunks = new(8);
    }
    #endregion

    #region Network Interface Info
    private static List<NetworkAdapterInfo> _networkInterfaces = GetNetworkInterfaces();

    private sealed record NetworkAdapterInfo(IPAddress IpAddress, string Name)
    {
        public string DisplayName => $"{Name}: {IpAddress}";
    }

    private static List<NetworkAdapterInfo> GetNetworkInterfaces()
    {
        var list = new List<NetworkAdapterInfo> { new(IPAddress.Loopback, "Localhost (127.0.0.1)") };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(new NetworkAdapterInfo(ip.Address, ni.Name));
                    }
                }
            }
        }
        catch { /* ignore */ }
        return list;
    }
    #endregion

    #region Constructor & Dispose
    public PONKInput()
    {
        LaserPoints.UpdateAction += Update;
        StatusMessage.UpdateAction += Update;
        FrameCount.UpdateAction += Update;
        SenderName.UpdateAction += Update;
        SenderId.UpdateAction += Update;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListening();
    }
    #endregion

    #region IStatusProvider
    public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
    public string GetStatusMessage() => _statusMessage;
    #endregion

    #region ICustomDropdownHolder
    string ICustomDropdownHolder.GetValueForInput(Guid id) =>
        id == LocalIpAddress.Id ? LocalIpAddress.Value ?? string.Empty : string.Empty;

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid id)
    {
        if (id == LocalIpAddress.Id)
        {
            _networkInterfaces = GetNetworkInterfaces();
            foreach (var adapter in _networkInterfaces)
                yield return adapter.DisplayName;
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid id, string? value, bool isListItem)
    {
        if (string.IsNullOrEmpty(value) || !isListItem || id != LocalIpAddress.Id) return;
        var parts = value.Split(": ");
        if (parts.Length > 1) LocalIpAddress.SetTypedInputValue(parts[1]);
    }
    #endregion

    #region Update
    private void Update(EvaluationContext context)
    {
        _printToLog = PrintToLog.GetValue(context);
        var active = Active.GetValue(context);
        var localIp = LocalIpAddress.GetValue(context) ?? string.Empty;
        var port = Port.GetValue(context);

        var settingsChanged = active != _wasActive
                              || !string.Equals(localIp, _lastLocalIp, StringComparison.Ordinal)
                              || port != _lastPort;
        if (settingsChanged)
        {
            StopListening();
            if (active) StartListening();
            _wasActive = active;
            _lastLocalIp = localIp;
            _lastPort = port;
        }
        else if (active && (_listenerThread == null || !_listenerThread.IsAlive))
        {
            // Auto-recover if the thread died (e.g. socket error)
            if (context.LocalTime - _lastRetryTime > 2.0)
            {
                _lastRetryTime = context.LocalTime;
                StartListening();
            }
        }

        CleanupStaleFrames(Timeout.GetValue(context));

        // Surface the most recent frame on the output slot
        LaserPoints.Value = _currentPoints ?? s_emptyList;
        FrameCount.Value = (int)Math.Min(_totalFramesReceived, int.MaxValue);
        SenderId.Value = _lastCompletedSenderId;
        SenderName.Value = _lastCompletedSenderName;
        StatusMessage.Value = _statusMessage;
    }

    private static readonly StructuredList s_emptyList = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());
    #endregion

    #region Listener Lifecycle
    private void StartListening()
    {
        if (_listenerThread is { IsAlive: true } || _disposed) return;
        _runListener = true;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "PONKInputListener" };
        _listenerThread.Start();
        if (_printToLog) Log.Debug("PONK Input: Starting listener thread.", this);
    }

    private void StopListening()
    {
        if (!_runListener) return;
        _runListener = false;
        if (_printToLog) Log.Debug("PONK Input: Stopping listener.", this);
        try { _udpClient?.Close(); } catch { /* ignore */ }
        try { _listenerThread?.Join(200); } catch { /* ignore */ }
        _listenerThread = null;
    }
    #endregion

    #region Listen Loop
    private void ListenLoop()
    {
        UdpClient? currentUdpClient = null;
        try
        {
            var localIpStr = LocalIpAddress.Value;
            var listenIp = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(localIpStr) && IPAddress.TryParse(localIpStr, out var parsed))
                listenIp = parsed;

            currentUdpClient = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            currentUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            currentUdpClient.Client.Bind(new IPEndPoint(listenIp, Port.Value));

            // Join the PONK default multicast group on the chosen interface. Unicast packets
            // sent to the same port land in the same socket, so we get both for free.
            try
            {
                var mreq = new MulticastOption(DefaultMulticastAddress, listenIp);
                currentUdpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mreq);
            }
            catch (Exception ex)
            {
                if (_printToLog) Log.Warning($"PONK Input: AddMembership failed ({ex.Message}); continuing unicast-only", this);
            }

            _udpClient = currentUdpClient;
            SetStatus($"Listening on {listenIp}:{Port.Value} (multicast {DefaultMulticastAddress}:{DefaultPort})",
                      IStatusProvider.StatusLevel.Notice);
            if (_printToLog)
                Log.Debug($"PONK Input: Bound to {listenIp}:{Port.Value}, joined {DefaultMulticastAddress}", this);

            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            while (_runListener && !_disposed)
            {
                byte[] data;
                try
                {
                    if (currentUdpClient == null) break;
                    data = currentUdpClient.Receive(ref remoteEndPoint);
                }
                catch (SocketException ex)
                {
                    if (_runListener && !_disposed)
                        Log.Warning($"PONK Input socket error: {ex.Message}", this);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (data.Length == 0) continue;
                _packetsReceived++;
                ProcessPacket(data);
            }
        }
        catch (Exception e)
        {
            SetStatus($"PONK Input bind error: {e.Message}", IStatusProvider.StatusLevel.Error);
            if (_printToLog) Log.Error($"PONK Input: {e.Message}", this);
        }
        finally
        {
            try { currentUdpClient?.Close(); } catch { /* ignore */ }
            if (currentUdpClient == _udpClient) _udpClient = null;
        }
    }
    #endregion

    #region Packet Processing
    private void ProcessPacket(byte[] data)
    {
        try
        {
            if (data.Length < HeaderSize) { _invalidPackets++; return; }

            // Magic
            for (var i = 0; i < 8; i++)
            {
                if (data[i] != (byte)HeaderString[i]) { _invalidPackets++; return; }
            }

            // Version
            if (data[8] > ProtocolVersion) { _invalidPackets++; return; }

            var senderId = data[9] | (data[10] << 8) | (data[11] << 16) | (data[12] << 24);
            var senderName = ReadSenderName(data, 13, 32);

            var frameNumber = data[45];
            var chunkCount = data[46];
            var chunkNumber = data[47];
            var dataCrc = (uint)(data[48] | (data[49] << 8) | (data[50] << 16) | (data[51] << 24));

            if (chunkCount == 0 || chunkNumber >= chunkCount) { _invalidPackets++; return; }

            var expectedSender = ExpectedSenderId.Value;
            if (expectedSender != 0 && senderId != expectedSender) return;

            // Get or create the per-frame buffer
            var frame = _frameBuffers.GetOrAdd(frameNumber, _ => new FrameBuffer
            {
                FrameNumber = frameNumber,
                FirstChunkTime = DateTime.UtcNow,
            });

            // First chunk for this frame number - lock to make sure the chunk count/CRC are consistent
            lock (frame)
            {
                if (frame.Chunks.Count == 0)
                {
                    frame.ChunkCount = chunkCount;
                    frame.DataCrc = dataCrc;
                    frame.SenderId = senderId;
                }
                else if (frame.ChunkCount != chunkCount || frame.DataCrc != dataCrc || frame.SenderId != senderId)
                {
                    // A later chunk disagrees with the first - drop the frame and start over
                    frame.Chunks.Clear();
                    frame.ChunkCount = chunkCount;
                    frame.DataCrc = dataCrc;
                    frame.SenderId = senderId;
                    _chunksOrphaned++;
                }

                // Copy payload (data.Length - HeaderSize bytes). Zero-length chunks are
                // stored too: canonical senders emit an empty trailing chunk when the
                // frame data is an exact multiple of the chunk quantum, and the chunk
                // count must be reachable for the frame to complete.
                var payloadLength = data.Length - HeaderSize;
                var payload = new byte[payloadLength];
                if (payloadLength > 0)
                    Array.Copy(data, HeaderSize, payload, 0, payloadLength);
                frame.Chunks[chunkNumber] = payload;
            }

            if (frame.Chunks.Count < frame.ChunkCount) return;

            // All chunks received - reassemble and verify CRC
            byte[] frameData;
            lock (frame)
            {
                frameData = CombineChunks(frame);
            }

            var actualCrc = ComputeChecksum(frameData);
            if (actualCrc != dataCrc)
            {
                _invalidPackets++;
                if (_printToLog)
                    Log.Warning($"PONK Input: Frame {frameNumber} CRC mismatch (expected {dataCrc:X8}, got {actualCrc:X8})", this);
                _frameBuffers.TryRemove(frameNumber, out _);
                return;
            }

            // Parse the frame and publish points
            ParseFrameAndPublish(frameData, senderId, senderName);
            _frameBuffers.TryRemove(frameNumber, out _);
            _totalFramesReceived++;
            _lastCompletedFrame = frameNumber;
            _lastCompletedSenderId = senderId;
            _lastCompletedSenderName = senderName;
            _lastFrameTicks = Stopwatch.GetTimestamp();
            FrameCount.DirtyFlag.Invalidate();
            SenderId.DirtyFlag.Invalidate();
            SenderName.DirtyFlag.Invalidate();
            LaserPoints.DirtyFlag.Invalidate();

            if (_printToLog)
                Log.Debug($"PONK Input: Frame {frameNumber} from '{senderName}' (id {senderId}) - {_totalPointsReceived} points total", this);
        }
        catch (Exception ex)
        {
            _invalidPackets++;
            if (_printToLog) Log.Warning($"PONK Input: Packet parse error - {ex.Message}", this);
        }
    }

    private static byte[] CombineChunks(FrameBuffer frame)
    {
        // Per spec: chunks should be received in order but the receiver accepts any order.
        var totalSize = 0;
        for (var i = 0; i < frame.ChunkCount; i++)
        {
            if (frame.Chunks.TryGetValue((byte)i, out var c)) totalSize += c.Length;
        }
        var combined = new byte[totalSize];
        var offset = 0;
        for (var i = 0; i < frame.ChunkCount; i++)
        {
            if (!frame.Chunks.TryGetValue((byte)i, out var c)) continue;
            Array.Copy(c, 0, combined, offset, c.Length);
            offset += c.Length;
        }
        return combined;
    }

    private static string ReadSenderName(byte[] data, int offset, int length)
    {
        // 32-byte null-padded ASCII
        var end = offset;
        while (end < offset + length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    /// <summary>
    /// "CRC" in the PONK spec is the sum of all frame-data bytes, mod 2^32. Not a polynomial CRC.
    /// </summary>
    private static uint ComputeChecksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i++) sum += data[i];
        return sum;
    }
    #endregion

    #region Frame Parsing
    private void ParseFrameAndPublish(byte[] data, int senderId, string senderName)
    {
        // Pull range inputs once per frame
        var minX = MinX.Value;
        var maxX = MaxX.Value;
        var minY = MinY.Value;
        var maxY = MaxY.Value;

        _pointAccumulator.Clear();
        var offset = 0;
        var pathCount = 0;

        while (offset < data.Length)
        {
            // Data format
            if (offset + 1 > data.Length) break;
            var dataFormat = data[offset++];

            // Meta data count
            if (offset + 1 > data.Length) break;
            var metaCount = data[offset++];
            if (offset + metaCount * 12 > data.Length) break;
            offset += metaCount * 12; // skip metadata - TiXL doesn't apply render hints server-side

            // Point count
            if (offset + 2 > data.Length) break;
            var pointCount = data[offset] | (data[offset + 1] << 8);
            offset += 2;

            int bytesPerPoint;
            switch (dataFormat)
            {
                case DataFormatXyF32RgbU8: bytesPerPoint = 11; break; // 2*4 + 3*1
                case DataFormatXyRgbU16: bytesPerPoint = 10; break;   // 5*2
                default:
                    if (_printToLog)
                        Log.Warning($"PONK Input: Unsupported data format {dataFormat}, aborting frame", this);
                    return;
            }

            if (offset + (long)pointCount * bytesPerPoint > data.Length) break;

            for (var i = 0; i < pointCount; i++)
            {
                float x, y;
                int r, g, b;

                if (dataFormat == DataFormatXyF32RgbU8)
                {
                    x = BitConverter.ToSingle(data, offset);
                    y = BitConverter.ToSingle(data, offset + 4);
                    r = data[offset + 8];
                    g = data[offset + 9];
                    b = data[offset + 10];
                }
                else // DataFormatXyRgbU16
                {
                    var xu = ReadU16(data, offset);
                    var yu = ReadU16(data, offset + 2);
                    var ru = ReadU16(data, offset + 4);
                    var gu = ReadU16(data, offset + 6);
                    var bu = ReadU16(data, offset + 8);

                    // u16 -> [-1, 1] (PONK convention) -> f32
                    x = (xu / 65535f) * 2f - 1f;
                    y = (yu / 65535f) * 2f - 1f;
                    // u16 0..65535 -> u8
                    r = ru >> 8;
                    g = gu >> 8;
                    b = bu >> 8;
                }

                offset += bytesPerPoint;

                // Remap PONK's [-1, 1] XY to the user-configured output range, then scale to ILDA 16-bit
                var lx = RemapAndScale(x, minX, maxX);
                var ly = RemapAndScale(y, minY, maxY);
                _pointAccumulator.Add(new LaserPoint(lx, ly, Scale8To16(r), Scale8To16(g), Scale8To16(b)));
            }

            pathCount++;
        }

        _totalPointsReceived += _pointAccumulator.Count;

        if (_pointAccumulator.Count == 0) return;

        // Publish: copy out of the accumulator (the listener thread keeps reusing it)
        var snapshot = new LaserPoint[_pointAccumulator.Count];
        _pointAccumulator.CopyTo(snapshot);
        _currentPoints = new StructuredList<LaserPoint>(snapshot);
    }

    private static int RemapAndScale(float value, float min, float max)
    {
        // Map the PONK normal range onto the user-specified range, then scale to ILDA 16-bit.
        if (Math.Abs(max - min) < 1e-6f) return 0;
        var t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        return (int)(t * 2f * LaserPointCoordMax) - LaserPointCoordMax;
    }

    private static int Scale8To16(int v8) => v8 << 8; // 0..255 -> 0..65280
    private static int ReadU16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);
    #endregion

    #region Cleanup & Status
    private void CleanupStaleFrames(float timeoutSeconds)
    {
        if (timeoutSeconds <= 0) return;
        var cutoff = DateTime.UtcNow.AddSeconds(-timeoutSeconds);
        var stale = new List<byte>();
        foreach (var kvp in _frameBuffers)
        {
            if (kvp.Value.FirstChunkTime < cutoff) stale.Add(kvp.Key);
        }
        foreach (var key in stale)
        {
            if (_frameBuffers.TryRemove(key, out var frame))
            {
                _chunksOrphaned += frame.Chunks.Count;
            }
        }
    }

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
    }
    #endregion
}
