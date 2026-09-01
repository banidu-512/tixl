#nullable enable
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Lib.laser;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Utils;

namespace Lib.laser.output;

[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
internal sealed class PONKOutput : Instance<PONKOutput>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    #region PONK Protocol Constants (from Common/Cpp/PonkDefs.h in the MadMapper/Ponk repo)
    // Header
    private const string HeaderString = "PONK-UDP";
    private const byte ProtocolVersion = 0;
    // 8 magic + 1 version + 4 sender id + 32 name + 1 frame + 1 chunk count + 1 chunk number + 4 crc.
    // The crc field lives at offsets 48-51, so the payload must start at 52 - the header is NOT 48 bytes.
    private const int HeaderSize = 52;

    // Data formats
    private const byte DataFormatXyRgbU16 = 0;
    private const byte DataFormatXyF32RgbU8 = 1; // Mandatory minimum receivers must support

    // Metadata key length + value length
    private const int MetaEntrySize = 12; // 8-char key + float32

    // Transmission
    private const int MaxChunkPayload = 1472 - HeaderSize; // 1420 bytes of frame data per packet
    private const int MaxDatagramSize = 1472; // PONK_MAX_CHUNK_SIZE
    private const int MaxPointsPerFrame = 32000;

    // Default transport
    private static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.10.24");
    private const int DefaultPort = 5583;
    private const int MaxSenderNameBytes = 32; // Null-terminated, so 31 usable chars
    #endregion

    #region Inputs
    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567801")]
    public readonly InputSlot<bool> Enable = new();

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567802")]
    public readonly InputSlot<bool> SimulationMode = new(true);

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567803")]
    public readonly InputSlot<bool> UseMulticast = new(true);

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567804")]
    public readonly InputSlot<string> LocalIpAddress = new();

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567805")]
    public readonly InputSlot<string> TargetIpAddress = new("239.255.10.24");

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567806")]
    public readonly InputSlot<int> Port = new(DefaultPort);

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567807")]
    public readonly InputSlot<string> SenderName = new("TiXL");

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567808")]
    public readonly InputSlot<bool> LoopLastFrame = new(true);

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF1234567809")]
    public readonly InputSlot<bool> PrintToLog = new();

    [Input(Guid = "B1C2D3E4-F5A6-7890-ABCD-EF123456780A")]
    public readonly InputSlot<float> MaxScanSpeed = new(1.0f);

    [Input(Guid = "B1C2D3E4-F5A6-ABCD-EF123456780B")]
    public readonly InputSlot<int> PathNumber = new(1);

    [Input(Guid = "B1C2D3E4-F5A6-7890-EF12-3456780C1234")]
    public readonly InputSlot<StructuredList> LaserPoints = new();
    #endregion

    #region Outputs
    [Output(Guid = "C1D2E3F4-A5B6-7890-ABCD-EF1234567890")]
    public readonly Slot<bool> IsConnected = new();

    [Output(Guid = "C1D2E3F4-A5B6-7890-ABCD-EF1234567891", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PacketsSent = new();

    [Output(Guid = "C1D2E3F4-A5B6-7890-ABCD-EF1234567892", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> StatusMessage = new();

    [Output(Guid = "C1D2E3F4-A5B6-7890-ABCD-EF1234567893", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<long> PointsSent = new();

    [Output(Guid = "C1D2E3F4-A5B6-7890-ABCD-EF1234567894")]
    public readonly Slot<Command> Command = new();
    #endregion

    #region Private Fields
    // Stable sender identifier per operator instance - PONK spec says the receiver keys on this int
    // and a project reload should reuse the same id so the receiver treats it as the same source.
    private readonly int _senderId = Random.Shared.Next(int.MinValue, int.MaxValue);

    private readonly ConcurrentQueue<LaserPoint[]> _pointQueue = new();
    private CancellationTokenSource? _connectionCts;
    private LaserPoint[]? _lastFrame;
    private byte _frameNumber;

    private Socket? _socket;
    private IPEndPoint? _sendEndPoint;
    private IPAddress? _bindAddress;
    private int _bindPort;

    // Tunables
    private const int SendRetryDelayMs = 50;
    private const int MaxConsecutiveSendErrors = 20;
    private const int MaxQueuedFrames = 5;
    private const int ConnectRetryDelayMs = 2000;

    // State
    private volatile bool _disposed;
    private volatile bool _isSending;
    private long _totalPointsSent;
    private long _totalPacketsSent;
    private int _consecutiveErrors;

    // Update / change detection
    private bool _lastEnable;
    private bool _lastSimulationMode = true;
    private bool _lastUseMulticast = true;
    private string _lastLocalIp = string.Empty;
    private string _lastTargetIp = string.Empty;
    private int _lastPort = DefaultPort;
    private bool _lastLoopLastFrame = true;

    // Status
    private string _statusMessage = "Disconnected";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;

    // For simulation bookkeeping
    private int _lastPointCount;

    private bool _printToLog;
    // Network interface list for the local IP dropdown
    private static List<NetworkAdapterInfo> _networkInterfaces = GetNetworkInterfaces();
    #endregion

    #region Network Adapter Info
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
    public PONKOutput()
    {
        Command.UpdateAction += Update;
        Command.Value = new Command();
        IsConnected.UpdateAction += Update;
        PacketsSent.UpdateAction += Update;
        StatusMessage.UpdateAction += Update;
        PointsSent.UpdateAction += Update;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TearDownConnection();
    }
    #endregion

    #region IStatusProvider
    public IStatusProvider.StatusLevel GetStatusLevel() => _statusLevel;
    public string GetStatusMessage() => _statusMessage;
    #endregion

    #region ICustomDropdownHolder
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id) return LocalIpAddress.Value ?? string.Empty;
        if (inputId == TargetIpAddress.Id) return TargetIpAddress.Value ?? string.Empty;
        return string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id)
        {
            _networkInterfaces = GetNetworkInterfaces();
            foreach (var adapter in _networkInterfaces)
                yield return adapter.DisplayName;
        }
        else if (inputId == TargetIpAddress.Id)
        {
            // Offer the PONK default multicast group as a convenience option
            yield return $"{DefaultMulticastAddress} (PONK default multicast)";
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected) || !isAListItem) return;
        if (inputId == LocalIpAddress.Id)
        {
            var parts = selected.Split(": ");
            if (parts.Length > 1)
                LocalIpAddress.SetTypedInputValue(parts[1]);
        }
        else if (inputId == TargetIpAddress.Id)
        {
            var start = selected.IndexOf('(');
            if (start > 0)
            {
                var trimmed = selected[..start].Trim();
                if (IPAddress.TryParse(trimmed, out _))
                    TargetIpAddress.SetTypedInputValue(trimmed);
            }
        }
    }
    #endregion

    #region Update
    private void Update(EvaluationContext context)
    {
        var enable = Enable.GetValue(context);
        var simulation = SimulationMode.GetValue(context);
        var useMulticast = UseMulticast.GetValue(context);
        var localIpString = LocalIpAddress.GetValue(context) ?? string.Empty;
        var targetIpString = TargetIpAddress.GetValue(context) ?? string.Empty;
        var port = Port.GetValue(context);
        var loopLastFrame = LoopLastFrame.GetValue(context);
        var printToLog = PrintToLog.GetValue(context);

        _lastLoopLastFrame = loopLastFrame;
        _printToLog = printToLog;

        // --- Enqueue points from the upstream graph ---
        if (enable && !simulation && _isSending &&
            LaserPoints.GetValue(context) is StructuredList<LaserPoint> laserPoints &&
            laserPoints.TypedElements != null && laserPoints.NumElements > 0)
        {
            EnqueuePoints(laserPoints.TypedElements, laserPoints.NumElements);
        }
        else if (enable && simulation && _isSending &&
                 LaserPoints.GetValue(context) is StructuredList<LaserPoint> simPoints &&
                 simPoints.TypedElements != null && simPoints.NumElements > 0)
        {
            SimulateSendPoints(simPoints.TypedElements, simPoints.NumElements);
        }

        // --- Connection lifecycle ---
        var shouldConnect = enable && !simulation;
        var targetChanged = useMulticast != _lastUseMulticast
                            || !string.Equals(targetIpString, _lastTargetIp, StringComparison.Ordinal)
                            || port != _lastPort
                            || !string.Equals(localIpString, _lastLocalIp, StringComparison.Ordinal)
                            || enable != _lastEnable
                            || simulation != _lastSimulationMode;

        if (shouldConnect != _lastEnable || targetChanged)
        {
            _lastEnable = enable;
            _lastSimulationMode = simulation;
            _lastUseMulticast = useMulticast;
            _lastTargetIp = targetIpString;
            _lastPort = port;
            _lastLocalIp = localIpString;

            if (shouldConnect)
                HandleConnectionStart(useMulticast, localIpString, targetIpString, port);
            else
                HandleConnectionStop();
        }

        // --- Status / output slots ---
        UpdateStatus();
        if (simulation && _lastEnable)
        {
            // Synthetic "connected" so a simulation-mode graph reports green
            IsConnected.Value = true;
        }
        else
        {
            IsConnected.Value = _isSending;
        }
        PointsSent.Value = _totalPointsSent;
        PointsSent.DirtyFlag.Invalidate();
        PacketsSent.Value = (int)Math.Min(_totalPacketsSent, int.MaxValue);
        Command.DirtyFlag.Clear();
    }
    #endregion

    #region Connection Lifecycle
    private void HandleConnectionStart(bool useMulticast, string localIpString, string targetIpString, int port)
    {
        TearDownConnection();

        if (port <= 0 || port > 65535)
        {
            SetStatus($"Invalid port {port}", IStatusProvider.StatusLevel.Error);
            return;
        }

        if (!IPAddress.TryParse(targetIpString, out var targetIp))
        {
            SetStatus($"Invalid target IP '{targetIpString}'", IStatusProvider.StatusLevel.Error);
            return;
        }

        // Local interface is optional - defaulting to Any lets the OS pick. We honour the
        // selection when supplied so a multi-NIC host can constrain which network is used.
        IPAddress? localIp = null;
        if (!string.IsNullOrWhiteSpace(localIpString) && IPAddress.TryParse(localIpString, out var parsed))
            localIp = parsed;

        var cts = new CancellationTokenSource();
        _connectionCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await ConnectionLoopAsync(useMulticast, localIp, targetIp, port, token);
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private void HandleConnectionStop()
    {
        TearDownConnection();
        SetStatus(_lastEnable ? "Simulation mode - not sending to device" : "Disconnected",
                  IStatusProvider.StatusLevel.Notice);
        IsConnected.DirtyFlag.Invalidate();
    }

    private void TearDownConnection()
    {
        var cts = Interlocked.Exchange(ref _connectionCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket != null)
        {
            // Closing the socket off-thread protects against SO_LINGER / multicast leave hangs
            _ = Task.Run(() =>
            {
                try { socket.Close(); } catch { }
            });
        }

        _sendEndPoint = null;
        _bindAddress = null;
        _bindPort = 0;
        _isSending = false;
        _pointQueue.Clear();
        _lastFrame = null;
    }

    /// <summary>
    /// Owns socket bring-up. UDP socket setup is cheap and non-blocking, but creating a
    /// socket on a chosen interface requires a bind - if the bind fails (port in use, bad
    /// interface) we retry with backoff. Once the socket is up, we run the send loop until
    /// cancelled.
    /// </summary>
    private async Task ConnectionLoopAsync(bool useMulticast, IPAddress? localIp, IPAddress targetIp,
                                           int port, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested && !_disposed)
        {
            attempt++;
            Socket? socket = null;
            try
            {
                SetStatus(attempt == 1
                              ? $"Opening UDP socket to {targetIp}:{port}..."
                              : $"Reopening UDP socket to {targetIp}:{port} (attempt {attempt})...",
                          IStatusProvider.StatusLevel.Notice);
                if (_printToLog)
                    Log.Debug($"PONK: Opening UDP socket to {targetIp}:{port} (attempt {attempt})", this);

                socket = CreateSendSocket(localIp, useMulticast, targetIp, out var bindAddr, out var bindPort);
                if (token.IsCancellationRequested)
                {
                    CloseQuietly(socket);
                    return;
                }

                _socket = socket;
                _bindAddress = bindAddr;
                _bindPort = bindPort;
                _sendEndPoint = new IPEndPoint(targetIp, port);
                _isSending = true;
                _consecutiveErrors = 0;
                SetStatus($"Sending to {targetIp}:{port} (sender id {_senderId})", IStatusProvider.StatusLevel.Success);
                IsConnected.DirtyFlag.Invalidate();
                if (_printToLog)
                    Log.Debug($"PONK: Socket bound to {bindAddr}:{bindPort}, sending to {targetIp}:{port}", this);

                // Runs until cancellation. SendLoop owns its own retry/back-off and returns
                // only when the socket is unrecoverable; the outer loop then rebuilds it.
                await SendLoopAsync(socket, _sendEndPoint, token);
                return;
            }
            catch (OperationCanceledException)
            {
                CloseQuietly(socket);
                return;
            }
            catch (Exception e)
            {
                CloseQuietly(socket);
                _isSending = false;
                IsConnected.DirtyFlag.Invalidate();

                if (token.IsCancellationRequested) return;

                SetStatus($"Socket error: {e.Message} - retrying...", IStatusProvider.StatusLevel.Warning);
                if (attempt == 1)
                    Log.Warning($"PONK: Socket setup failed - {e.Message}", this);
                else if (_printToLog)
                    Log.Debug($"PONK: Retry {attempt} failed - {e.Message}", this);
            }

            try { await Task.Delay(ConnectRetryDelayMs, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static Socket CreateSendSocket(IPAddress? localIp, bool useMulticast, IPAddress targetIp,
                                           out IPAddress boundInterface, out int boundPort)
    {
        // PONK_PORT (5583) is the documented default; using a per-sender ephemeral port keeps
        // multiple TiXL instances (or the operator running alongside MadMapper) from colliding.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
            EnableBroadcast = true,
        };

        // Allow address reuse so receivers running on the same machine aren't blocked
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Bump send buffer - laser frames can be large bursts
        try { socket.SendBufferSize = 256 * 1024; } catch { }

        // Bind to the chosen local interface (or Any) on an ephemeral port. Bind is required
        // so multicast packets leave on the right interface.
        var bindAddr = localIp ?? IPAddress.Any;
        var bindPort = 0; // OS picks an ephemeral port
        var bindEp = new IPEndPoint(bindAddr, bindPort);
        socket.Bind(bindEp);

        if (useMulticast)
        {
            // Set the multicast interface so packets go out on the right NIC
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                                       bindAddr.GetAddressBytes());
            }
            catch { /* IPAny may reject; multicast group routing is OS-defined */ }

            // Default multicast TTL is 1; bump so it crosses subnets like MadMapper does
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
            }
            catch { }

            // Some NICs complain if we don't join the group we send to, so opt-in (best effort)
            try
            {
                var mreq = new MulticastOption(targetIp, bindAddr);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mreq);
            }
            catch { /* not all platforms support AddMembership on a send socket */ }
        }
        else
        {
            // Unicast: when sending across a non-default route, allow the OS to pick a sensible
            // source address if the user picked Any.
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
            }
            catch { }
        }

        var localEp = (IPEndPoint)socket.LocalEndPoint!;
        boundInterface = localEp.Address;
        boundPort = localEp.Port;
        return socket;
    }

    private static void CloseQuietly(Socket? s)
    {
        if (s == null) return;
        try { s.Close(); } catch { }
    }
    #endregion

    #region Send Loop
    private async Task SendLoopAsync(Socket socket, IPEndPoint target, CancellationToken token)
    {
        var senderName = (SenderName.Value ?? "TiXL");
        var maxScanSpeed = MaxScanSpeed.Value;
        var pathNumber = PathNumber.Value;

        var headerBuffer = new byte[HeaderSize];
        var payload = new MemoryStream(capacity: MaxDatagramSize);
        var writer = new BinaryWriter(payload);

        while (!token.IsCancellationRequested && !_disposed)
        {
            // Re-read inputs each frame so editor tweaks take effect without reconnecting
            senderName = (SenderName.Value ?? "TiXL");
            maxScanSpeed = Math.Max(0.01f, MaxScanSpeed.Value);
            pathNumber = PathNumber.Value;

            LaserPoint[]? points = null;
            if (_pointQueue.TryDequeue(out var dequeued) && dequeued.Length > 0)
            {
                points = dequeued;
            }
            else if (_lastLoopLastFrame && _lastFrame is { Length: > 0 })
            {
                // Keep the projector showing the last frame while no new data arrives
                _pointQueue.Enqueue(_lastFrame);
            }

            if (points == null)
            {
                try { await Task.Delay(1, token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Build the frame's full data section (all paths + meta) in `payload`
            payload.SetLength(0);
            WritePathData(writer, points, maxScanSpeed, pathNumber);

            var frameData = payload.ToArray();
            var crc = ComputeChecksum(frameData);
            var chunkCount = ComputeChunkCount(frameData.Length);

            try
            {
                SendFrame(socket, target, headerBuffer, frameData, crc, chunkCount, senderName, token);
                _totalPointsSent += points.Length;
                _consecutiveErrors = 0;
                RememberFrame(points);
                PointsSent.DirtyFlag.Invalidate();
                PacketsSent.DirtyFlag.Invalidate();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (token.IsCancellationRequested) return;
                _consecutiveErrors++;
                if (_printToLog)
                    Log.Debug($"PONK: Send error ({_consecutiveErrors}/{MaxConsecutiveSendErrors}): {e.Message}", this);
                if (_consecutiveErrors >= MaxConsecutiveSendErrors)
                {
                    // Surface the failure to the outer loop so it can rebuild the socket
                    SetStatus($"Send error: {e.Message} - reconnecting...", IStatusProvider.StatusLevel.Warning);
                    throw;
                }
                SetStatus($"Send error: {e.Message}", IStatusProvider.StatusLevel.Warning);
                try { await Task.Delay(SendRetryDelayMs, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void SendFrame(Socket socket, IPEndPoint target, byte[] headerBuffer, byte[] frameData, uint crc,
                           int chunkCount, string senderName, CancellationToken token)
    {
        // PONK chunks are header-prefixed; each chunk's payload is a slice of the frame data.
        // All chunks of a frame carry the same CRC so receivers can drop incomplete frames.
        var written = 0;
        for (int chunkNumber = 0; chunkNumber < chunkCount; chunkNumber++)
        {
            var payloadSize = Math.Min(MaxChunkPayload, frameData.Length - written);
            var totalSize = HeaderSize + payloadSize;
            if (headerBuffer.Length < totalSize) Array.Resize(ref headerBuffer, totalSize);

            WriteHeader(headerBuffer, senderName, _frameNumber, (byte)chunkCount, (byte)chunkNumber, crc);
            Array.Copy(frameData, written, headerBuffer, HeaderSize, payloadSize);

var sent = 0;
                while (sent < totalSize)
                {
                    try
                    {
                        sent += socket.SendTo(headerBuffer, sent, totalSize - sent, SocketFlags.None, target);
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.WouldBlock ||
                                                   se.SocketErrorCode == SocketError.TryAgain ||
                                                   se.SocketErrorCode == SocketError.IOPending ||
                                                   se.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        // Brief yield to avoid a hot spin; the next SendTo will resume
                        Thread.Sleep(1);
                    }
                }

            written += payloadSize;
            _totalPacketsSent++;
        }

        // Frame number wraps mod 256
        unchecked { _frameNumber++; }
    }

    private void WriteHeader(byte[] buffer, string senderName, byte frameNumber, byte chunkCount, byte chunkNumber, uint crc)
    {
        // All multi-byte fields are little-endian per the PONK reference implementation
        // Header layout (48 bytes packed):
        //  0-7:  "PONK-UDP" (8 bytes ASCII)
        //  8:    protocolVersion (1 byte = 0)
        //  9-12: senderIdentifier (4 bytes, LE int32)
        // 13-44: senderName (32 bytes, null-padded ASCII)
        // 45:   frameNumber (1 byte,uchar)
        // 46:   chunkCount (1 byte)
        // 47:   chunkNumber (1 byte)
        // 48-51: dataCrc (4 bytes, LE uint32)

        // Write headerString
        var nameBytes = Encoding.ASCII.GetBytes(HeaderString);
        for (var i = 0; i < 8; i++) buffer[i] = i < nameBytes.Length ? nameBytes[i] : (byte)' ';

        // protocolVersion
        buffer[8] = ProtocolVersion;

        // senderIdentifier - little-endian int32
        buffer[9] = (byte)(_senderId & 0xFF);
        buffer[10] = (byte)((_senderId >> 8) & 0xFF);
        buffer[11] = (byte)((_senderId >> 16) & 0xFF);
        buffer[12] = (byte)((_senderId >> 24) & 0xFF);

        // senderName - 32 bytes, null-padded ASCII
        var senderBytes = Encoding.ASCII.GetBytes(senderName);
        for (var i = 0; i < MaxSenderNameBytes; i++)
        {
            if (i < senderBytes.Length)
                buffer[13 + i] = senderBytes[i];
            else
                buffer[13 + i] = 0; // null terminate / pad
        }

        // frameNumber / chunkCount / chunkNumber
        buffer[45] = frameNumber;
        buffer[46] = chunkCount;
        buffer[47] = chunkNumber;

        // dataCrc - little-endian uint32
        buffer[48] = (byte)(crc & 0xFF);
        buffer[49] = (byte)((crc >> 8) & 0xFF);
        buffer[50] = (byte)((crc >> 16) & 0xFF);
        buffer[51] = (byte)((crc >> 24) & 0xFF);
    }

    private void WritePathData(BinaryWriter writer, LaserPoint[] points, float maxScanSpeed, int pathNumber)
    {
        // One path per frame. LaserPoint.X/Y are floats in TiXL's renderer; PONK's mandatory
        // format expects X/Y as float32 in [-1, 1] and R/G/B as uint8.
        writer.Write(DataFormatXyF32RgbU8);

        // Metadata: PATHNUMB identifies the shape across frames so receivers can dispatch;
        // MAXSPEED tells the receiver the requested scan-speed multiplier.
        writer.Write((byte)2);
        WriteMetaEntry(writer, "PATHNUMB", pathNumber);
        WriteMetaEntry(writer, "MAXSPEED", maxScanSpeed);

        // pointCount is a ushort and chunkCount a byte, so cap the frame at a point count
        // whose chunking stays inside both limits (32000 points -> ~247 chunks at 1420 B/chunk).
        var count = Math.Min(points.Length, MaxPointsPerFrame);
        writer.Write((ushort)count);

        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            // Clamp to the [-1, 1] receiver range. ConvertLaserPoint's U16 range is broader
            // than PONK's normalised range so we don't reuse it directly.
            var x = Math.Clamp(p.X, -1f, 1f);
            var y = Math.Clamp(p.Y, -1f, 1f);
            var r = (byte)Math.Clamp(p.R >> 8, 0, 255);
            var g = (byte)Math.Clamp(p.G >> 8, 0, 255);
            var b = (byte)Math.Clamp(p.B >> 8, 0, 255);

            writer.Write(x);
            writer.Write(y);
            writer.Write(r);
            writer.Write(g);
            writer.Write(b);
        }
    }

    private static void WriteMetaEntry(BinaryWriter writer, string key, float value)
    {
        // 8-char key, null-padded; 4-byte float
        var keyBytes = Encoding.ASCII.GetBytes(key);
        var keyBuf = new byte[8];
        var copy = Math.Min(keyBytes.Length, 7); // keep at least one NUL terminator
        Array.Copy(keyBytes, keyBuf, copy);
        writer.Write(keyBuf);
        writer.Write(value);
    }

    private static int ComputeChunkCount(int frameDataLength)
    {
        if (frameDataLength <= 0) return 0;
        // ceil(frameData / MaxChunkPayload) - matches the C++ sample sender
        return (frameDataLength + MaxChunkPayload - 1) / MaxChunkPayload;
    }

    /// <summary>
    /// PONK's "CRC" is a sum of all frame-data bytes (mod 2^32). It's not a polynomial CRC;
    /// the field name is kept for protocol compatibility. Same value in every chunk of a frame.
    /// </summary>
    private static uint ComputeChecksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i++) sum += data[i];
        return sum;
    }

    private void RememberFrame(LaserPoint[] points)
    {
        if (!_lastLoopLastFrame) return;
        if (_lastFrame == null || _lastFrame.Length != points.Length)
            _lastFrame = new LaserPoint[points.Length];
        Array.Copy(points, _lastFrame, points.Length);
    }
    #endregion

    #region Queue / Simulation
    private void EnqueuePoints(LaserPoint[] points, int count)
    {
        if (count <= 0) return;

        // Drop the oldest frame if the producer outruns the consumer; UDP is best-effort
        // so this is preferable to back-pressure stalling the render thread.
        while (_pointQueue.Count >= MaxQueuedFrames)
        {
            if (!_pointQueue.TryDequeue(out _)) break;
        }

        // Copy out of the upstream buffer: the path renderer reuses the same allocation
        // for every frame and we'd race the next Update otherwise.
        var copy = new LaserPoint[count];
        Array.Copy(points, copy, count);
        _pointQueue.Enqueue(copy);
    }

    private void SimulateSendPoints(LaserPoint[] points, int count)
    {
        _totalPointsSent += count;
        _lastPointCount = count;
    }
    #endregion

    #region Status
    private void UpdateStatus()
    {
        if (!_lastEnable)
            StatusMessage.Value = "Output disabled. Enable 'Enable'.";
        else if (_lastSimulationMode)
            StatusMessage.Value = $"SIMULATION MODE - Points sent: {_totalPointsSent}";
        else if (_isSending)
            StatusMessage.Value = $"Streaming via PONK to {_lastTargetIp}:{_lastPort} - Packets: {_totalPacketsSent}";
        else
            StatusMessage.Value = string.IsNullOrEmpty(_lastTargetIp)
                                      ? "No target IP configured"
                                      : $"Connecting to {_lastTargetIp}:{_lastPort}...";
    }

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
    }
    #endregion
}
