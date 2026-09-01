using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Lib.laser;
using T3.Core.DataTypes;
using T3.Core.Utils;
using LaserCore.EtherDream.Net.Device;
using LaserCore.EtherDream.Net.Discovery;
using LaserCore.EtherDream.Net.Dto;
using LaserCore.EtherDream.Net.Enums;

namespace Lib.laser.output;

[Guid("21F7DE8E-2FB3-4222-B951-01E7AABCA6B8")]
internal sealed class EtherDreamOutput : Instance<EtherDreamOutput>, IStatusProvider, ICustomDropdownHolder, IDisposable
{
    [Output(Guid = "A5C6BAF1-46AE-4FF1-A7C6-CDF8E29ABC8B")]
    public readonly Slot<bool> IsConnected = new();

    [Output(Guid = "7C93E6E4-3908-4972-98F9-B193A0016C94")]
    public readonly Slot<int> BufferFullness = new();

    [Output(Guid = "4B7EADB3-125B-45E5-95DA-15CD65A269BF", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> StatusMessage = new();

    [Output(Guid = "9C188446-3A09-4D88-A124-A00DD50EFAEB", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<long> PointsSent = new();

    [Output(Guid = "D1E2F3A4-B5C6-4789-9012-345A6789CDEF")]
    public readonly Slot<Command> Command = new();

    // Library instances
    private DeviceDiscovery _deviceDiscovery;
    private Dac _dac;

    // Fields for queueing points before sending to the library
    private readonly ConcurrentQueue<LaserPoint[]> _pointQueue = new();
    private CancellationTokenSource _connectionCancellationTokenSource;
    private LaserPoint[] _lastFrame;

    // Timing / retry tuning
    private const int ConnectRetryDelayMs = 2000;
    private const int SendRetryDelayMs = 250;
    private const int WarmupRetryDelayMs = 500;
    private const int MaxWarmupRetries = 120;      // ~60s for the light engine to become ready
    private const int MaxConsecutiveSendErrors = 5;
    private const int DiscoveryPollIntervalMs = 100;
    private const int MaxQueuedFrames = 10;

    // Network interface selection for UI dropdown
    private static List<NetworkAdapterInfo> _networkInterfaces = GetNetworkInterfaces();
    private double _lastNetworkRefreshTime;

    public EtherDreamOutput()
    {
        Command.UpdateAction += Update;
        Command.Value = new Command();
        IsConnected.UpdateAction += Update;
        BufferFullness.UpdateAction += Update;
        StatusMessage.UpdateAction += Update;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TearDownConnection();

        // Null the field first so the discovery loop exits before the client is disposed
        var discovery = _deviceDiscovery;
        _deviceDiscovery = null;
        discovery?.Dispose();
        _pointQueue.Clear();
    }

    private void Update(EvaluationContext context)
    {
        var enable = Enable.GetValue(context);
        var ipAddressString = IpAddress.GetValue(context);
        var scanRate = ScanRate.GetValue(context);
        var simulationMode = SimulationMode.GetValue(context);
        var discoverDevices = DiscoverDevices.GetValue(context);
        var localIpString = LocalIpAddress.GetValue(context);
        var printToLog = PrintToLog.GetValue(context);
        var loopLastFrame = LoopLastFrame.GetValue(context);
        var clearEStop = ClearEStop.GetValue(context);
        var points = LaserPoints.GetValue(context);

        _scanRate = Math.Clamp(scanRate, 100, 100000);
        _lastEnable = enable;
        _simulationMode = simulationMode;
        _printToLog = printToLog;
        _loopLastFrame = loopLastFrame;

        // Rising edge on ClearEStop requests an emergency-stop clear on the send loop thread
        if (clearEStop && !_lastClearEStop)
        {
            _clearEStopRequested = true;
        }

        _lastClearEStop = clearEStop;

        // Handle network interface selection for discovery (still using our custom logic for dropdown)
        if (_selectedSubnetMask == null && !string.IsNullOrEmpty(localIpString))
        {
            var adapter = _networkInterfaces.FirstOrDefault(ni => ni.IpAddress.ToString() == localIpString);
            if (adapter == null && context.LocalTime - _lastNetworkRefreshTime > 2.0)
            {
                _lastNetworkRefreshTime = context.LocalTime;
                _networkInterfaces = GetNetworkInterfaces();
                adapter = _networkInterfaces.FirstOrDefault(ni => ni.IpAddress.ToString() == localIpString);
            }
            if (adapter != null)
            {
                _selectedSubnetMask = adapter.SubnetMask;
            }
        }

        // --- Discovery Handling ---
        if (discoverDevices && _deviceDiscovery == null && !_discoveryUnavailable && !_disposed)
        {
            try
            {
                _deviceDiscovery = new DeviceDiscovery();
                _ = Task.Run(RunDiscoveryLoop);
                if (_printToLog) Log.Debug("EtherDream: Starting discovery...", this);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Another instance in this process already owns the discovery port; its
                // results are still visible through the shared static device list.
                Log.Warning("EtherDream: Discovery port 7654 already in use. Using existing discovery.", this);
                _discoveryUnavailable = true;
            }
        }
        else if (!discoverDevices && (_deviceDiscovery != null || _discoveryUnavailable))
        {
            _discoveryUnavailable = false;
            var discovery = _deviceDiscovery;
            _deviceDiscovery = null;
            discovery?.Dispose();
            var deviceCount = DeviceDiscovery.DiscoveredDevices.Count;
            if (_printToLog) Log.Debug($"EtherDream: Stopping discovery. Found {deviceCount} device(s)", this);
        }

        // --- Point Sending ---
        if (enable && points is StructuredList<LaserPoint> laserPoints && laserPoints.TypedElements != null && laserPoints.NumElements > 0)
        {
            if (simulationMode)
            {
                SimulateSendPoints(laserPoints.TypedElements, laserPoints.NumElements);
            }
            else
            {
                EnqueuePoints(laserPoints.TypedElements, laserPoints.NumElements);
            }
        }

        // --- Connection Management ---
        var shouldConnect = enable && !simulationMode && !string.IsNullOrEmpty(ipAddressString);
        if (shouldConnect != _lastConnectState || ipAddressString != _lastIpAddress || simulationMode != _lastSimulationForConnection)
        {
            HandleConnectionChange(shouldConnect, simulationMode, ipAddressString);
        }

        if (simulationMode)
        {
            _isConnected = true;
            SimulateBufferStatus();
        }

        UpdateStatus();
        UpdateBufferStatus();

        IsConnected.Value = _isConnected;
        Command.DirtyFlag.Clear();
    }

    private async Task RunDiscoveryLoop()
    {
        var announcedDevices = new HashSet<string>();

        while (!_disposed)
        {
            var discovery = _deviceDiscovery;
            if (discovery == null)
                return;

            try
            {
                foreach (var device in discovery.GetAvailableDevices())
                {
                    // Only announce devices once to avoid spamming the log on every poll
                    if (announcedDevices.Add(device.Ip))
                    {
                        Log.Info($"EtherDream: Discovered device at {device.Ip} ({DeviceDiscovery.GetDeviceName(device)})", this);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // Transient socket errors while polling for broadcasts - keep going
            }

            await Task.Delay(DiscoveryPollIntervalMs);
        }
    }

    private void HandleConnectionChange(bool shouldConnect, bool simulationMode, string ipAddressString)
    {
        _lastConnectState = shouldConnect;
        _lastSimulationForConnection = simulationMode;
        _lastIpAddress = ipAddressString;

        TearDownConnection();

        if (shouldConnect && !string.IsNullOrWhiteSpace(ipAddressString))
        {
            var cts = new CancellationTokenSource();
            _connectionCancellationTokenSource = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ConnectionLoopAsync(ipAddressString, token);
                }
                finally
                {
                    // The loop owns the CTS; by the time we get here cancellation was
                    // already processed, so disposing cannot race with Task.Delay registrations
                    cts.Dispose();
                }
            });
        }
        else
        {
            SetStatus(simulationMode && _lastEnable
                          ? "Simulation mode - not sending to device"
                          : "Disconnected", IStatusProvider.StatusLevel.Notice);
            IsConnected.DirtyFlag.Invalidate();
        }
    }

    private void TearDownConnection()
    {
        var cts = Interlocked.Exchange(ref _connectionCancellationTokenSource, null);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by the connection loop
            }
        }

        // Dispose the DAC off-thread: it sends a stop command with multi-second socket timeouts
        var dac = Interlocked.Exchange(ref _dac, null);
        if (dac != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    dac.Dispose();
                }
                catch
                {
                    // Socket may already be dead
                }
            });
        }

        _isConnected = false;
        _isPlaying = false;
        _pointQueue.Clear();
        _lastFrame = null;
    }

    /// <summary>
    /// Runs on a background thread because the Dac constructor performs a blocking TCP connect
    /// that can stall for seconds when the device is unreachable.
    /// Retries until cancelled, and recovers from streaming errors by reconnecting.
    /// </summary>
    private async Task ConnectionLoopAsync(string ipAddress, CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested && !_disposed)
        {
            attempt++;
            Dac dac = null;
            try
            {
                SetStatus(attempt == 1
                              ? $"Connecting to {ipAddress}..."
                              : $"Connecting to {ipAddress} (attempt {attempt})...", IStatusProvider.StatusLevel.Notice);
                if (_printToLog) Log.Debug($"EtherDream: Connecting to {ipAddress} (attempt {attempt})", this);

                // Dac constructor connects automatically (blocking)
                dac = new Dac(ipAddress);
                if (token.IsCancellationRequested)
                {
                    DisposeDacQuietly(dac);
                    return;
                }

                dac.StatusUpdated += HandleStatusUpdate;
                dac.DeviceConnected += () => HandleConnectionStateChanged(true);
                dac.DeviceDisconnected += () => HandleConnectionStateChanged(false);
                dac.SafetyFaultDetected += reason => Log.Warning($"EtherDream: Safety fault - {reason}", this);

                _dac = dac;
                _isConnected = true;
                SetStatus($"Connected to {ipAddress}", IStatusProvider.StatusLevel.Success);
                IsConnected.DirtyFlag.Invalidate();
                if (_printToLog) Log.Debug($"EtherDream: Connected to {ipAddress}", this);

                // Runs until cancelled or until a fatal error is thrown
                await SendLoopAsync(dac, token);
                return;
            }
            catch (OperationCanceledException)
            {
                DisposeDacQuietly(dac);
                return;
            }
            catch (Exception e)
            {
                DisposeDacQuietly(dac);
                _isConnected = false;
                IsConnected.DirtyFlag.Invalidate();

                // Errors raised while cancelling come from our own teardown (aborted socket);
                // don't overwrite the status or schedule a retry in that case
                if (token.IsCancellationRequested)
                    return;

                SetStatus($"Connection lost: {e.Message} - retrying...", IStatusProvider.StatusLevel.Warning);
                if (attempt == 1)
                    Log.Warning($"EtherDream: Connection to {ipAddress} failed - {e.Message}", this);
                else if (_printToLog)
                    Log.Debug($"EtherDream: Retry {attempt} failed - {e.Message}", this);
            }

            try
            {
                await Task.Delay(ConnectRetryDelayMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Streams queued points to the DAC. StreamPoints blocks while it paces points into the
    /// DAC buffer, so this loop naturally adapts to the device's point rate. Transient socket
    /// errors are retried because the library reconnects on its own heartbeat; the light
    /// engine needs some warm-up time before it accepts point data.
    /// </summary>
    private async Task SendLoopAsync(Dac dac, CancellationToken token)
    {
        var consecutiveErrors = 0;
        var warmupRetries = 0;

        while (!token.IsCancellationRequested && !_disposed)
        {
            if (_clearEStopRequested)
            {
                _clearEStopRequested = false;
                try
                {
                    dac.ClearEStop();
                    SetStatus("E-stop cleared", IStatusProvider.StatusLevel.Success);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    SetStatus("Clearing E-stop failed - will retry on next toggle", IStatusProvider.StatusLevel.Warning);
                }
            }

            if (_pointQueue.TryDequeue(out var points) && points.Length > 0)
            {
                try
                {
                    dac.StreamPoints(ConvertPoints(points), (uint)_scanRate);
                    _totalPointsSent += points.Length;
                    PointsSent.DirtyFlag.Invalidate();
                    consecutiveErrors = 0;
                    warmupRetries = 0;
                    RememberFrame(points);
                }
                catch (InvalidOperationException ex) when (!token.IsCancellationRequested)
                {
                    // Light engine not ready yet - warm-up needs waiting out, an active
                    // emergency stop needs the user to request a clear
                    if (++warmupRetries > MaxWarmupRetries)
                        throw;

                    if (ex.Message.Contains("EmergencyStop"))
                    {
                        SetStatus("E-stop active on device - set 'ClearEStop' to resume", IStatusProvider.StatusLevel.Warning);
                    }
                    else
                    {
                        SetStatus($"Waiting for light engine to become ready ({warmupRetries * WarmupRetryDelayMs / 1000}s)...", IStatusProvider.StatusLevel.Notice);
                    }

                    await Task.Delay(WarmupRetryDelayMs, token);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    consecutiveErrors++;
                    if (_printToLog) Log.Debug($"EtherDream: Send error ({consecutiveErrors}/{MaxConsecutiveSendErrors})", this);
                    if (consecutiveErrors >= MaxConsecutiveSendErrors)
                        throw; // Give up on this connection - outer loop reconnects

                    SetStatus($"Connection problems - retrying ({consecutiveErrors}/{MaxConsecutiveSendErrors})...", IStatusProvider.StatusLevel.Warning);
                    await Task.Delay(SendRetryDelayMs, token);
                }
            }
            else if (_loopLastFrame && _lastFrame is { Length: > 0 })
            {
                // Keep the projector showing the last frame while no new data arrives
                _pointQueue.Enqueue(_lastFrame);
            }
            else
            {
                try
                {
                    await Task.Delay(1, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void RememberFrame(LaserPoint[] points)
    {
        if (!_loopLastFrame)
            return;

        if (_lastFrame == null || _lastFrame.Length != points.Length)
            _lastFrame = new LaserPoint[points.Length];

        Array.Copy(points, _lastFrame, points.Length);
    }

    private static void DisposeDacQuietly(Dac dac)
    {
        if (dac == null)
            return;

        try
        {
            dac.Dispose();
        }
        catch
        {
            // Socket may already be dead
        }
    }

    private void HandleStatusUpdate(AckCode ack, PlayBackEngineState playBackEngineState, LightEngineState lightEngineState, ushort bufferFullness)
    {
        _bufferFullness = bufferFullness;
        _isPlaying = playBackEngineState == PlayBackEngineState.Playing;
        BufferFullness.DirtyFlag.Invalidate();
    }

    private void HandleConnectionStateChanged(bool isConnected)
    {
        _isConnected = isConnected;
        IsConnected.DirtyFlag.Invalidate();
        SetStatus(isConnected ? "Connected" : "Disconnected", isConnected ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Notice);
    }

    private DacPointDto[] ConvertPoints(LaserPoint[] points)
    {
        var dacPoints = new DacPointDto[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            dacPoints[i] = new DacPointDto
            {
                Control = 0,
                X = (short)Math.Clamp(p.X, short.MinValue, short.MaxValue),
                Y = (short)Math.Clamp(p.Y, short.MinValue, short.MaxValue),
                R = (ushort)Math.Clamp(p.R, 0, ushort.MaxValue),
                G = (ushort)Math.Clamp(p.G, 0, ushort.MaxValue),
                B = (ushort)Math.Clamp(p.B, 0, ushort.MaxValue),
                I = (ushort)Math.Clamp(p.I, 0, ushort.MaxValue),
                U1 = 0,
                U2 = 0,
            };
        }

        if (_printToLog && dacPoints.Length > 0)
        {
            var first = dacPoints[0];
            Log.Debug($"EtherDream: Converted {dacPoints.Length} points. First DAC: X={first.X} Y={first.Y} R={first.R} G={first.G} B={first.B} I={first.I}", this);
        }

        return dacPoints;
    }

    private void EnqueuePoints(LaserPoint[] points, int count)
    {
        if (count == 0)
            return;

        // Limit queue size, drop oldest if full
        while (_pointQueue.Count >= MaxQueuedFrames)
        {
            if (!_pointQueue.TryDequeue(out _))
                break;
        }

        _pointQueue.Enqueue(points);

        if (_printToLog && count > 0)
        {
            var first = points[0];
            Log.Debug($"EtherDream: Enqueued {count} points. Queue: {_pointQueue.Count}. First: X={first.X} Y={first.Y} R={first.R} G={first.G} B={first.B} I={first.I}", this);
        }
    }

    #region Network Interfaces (Keeping custom implementation for UI dropdown)
    private static List<NetworkAdapterInfo> GetNetworkInterfaces()
    {
        var list = new List<NetworkAdapterInfo> { new(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "Localhost (127.0.0.1)") };
        try
        {
            list.AddRange(from ni in NetworkInterface.GetAllNetworkInterfaces()
                          where ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          from ip in ni.GetIPProperties().UnicastAddresses
                          where ip.Address.AddressFamily == AddressFamily.InterNetwork
                          select new NetworkAdapterInfo(ip.Address, ip.IPv4Mask, ni.Name));
        }
        catch { }
        return list;
    }

    private sealed record NetworkAdapterInfo(IPAddress IpAddress, IPAddress SubnetMask, string Name)
    {
        public string DisplayName => $"{Name}: {IpAddress}";
    }
    #endregion

    private IPAddress _selectedSubnetMask;
    private int _bufferFullness;

    private void UpdateBufferStatus()
    {
        BufferFullness.Value = _bufferFullness;
    }

    private void UpdateStatus()
    {
        if (!_lastEnable)
        {
            SetStatus("Output disabled. Enable 'Enable'.", IStatusProvider.StatusLevel.Notice);
            StatusMessage.Value = _statusMessage;
        }
        else if (_simulationMode)
        {
            StatusMessage.Value = $"SIMULATION MODE - Points sent: {_totalPointsSent}";
        }
        else if (_isConnected)
        {
            StatusMessage.Value = _isPlaying
                                      ? $"Streaming - Buffer: {_bufferFullness} points"
                                      : $"Connected - Buffer: {_bufferFullness} points";
        }
        else if (!string.IsNullOrEmpty(_lastIpAddress))
        {
            StatusMessage.Value = $"Connecting to {_lastIpAddress}...";
        }
        else
        {
            StatusMessage.Value = "No IP address configured";
        }

        PointsSent.Value = _totalPointsSent;
        PointsSent.DirtyFlag.Invalidate();
    }

    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
    }

    private void SimulateSendPoints(LaserPoint[] points, int count)
    {
        _totalPointsSent += count;
        _lastPointCount = count;

        if (count > 0 && _shouldLogFirstPoint)
        {
            var firstPoint = points[0];
            if (_printToLog) Log.Debug($"EtherDream SIM: First point - X:{firstPoint.X} Y:{firstPoint.Y} R:{firstPoint.R} G:{firstPoint.G} B:{firstPoint.B}", this);
            _shouldLogFirstPoint = false;
        }

        if (count > 0)
        {
            _hasDataToSend = true;
        }
    }

    private void SimulateBufferStatus()
    {
        if (_hasDataToSend)
        {
            _bufferFullness = Math.Min(100, _bufferFullness + 10);
            _hasDataToSend = false;
        }
        else
        {
            _bufferFullness = Math.Max(0, _bufferFullness - 5);
        }
    }

    #region IStatusProvider
    private string _statusMessage = "Disconnected";
    private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return _statusLevel;
    }

    public string GetStatusMessage()
    {
        return _statusMessage;
    }
    #endregion

    private volatile bool _disposed;
    private volatile bool _isConnected;
    private volatile bool _isPlaying;
    private bool _lastConnectState;
    private bool _lastSimulationForConnection;
    private string _lastIpAddress = string.Empty;
    private bool _lastEnable;
    private volatile int _scanRate = 30000;

    private bool _simulationMode;
    private bool _printToLog;
    private volatile bool _loopLastFrame = true;
    private bool _discoveryUnavailable;
    private bool _lastClearEStop;
    private volatile bool _clearEStopRequested;
    private long _totalPointsSent;
    private int _lastPointCount;
    private bool _hasDataToSend;
    private bool _shouldLogFirstPoint = true;

    // Inputs
    [Input(Guid = "02674C5E-E869-4572-B8FD-2EF35ADB5A5A")]
    public readonly InputSlot<T3.Core.DataTypes.StructuredList> LaserPoints = new InputSlot<T3.Core.DataTypes.StructuredList>();

    [Input(Guid = "B1C2D3E4-F5A6-7890-BCDE-123456789ABC")]
    public readonly InputSlot<string> LocalIpAddress = new InputSlot<string>();

    [Input(Guid = "C12CD058-8C3A-426F-9AF0-17EC41319A66")]
    public readonly InputSlot<string> IpAddress = new InputSlot<string>();

    [Input(Guid = "1390D6F9-67C2-4F74-9438-7FA60A4BB5D6")]
    public readonly InputSlot<int> Port = new InputSlot<int>();

    [Input(Guid = "64A26581-2477-4F80-AE7B-0FDB27C0A101")]
    public readonly InputSlot<int> ScanRate = new InputSlot<int>();

    [Input(Guid = "0C7EE9F2-2009-4CE4-A04B-ED7D8386F146")]
    public readonly InputSlot<bool> Enable = new InputSlot<bool>();

    [Input(Guid = "90FBB4D3-37CE-4BF7-9638-729D06A2C6F8")]
    public readonly InputSlot<bool> SimulationMode = new InputSlot<bool>();

    [Input(Guid = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    public readonly InputSlot<bool> DiscoverDevices = new InputSlot<bool>();

    [Input(Guid = "B0B4F4C2-5F4A-497D-BED4-B40E8E159B4C")]
    public readonly InputSlot<bool> PrintToLog = new InputSlot<bool>();

    [Input(Guid = "E7F8A9B0-C1D2-4E3F-9A8B-7C6D5E4F3B2A")]
    public readonly InputSlot<bool> LoopLastFrame = new InputSlot<bool>(true);

    [Input(Guid = "F3A4B5C6-D7E8-4901-8234-56789ABCDEF0")]
    public readonly InputSlot<bool> ClearEStop = new InputSlot<bool>();

    #region ICustomDropdownHolder
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == IpAddress.Id)
            return IpAddress.Value ?? string.Empty;
        if (inputId == LocalIpAddress.Id)
            return LocalIpAddress.Value ?? string.Empty;
        return string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == LocalIpAddress.Id)
        {
            // Use our custom GetNetworkInterfaces for local IP dropdown
            _networkInterfaces = GetNetworkInterfaces(); // Refresh list
            foreach (var adapter in _networkInterfaces)
            {
                yield return adapter.DisplayName;
            }
        }
        else if (inputId == IpAddress.Id && (_deviceDiscovery != null || DeviceDiscovery.DiscoveredDevices.Count > 0))
        {
            // Use library's static discovered devices dictionary (shared across instances)
            foreach (var device in DeviceDiscovery.DiscoveredDevices.Values)
            {
                var name = DeviceDiscovery.GetDeviceName(device);
                yield return $"{name} ({device.Ip})";
            }
        }
    }

    public void HandleResultForInput(Guid inputId, string selected, bool isAListItem)
    {
        if (inputId == LocalIpAddress.Id)
        {
            // Extract IP from "Name: IP"
            var parts = selected.Split(": ");
            if (parts.Length > 1)
            {
                LocalIpAddress.SetTypedInputValue(parts[1]);
            }
        }
        else if (inputId == IpAddress.Id)
        {
            // Extract IP from "Name (IP)"
            var start = selected.LastIndexOf('(');
            var end = selected.LastIndexOf(')');
            if (start != -1 && end != -1 && end > start)
            {
                var ip = selected.Substring(start + 1, end - start - 1);
                IpAddress.SetTypedInputValue(ip);
            }
        }
    }
    #endregion
}
