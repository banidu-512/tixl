#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LaserCore.EtherDream.Net.Device;
using LaserCore.EtherDream.Net.Discovery;
using LaserCore.EtherDream.Net.Dto;
using LaserCore.EtherDream.Net.Enums;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator.Interfaces;

namespace T3.Core.Output;

/// <summary>
/// The device side of a laser output, owned by the Output Setup instead of graph operators: one
/// <see cref="LaserOutputStream"/> per bound laser plug streams the frames the setup's laser
/// pipeline produces to that plug's hardware. The stream runs its own connection/send threads and
/// applies the plug's device settings (scan rate, brightness ceiling, channel order) as the final
/// audit before encoding.
///
/// Safety model: the output's safety limits (safe zone, jump/dwell limits, brightness) are
/// re-applied by <see cref="SubmitFrame"/> at the stream door, so nothing unclamped can reach an
/// encode even from a caller that skipped the setup pipeline; the global
/// <see cref="LaserMasterEnable"/> blanks the beam while the scanners keep scanning the last
/// geometry (settled blanking), a frame watchdog blanks the beam when no fresh frames arrive
/// within its grace period, and every frame leads with dark approach points so a fresh frame
/// never streaks the beam from the scanners' park position.
/// </summary>
public sealed class LaserOutputStream : IDisposable
{
    /// <summary>Frame watchdog grace period: no fresh frames for this long blanks the beam.</summary>
    private const float WatchdogTimeoutSec = 1f;

    public static LaserOutputStream? Create(LaserPlug plug)
    {
        try
        {
            return plug.Kind switch
                       {
                           LaserPlug.Kinds.EtherDream => new LaserOutputStream(new EtherDreamStream(plug)),
                           LaserPlug.Kinds.Ponk => new LaserOutputStream(new PonkStream(plug)),
                           LaserPlug.Kinds.Citp => new LaserOutputStream(new CitpStream(plug)),
                           _ => null,
                       };
        }
        catch (Exception e)
        {
            Log.Warning($"Laser output stream for plug '{plug.Name}' failed to start - {e.Message}");
            return null;
        }
    }

    private LaserOutputStream(IStream inner)
    {
        _inner = inner;
        _safetyStats = new LaserSafetyTransforms.Stats();
    }

    private readonly IStream _inner;
    private readonly List<LaserPoint> _safetyWorkList = new(4096);
    private LaserSafetyTransforms.Stats _safetyStats;

    /// <summary>
    /// Queues a frame for the send thread, re-applying the output's safety limits at the door -
    /// the non-bypassable stage that runs no matter what the caller did upstream. Copies out of
    /// the caller's buffer; call from one thread only (the render thread).
    /// </summary>
    public void SubmitFrame(LaserPoint[] frame, in LaserSafetyTransforms.Config safety)
    {
        var count = frame.Length;
        if (count == 0)
            return;

        // The safety transforms are idempotent (clamps, subdivision, dwell blanking), so
        // re-applying the stage the setup pipeline already ran is safe and never inflates
        // the point count.
        _safetyWorkList.Clear();
        for (var i = 0; i < count; i++)
            _safetyWorkList.Add(frame[i]);

        var result = LaserSafetyTransforms.Apply(_safetyWorkList, in safety, ref _safetyStats);
        _inner.SubmitFrame(result.ToArray());
    }

    public bool IsConnected => _inner.IsConnected;

    public IStatusProvider.StatusLevel StatusLevel => _inner.StatusLevel;
    public string StatusMessage => _inner.StatusMessage;

    public void Dispose()
    {
        var inner = _inner;
        _ = Task.Run(() =>
                         {
                             try { inner.Dispose(); }
                             catch { /* sockets may already be dead */ }
                         });
    }

    /// <summary>Protocol-specific transport behind a <see cref="LaserOutputStream"/>.</summary>
    private interface IStream : IDisposable
    {
        void SubmitFrame(LaserPoint[] frame);
        bool IsConnected { get; }
        IStatusProvider.StatusLevel StatusLevel { get; }
        string StatusMessage { get; }
    }

    #region Shared helpers (blanking, watchdog, dark approach)

    /// <summary>Returns a blanked copy (positions kept, colors zeroed) via a reusable scratch buffer.</summary>
    internal static LaserPoint[] BlankFrame(LaserPoint[] source, ref LaserPoint[] scratch)
    {
        if (scratch.Length != source.Length)
            scratch = new LaserPoint[source.Length];
        LaserSafetyTransforms.BlankCopyInto(source, scratch);
        return scratch;
    }

    /// <summary>True when no fresh frame has been submitted within the watchdog window.</summary>
    internal static bool WatchdogElapsed(long lastSubmitTimestamp)
    {
        if (WatchdogTimeoutSec <= 0f)
            return false;
        var last = Interlocked.Read(ref lastSubmitTimestamp);
        if (last == 0)
            return false;
        return Environment.TickCount64 - last > (long)(WatchdogTimeoutSec * 1000f);
    }

    /// <summary>Leads the frame with dark copies of its first point: the scanners travel from
    /// wherever they are parked to the shape's start with the beam off.</summary>
    internal static LaserPoint[] PrependDarkApproach(LaserPoint[] frame, ref LaserPoint[] scratch)
    {
        const int approachPoints = 6;
        var total = frame.Length + approachPoints;
        if (scratch.Length != total)
            scratch = new LaserPoint[total];

        var first = frame[0];
        first.R = 0;
        first.G = 0;
        first.B = 0;
        first.I = 0;
        for (var i = 0; i < approachPoints; i++)
            scratch[i] = first;

        Array.Copy(frame, 0, scratch, approachPoints, frame.Length);
        return scratch;
    }

    /// <summary>Queue bookkeeping shared by both transports: bounds the queue, copies the frame
    /// out of the upstream buffer (producers reuse their allocation every frame) and resets the
    /// watchdog clock.</summary>
    internal static LaserPoint[]? CopyForQueue(LaserPoint[] frame, ConcurrentQueue<LaserPoint[]> queue, int maxQueuedFrames)
    {
        if (frame.Length == 0)
            return null;

        while (queue.Count >= maxQueuedFrames)
        {
            if (!queue.TryDequeue(out _))
                break;
        }

        var copy = new LaserPoint[frame.Length];
        Array.Copy(frame, copy, frame.Length);
        return copy;
    }
    #endregion

    #region EtherDream transport
    /// <summary>
    /// Streams to an Ether Dream DAC over TCP, lifted from the former [EtherDreamOutput] operator.
    /// The two unkillable rules of the send loop carry over unchanged: StreamPoints is only called
    /// once a status poll confirmed buffer room (a full buffer blocks inside the library's socket
    /// lock forever when the engine has halted), and a halted engine is recovered IN PLACE
    /// (Prepare discards the stuck queue; reconnect churn is what wedges this DAC's firmware).
    /// </summary>
    private sealed class EtherDreamStream : IStream
    {
        public EtherDreamStream(LaserPlug plug)
        {
            _plug = plug;
            var device = plug.EtherDreamSettings;
            _scanRate = Math.Clamp(device.ScanRate, 100, 100000);
            if (_scanRate == 100 && device.ScanRate < 100)
                _scanRate = 30000; // plug left the scan rate at its default (0)
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _localAddress = plug.LocalAddress ?? string.Empty;

            // The plug picker lists DACs while a stream runs: a connected DAC stops its
            // discovery broadcasts, so start the shared listener with the stream.
            LaserPlugDiscovery.EnsureEtherDreamDiscovery();

            StartConnection();
        }

        private readonly LaserPlug _plug;
        private readonly int _scanRate;
        private readonly float _maxBrightness;
        private readonly bool _swapColorChannels;
        private readonly string _localAddress;

        // Library instances / connection state
        private DeviceDiscovery? _deviceDiscovery;
        private Dac? _dac;
        private CancellationTokenSource? _connectionCts;
        private readonly ConcurrentQueue<LaserPoint[]> _pointQueue = new();
        private volatile LaserPoint[]? _lastFrame;   // written by the send loop, cleared on teardown

        // Timing / retry tuning (operator-proven values)
        private const int SendRetryDelayMs = 250;
        private const int WarmupRetryDelayMs = 500;
        private const int MaxWarmupRetries = 120;      // ~60s for the light engine to become ready
        private const int MaxConsecutiveSendErrors = 5;
        private const int SendStallTimeoutMs = 3000;   // StreamPoints spins on a full, stopped buffer — abandon and reconnect
        private const int PlaybackStallDelayMs = 1000; // not-playing for this long while streaming → explicit re-begin
        private const int RebeginMinIntervalMs = 1000;
        private const int MaxQueuedFrames = 2;
        private const int MinChunkPoints = 32;

        public bool IsConnected => _isConnected;
        public IStatusProvider.StatusLevel StatusLevel => _statusLevel;
        public string StatusMessage => _statusMessage;

        public void SubmitFrame(LaserPoint[] frame)
        {
            var copy = CopyForQueue(frame, _pointQueue, MaxQueuedFrames);
            if (copy == null)
                return;

            Interlocked.Exchange(ref _lastEnqueueTimestamp, Environment.TickCount64);
            _watchdogBlanked = false;
            _pointQueue.Enqueue(copy);
        }

        private void StartConnection()
        {
            TearDown();
            var address = ResolveAddress();
            if (string.IsNullOrWhiteSpace(address))
            {
                SetStatus("No DAC address (discovering...)", IStatusProvider.StatusLevel.Notice);
                return;
            }

            var cts = new CancellationTokenSource();
            _connectionCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await ConnectionLoopAsync(address, token);
                                }
                                finally
                                {
                                    // The loop owns the CTS; by the time we get here cancellation was
                                    // already processed, so disposing cannot race with Task.Delay registrations
                                    cts.Dispose();
                                }
                            });
        }

        /// <summary>The plug's address, or the first discovered DAC when empty (zero-touch).</summary>
        private string ResolveAddress()
        {
            if (!string.IsNullOrWhiteSpace(_plug.Address))
                return _plug.Address;

            try
            {
                if (_autoSelectedIp == null || !DeviceDiscovery.DiscoveredDevices.ContainsKey(_autoSelectedIp))
                {
                    var first = DeviceDiscovery.DiscoveredDevices.Values.FirstOrDefault();
                    var picked = first.Ip;
                    if (!string.IsNullOrWhiteSpace(picked) && picked != _autoSelectedIp)
                    {
                        _autoSelectedIp = picked;
                        Log.Debug($"EtherDream stream: auto-selected DAC at {_autoSelectedIp}");
                    }
                }

                return _autoSelectedIp ?? string.Empty;
            }
            catch
            {
                // The discovery loop mutates the dictionary concurrently - a transient
                // race just skips this frame's auto-pick
                return _autoSelectedIp ?? string.Empty;
            }
        }

        private string? _autoSelectedIp;

        private void TearDown()
        {
            var cts = Interlocked.Exchange(ref _connectionCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            var dac = Interlocked.Exchange(ref _dac, null);
            if (dac != null)
            {
                _ = Task.Run(() =>
                                 {
                                     try { DisposeDacQuietly(dac); }
                                     catch { }
                                 });
                LaserPlugDiscovery.ClearConnectedDevice(_lastConnectIp ?? string.Empty);
            }

            _isConnected = false;
            _isPlaying = false;
            _pointQueue.Clear();
            _lastFrame = null;
        }

        /// <summary>
        /// Runs on a background thread because the Dac constructor performs a blocking TCP connect
        /// that can stall for seconds when the device is unreachable. Retries until cancelled with
        /// progressive backoff 1s → 30s — hammering an unreachable or just-rebooted DAC with fixed
        /// fast retries is exactly the churn that wedges its firmware.
        /// </summary>
        private async Task ConnectionLoopAsync(string ipAddress, CancellationToken token)
        {
            var attempt = 0;

            while (!token.IsCancellationRequested)
            {
                attempt++;
                Dac? dac = null;
                var connectedAtUtc = DateTime.UtcNow;
                try
                {
                    SetStatus(attempt == 1
                                  ? $"Connecting to {ipAddress}..."
                                  : $"Connecting to {ipAddress} (attempt {attempt})...", IStatusProvider.StatusLevel.Notice);

                    dac = new Dac(ipAddress);
                    if (token.IsCancellationRequested)
                    {
                        DisposeDacQuietly(dac);
                        return;
                    }

                    dac.StatusUpdated += HandleStatusUpdate;
                    dac.DeviceConnected += () => HandleConnectionStateChanged(true);
                    dac.DeviceDisconnected += () => HandleConnectionStateChanged(false);
                    dac.SafetyFaultDetected += reason => Log.Warning($"EtherDream stream: safety fault - {reason}");

                    // A connected DAC stops its discovery broadcasts — report it so the plug picker
                    // can still offer the device a show is streaming to.
                    LaserPlugDiscovery.SetConnectedDevice("Ether Dream", ipAddress);

                    _dac = dac;
                    _lastConnectIp = ipAddress;
                    _isConnected = true;
                    _killedSocket = false;
                    // Arm the stall watchdog from connect time: the light engine NACKs prepare/begin
                    // for a moment while warming up, and a kick fired from stale (default) timestamps
                    // would flag that normal warmup as a stall on the very first send.
                    _lastPlayingUtc = DateTime.UtcNow;
                    SetStatus($"Connected to {ipAddress}", IStatusProvider.StatusLevel.Success);

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

                    if (token.IsCancellationRequested)
                        return;

                    SetStatus($"Connection lost: {e.Message} - retrying...", IStatusProvider.StatusLevel.Warning);
                    if (attempt == 1)
                        Log.Warning($"EtherDream stream: connection to {ipAddress} failed - {e.Message}");

                    // A connection that held for a while earns a fresh ladder - it served the
                    // show and its death is a new incident, not more of the same failure.
                    if (DateTime.UtcNow - connectedAtUtc > TimeSpan.FromSeconds(30))
                        attempt = 1;
                }

                var backoffMs = (int)Math.Min(1000L << Math.Min(attempt - 1, 5), 30_000);
                try
                {
                    await Task.Delay(backoffMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Streams queued points to the DAC under two unkillable rules. (1) StreamPoints is only
        /// called once a status poll confirmed the DAC actually has buffer room - a full buffer
        /// blocks inside the library's socket lock forever when the engine has halted. (2) A
        /// halted engine is recovered IN PLACE: Prepare() discards the stuck queue and the next
        /// send re-runs the library's p→d→b session.
        /// </summary>
        private async Task SendLoopAsync(Dac dac, CancellationToken token)
        {
            var consecutiveErrors = 0;
            var warmupRetries = 0;
            var capacity = dac.BufferCapacity > 0 ? (int)dac.BufferCapacity : 1800;
            var recoverEscalation = 0;              // 0 = soft Prepare, 1+ = full Stop+ClearEStop+Prepare
            var lastRecoverUtc = DateTime.MinValue; // rate-limits the in-place recovery
            var lastPingUtc = DateTime.MinValue;
            var pingFailures = 0;

            while (!token.IsCancellationRequested)
            {
                if (_isPlaying)
                    recoverEscalation = 0;

                // Master off: settled blanking. The blanked copy of the last frame must keep
                // being STREAMED - the DAC loops whatever its buffer holds, so skipping the
                // send would leave the last lit frame burning on the wall. The pick below
                // ignores queued frames while the master is off and zeroes the geometry.
                var masterOff = !LaserMasterEnable.Enabled;
                if (masterOff)
                {
                    _pointQueue.Clear();
                    if (_lastFrame is { Length: > 0 } && !_masterBlankApplied)
                    {
                        _lastFrame = BlankFrame(_lastFrame, ref _blankFrameScratch);
                        _masterBlankApplied = true;
                    }
                }
                else
                {
                    _masterBlankApplied = false;
                }

                // Pick the frame to stream: fresh frames first, otherwise loop the last one
                // so the buffer stays fed between the driver's ~30 Hz submissions. Looping
                // bypasses the watchdog clock, which measures FRESH frames only.
                if (_sendFrame == null || _sendOffset >= _sendFrame.Length)
                {
                    LaserPoint[]? frame = null;
                    if (!masterOff && _pointQueue.TryDequeue(out var points) && points.Length > 0)
                    {
                        frame = points;
                        // Every fresh frame leads beam-off: the approach to its first point is
                        // drawn dark no matter what the content's own first point looks like,
                        // so a pattern flip can never streak the beam across the show.
                        _needsDarkApproach = true;
                    }
                    else if (_lastFrame is { Length: > 0 })
                    {
                        if (masterOff)
                        {
                            frame = _lastFrame;
                        }
                        else if (WatchdogElapsed(_lastEnqueueTimestamp))
                        {
                            if (!_watchdogBlanked)
                            {
                                _watchdogBlanked = true;
                                Log.Warning("EtherDream stream: frame watchdog - no new frames, blanking output");
                                SetStatus("Frame watchdog - no new frames, output blanked", IStatusProvider.StatusLevel.Warning);
                            }
                            frame = BlankFrame(_lastFrame, ref _blankFrameScratch);
                        }
                        else
                        {
                            frame = _lastFrame;
                        }
                    }

                    if (frame == null)
                    {
                        // Idle (no content yet): heartbeat keeps the connection warm instead of
                        // letting it go stale - a live show must never drop the DAC link.
                        PollDac(dac, ref lastPingUtc, ref pingFailures);
                        try { await Task.Delay(5, token); }
                        catch (OperationCanceledException) { return; }
                        continue;
                    }

                    // Galvo safety: after any engine (re)start the scanners sit at their park
                    // position (center). Lead the frame with dark copies of its first point so
                    // the travel from park to the shape start happens beam-off.
                    _sendFrameRaw = frame;
                    if (_needsDarkApproach && frame.Length > 0)
                    {
                        frame = PrependDarkApproach(frame, ref _approachScratch);
                        _needsDarkApproach = false;
                    }

                    _sendFrame = frame;
                    _sendOffset = 0;
                }

                // The DAC's real capacity can exceed the library's default (1799) - grow the
                // estimate from observed fullness so the chunk sizing never throttles early.
                if (_bufferFullness > capacity)
                    capacity = _bufferFullness;

                var room = capacity - _bufferFullness;

                // In-place playback recovery: after a halt the engine sits idle while the buffer
                // stays full. Prepare() discards the stuck queue; the chunk sends then re-run the
                // library's own p→d→b session and playback resumes. Gated on a FULL buffer: while
                // there is room, our own chunk sends re-prepare on the fly, and a recovery here
                // would fight the streaming and reset playback every couple of seconds.
                if (!_isPlaying
                    && room < MinChunkPoints
                    && DateTime.UtcNow - _lastPlayingUtc > TimeSpan.FromMilliseconds(PlaybackStallDelayMs)
                    && DateTime.UtcNow - lastRecoverUtc > TimeSpan.FromMilliseconds(RebeginMinIntervalMs))
                {
                    lastRecoverUtc = DateTime.UtcNow;
                    try
                    {
                        if (recoverEscalation == 0)
                        {
                            dac.Prepare();
                        }
                        else
                        {
                            // Soft recovery did not take - reset the DAC's state machine fully
                            dac.Stop();
                            dac.ClearEStop();
                            dac.Prepare();
                        }

                        Log.Warning($"EtherDream stream: playback halt - in-place recovery (level {recoverEscalation}) sent");
                        SetStatus($"Playback halted - recovering (level {recoverEscalation})...", IStatusProvider.StatusLevel.Warning);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"EtherDream stream: in-place recovery failed - {e.Message} (recycling connection)");
                        throw;
                    }

                    recoverEscalation++;
                    pingFailures = 0;
                    _sendOffset = 0;
                    _needsDarkApproach = true;
                    continue;
                }

                // The DAC's real capacity can exceed the library's default (1799) - grow the
                // estimate from observed fullness so the chunk sizing never throttles early.
                if (_bufferFullness > capacity)
                    capacity = _bufferFullness;

                // Chunked streaming: push exactly as many points as the DAC reports room for,
                // in one data command each - the socket lock is held only for milliseconds.
                if (room < MinChunkPoints)
                {
                    PollDac(dac, ref lastPingUtc, ref pingFailures);
                    try { await Task.Delay(2, token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                var remaining = _sendFrame.Length - _sendOffset;
                var chunkLength = Math.Min(room, remaining);
                var chunk = new LaserPoint[chunkLength];
                Array.Copy(_sendFrame, _sendOffset, chunk, 0, chunkLength);

                try
                {
                    // Each chunk fits the reported room, so this returns after a single data
                    // command. The bounded wait is belt and braces for a socket-level hang; on
                    // timeout the socket is killed (unwinding the abandoned call) and the
                    // connection recycled.
                    var sendTask = Task.Run(() => dac.StreamPoints(ConvertPoints(chunk), (uint)_scanRate));
                    if (!sendTask.Wait(SendStallTimeoutMs, token))
                    {
                        _killedSocket = true;
                        KillDacSocket(dac);
                        throw new TimeoutException("DAC send stalled - connection recycled");
                    }

                    consecutiveErrors = 0;
                    warmupRetries = 0;
                    pingFailures = 0;
                    _sendOffset += chunkLength;
                    if (_sendOffset >= _sendFrame.Length)
                    {
                        RememberFrame(_sendFrameRaw);
                        _sendFrame = null;
                    }
                }
                catch (AggregateException ae) when (!token.IsCancellationRequested && ae.InnerException is InvalidOperationException ex)
                {
                    // Light engine not ready yet - warm-up needs waiting out
                    if (++warmupRetries > MaxWarmupRetries)
                        throw;

                    if (ex.Message.Contains("EmergencyStop"))
                    {
                        SetStatus("E-stop active on device", IStatusProvider.StatusLevel.Warning);
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

                    // A timed-out send leaves an abandoned StreamPoints spinning on the library's
                    // socket lock - every later command would deadlock on it. Its socket was
                    // killed, which unwinds that call; recycle the whole connection.
                    if (consecutiveErrors >= MaxConsecutiveSendErrors || _killedSocket)
                        throw;

                    SetStatus($"Connection problems - retrying ({consecutiveErrors}/{MaxConsecutiveSendErrors})...", IStatusProvider.StatusLevel.Warning);
                    await Task.Delay(SendRetryDelayMs, token);
                }
            }
        }

        /// <summary>
        /// Throttled DAC status poll while no points flow (or the buffer is full): refreshes
        /// buffer fullness and playback state straight from the response. Throws after repeated
        /// failures so a silently-dead link gets recycled instead of spinning forever.
        /// </summary>
        private void PollDac(Dac dac, ref DateTime lastPingUtc, ref int pingFailures)
        {
            // Fast while waiting for buffer room: the poll cadence caps sustainable send rate.
            if (DateTime.UtcNow - lastPingUtc < TimeSpan.FromMilliseconds(4))
                return;

            lastPingUtc = DateTime.UtcNow;
            try
            {
                var status = dac.Ping().DacStatus;
                pingFailures = 0;
                _bufferFullness = status.BufferFullness;
                var playing = status.PlayBackState == PlayBackEngineState.Playing;
                _isPlaying = playing;
                if (playing)
                {
                    _lastPlayingUtc = DateTime.UtcNow;
                }
                else
                {
                    _needsDarkApproach = true;
                }
            }
            catch
            {
                if (++pingFailures >= 5)
                    throw new InvalidOperationException("DAC heartbeat lost");
            }
        }

        private void RememberFrame(LaserPoint[] points)
        {
            if (_lastFrame == null || _lastFrame.Length != points.Length)
                _lastFrame = new LaserPoint[points.Length];

            Array.Copy(points, _lastFrame, points.Length);
        }

        private void HandleStatusUpdate(AckCode ack, PlayBackEngineState playBackEngineState, LightEngineState lightEngineState, ushort bufferFullness)
        {
            // Runs on a library callback thread - only plain field writes here.
            _bufferFullness = bufferFullness;
            var playing = playBackEngineState == PlayBackEngineState.Playing;
            _isPlaying = playing;
            if (playing)
            {
                _lastPlayingUtc = DateTime.UtcNow;
            }
            else
            {
                // Engine went idle - the scanners will park. Arm the dark approach so the next
                // frame travels to its start point beam-off.
                _needsDarkApproach = true;
            }
        }

        private void HandleConnectionStateChanged(bool isConnected)
        {
            _isConnected = isConnected;
            if (isConnected)
                _needsDarkApproach = true;
            SetStatus(isConnected ? "Connected" : "Disconnected", isConnected ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Notice);
        }

        private DacPointDto[] ConvertPoints(LaserPoint[] points)
        {
            // Final output audit: coordinates/colors are clamped into hardware range and
            // scaled by the brightness ceiling right before encoding - independent of
            // whatever upstream content sent.
            var scale = _maxBrightness;
            var swap = _swapColorChannels;
            var dacPoints = new DacPointDto[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                dacPoints[i] = new DacPointDto
                                   {
                                       Control = 0,
                                       X = (short)Math.Clamp(p.X, short.MinValue, short.MaxValue),
                                       Y = (short)Math.Clamp(p.Y, short.MinValue, short.MaxValue),
                                       R = (ushort)Math.Clamp((int)MathF.Round(Math.Max(swap ? p.B : p.R, 0) * scale), 0, ushort.MaxValue),
                                       G = (ushort)Math.Clamp((int)MathF.Round(Math.Max(p.G, 0) * scale), 0, ushort.MaxValue),
                                       B = (ushort)Math.Clamp((int)MathF.Round(Math.Max(swap ? p.R : p.B, 0) * scale), 0, ushort.MaxValue),
                                       I = (ushort)Math.Clamp((int)MathF.Round(Math.Max(p.I, 0) * scale), 0, ushort.MaxValue),
                                       U1 = 0,
                                       U2 = 0,
                                   };
            }

            return dacPoints;
        }

        private static void DisposeDacQuietly(Dac? dac)
        {
            if (dac == null)
                return;

            try
            {
                KillDacSocket(dac);
            }
            catch
            {
                // Reflection failed - Dispose below still tries the polite path
            }

            try
            {
                dac.Dispose();
            }
            catch
            {
                // Socket may already be dead
            }
        }

        /// <summary>
        /// Closes the DAC's TCP socket directly, bypassing the library's socket lock. The only
        /// writer that can hold that lock forever is an abandoned StreamPoints spinning on a
        /// halted engine - and Dispose itself takes the lock, so without this a stalled send
        /// would deadlock the whole teardown.
        /// </summary>
        private static void KillDacSocket(Dac dac)
        {
            var field = typeof(Dac).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(dac) is not TcpClient socket)
                return;

            try { socket.Client?.Close(); }
            catch { }

            try { socket.Close(); }
            catch { }
        }

        public void Dispose() => TearDown();

        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        // State (same volatile discipline as the operator: send-thread writers, UI readers)
        private string _statusMessage = "Disconnected";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
        private volatile bool _isConnected;
        private volatile bool _isPlaying;
        private DateTime _lastPlayingUtc;
        private string? _lastConnectIp;
        private volatile int _bufferFullness;
        private long _lastEnqueueTimestamp;
        private volatile bool _watchdogBlanked;
        private bool _masterBlankApplied;
        private LaserPoint[] _blankFrameScratch = Array.Empty<LaserPoint>();
        private LaserPoint[] _approachScratch = Array.Empty<LaserPoint>();
        private volatile bool _killedSocket;
        private volatile bool _needsDarkApproach;
        private LaserPoint[]? _sendFrame;
        private int _sendOffset;
        private LaserPoint[]? _sendFrameRaw;
    }
    #endregion

    #region PONK transport
    /// <summary>
    /// Streams laser frames as PONK-over-UDP (MadMapper's laser protocol), lifted from the former
    /// [PONKOutput] operator: 52-byte chunk headers, up to 1420 bytes of frame data per datagram,
    /// one virtual sender per stream. UDP is best-effort — frames are dropped, never queued up.
    /// </summary>
    private sealed class PonkStream : IStream
    {
        #region PONK Protocol Constants (from Common/Cpp/PonkDefs.h in the MadMapper/Ponk repo)
        private const string HeaderString = "PONK-UDP";
        private const byte ProtocolVersion = 0;
        // 8 magic + 1 version + 4 sender id + 32 name + 1 frame + 1 chunk count + 1 chunk number + 4 crc.
        private const int HeaderSize = 52;
        private const byte DataFormatXyF32RgbU8 = 1; // Mandatory minimum receivers must support
        private const int MaxChunkPayload = 1472 - HeaderSize;
        private const int MaxDatagramSize = 1472;
        private const int MaxPointsPerFrame = 32000;
        private static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.10.24");
        private const int DefaultPort = 5583;
        private const int MaxSenderNameBytes = 32; // Null-terminated, so 31 usable chars
        private const int SendRetryDelayMs = 50;
        private const int MaxConsecutiveSendErrors = 20;
        private const int MaxQueuedFrames = 5;
        private const int ConnectRetryDelayMs = 2000;
        #endregion

        public PonkStream(LaserPlug plug)
        {
            _plug = plug;
            var device = plug.PonkSettings;
            _useMulticast = device.UseMulticast;
            _senderName = string.IsNullOrWhiteSpace(device.SenderName) ? "TiXL" : device.SenderName;
            _maxScanSpeed = MathF.Max(0.01f, device.MaxScanSpeed);
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _localAddress = plug.LocalAddress ?? string.Empty;
            _targetAddress = string.IsNullOrWhiteSpace(plug.Address) ? DefaultMulticastAddress.ToString() : plug.Address;
            _port = plug.Port > 0 ? plug.Port : DefaultPort;

            StartConnection();
        }

        private readonly LaserPlug _plug;
        private readonly bool _useMulticast;
        private readonly string _senderName;
        private readonly float _maxScanSpeed;
        private readonly float _maxBrightness;
        private readonly bool _swapColorChannels;
        private readonly string _localAddress;
        private readonly string _targetAddress;
        private readonly int _port;

        // Stable sender identifier per stream - PONK spec says the receiver keys on this int
        // and a reload should reuse the same id so the receiver treats it as the same source.
        private readonly int _senderId = Random.Shared.Next(int.MinValue, int.MaxValue);

        private readonly ConcurrentQueue<LaserPoint[]> _pointQueue = new();
        private CancellationTokenSource? _connectionCts;
        private Socket? _socket;
        private volatile LaserPoint[]? _lastFrame; // replaced wholesale (never mutated)
        private byte _frameNumber;

        private volatile bool _disposed;
        private volatile bool _isSending;

        public bool IsConnected => _isSending;
        public IStatusProvider.StatusLevel StatusLevel => _statusLevel;
        public string StatusMessage => _statusMessage;

        public void SubmitFrame(LaserPoint[] frame)
        {
            var copy = CopyForQueue(frame, _pointQueue, MaxQueuedFrames);
            if (copy == null)
                return;

            Interlocked.Exchange(ref _lastEnqueueTimestamp, Environment.TickCount64);
            _watchdogBlanked = false;
            _pointQueue.Enqueue(copy);
        }

        private void StartConnection()
        {
            TearDown();

            if (!IPAddress.TryParse(_targetAddress, out var targetIp))
            {
                SetStatus($"Invalid target IP '{_targetAddress}'", IStatusProvider.StatusLevel.Error);
                return;
            }

            IPAddress? localIp = null;
            if (!string.IsNullOrWhiteSpace(_localAddress) && IPAddress.TryParse(_localAddress, out var parsed))
                localIp = parsed;

            var cts = new CancellationTokenSource();
            _connectionCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await ConnectionLoopAsync(_useMulticast, localIp, targetIp, _port, token);
                                }
                                finally
                                {
                                    cts.Dispose();
                                }
                            });
        }

        private void TearDown()
        {
            var cts = Interlocked.Exchange(ref _connectionCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            var socket = Interlocked.Exchange(ref _socket, null);
            if (socket != null)
            {
                // Closing the socket off-thread protects against SO_LINGER / multicast leave hangs
                _ = Task.Run(() =>
                                 {
                                     try { socket.Close(); }
                                     catch { }
                                 });
            }

            _isSending = false;
            _pointQueue.Clear();
            _lastFrame = null;
        }

        /// <summary>
        /// Owns socket bring-up. UDP socket setup is cheap and non-blocking, but creating a
        /// socket on a chosen interface requires a bind - if the bind fails (port in use, bad
        /// interface) we retry with backoff. Once the socket is up, the send loop runs until
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

                    socket = CreateSendSocket(localIp, useMulticast, targetIp);
                    if (token.IsCancellationRequested)
                    {
                        CloseQuietly(socket);
                        return;
                    }

                    _socket = socket;
                    var sendEndPoint = new IPEndPoint(targetIp, port);
                    _isSending = true;
                    _consecutiveErrors = 0;
                    SetStatus($"Sending to {targetIp}:{port}", IStatusProvider.StatusLevel.Success);

                    await SendLoopAsync(socket, sendEndPoint, token);
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

                    if (token.IsCancellationRequested)
                        return;

                    SetStatus($"Socket error: {e.Message} - retrying...", IStatusProvider.StatusLevel.Warning);
                    if (attempt == 1)
                        Log.Warning($"PONK stream: socket setup failed - {e.Message}");
                }

                try { await Task.Delay(ConnectRetryDelayMs, token); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static Socket CreateSendSocket(IPAddress? localIp, bool useMulticast, IPAddress targetIp)
        {
            // Using a per-sender ephemeral port keeps multiple TiXL instances (or the stream
            // running alongside MadMapper) from colliding.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                             {
                                 Blocking = false,
                                 EnableBroadcast = true,
                             };

            // Allow address reuse so receivers running on the same machine aren't blocked
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Bump send buffer - laser frames can be large bursts
            try { socket.SendBufferSize = 256 * 1024; }
            catch { }

            // Bind to the chosen local interface (or Any) on an ephemeral port. Bind is required
            // so multicast packets leave on the right interface.
            var bindAddr = localIp ?? IPAddress.Any;
            socket.Bind(new IPEndPoint(bindAddr, 0));

            if (useMulticast)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                                           bindAddr.GetAddressBytes());
                }
                catch { /* IPAny may reject; multicast group routing is OS-defined */ }

                // Default multicast TTL is 1; bump so it crosses subnets like MadMapper does
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32); }
                catch { }

                // Some NICs complain if we don't join the group we send to, so opt-in (best effort)
                try
                {
                    var mreq = new MulticastOption(targetIp, bindAddr);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mreq);
                }
                catch { /* not all platforms support AddMembership on a send socket */ }
            }

            return socket;
        }

        private static void CloseQuietly(Socket? s)
        {
            if (s == null)
                return;
            try { s.Close(); }
            catch { }
        }

        private async Task SendLoopAsync(Socket socket, IPEndPoint target, CancellationToken token)
        {
            var headerBuffer = new byte[HeaderSize];
            var payload = new MemoryStream(capacity: MaxDatagramSize);
            var writer = new BinaryWriter(payload);

            while (!token.IsCancellationRequested && !_disposed)
            {
                // Master off: settled blanking. The blanked copy of the last frame must keep
                // being SENT - receivers render whatever arrived last, so going silent leaves
                // the last lit frame up until their own timeout blanks it. The pick below
                // ignores queued frames while the master is off and zeroes the geometry.
                var masterOff = !LaserMasterEnable.Enabled;
                if (masterOff)
                {
                    _pointQueue.Clear();
                    if (_lastFrame is { Length: > 0 } && !_masterBlankApplied)
                    {
                        _lastFrame = BlankFrameCopy(_lastFrame);
                        _masterBlankApplied = true;
                    }
                }
                else
                {
                    _masterBlankApplied = false;
                }

                LaserPoint[]? frame = null;
                if (!masterOff && _pointQueue.TryDequeue(out var points) && points.Length > 0)
                {
                    frame = points;
                }
                else if (_lastFrame is { Length: > 0 })
                {
                    if (masterOff)
                    {
                        frame = _lastFrame;
                    }
                    else
                    {
                        // Frame watchdog: a stalled driver must not leave the last lit frame frozen
                        // on the receiver. Once the grace period expires, keep looping the geometry
                        // but with the beam blanked.
                        var watchdogBlank = WatchdogElapsed(_lastEnqueueTimestamp);
                        if (watchdogBlank && !_watchdogBlanked)
                        {
                            _watchdogBlanked = true;
                            Log.Warning("PONK stream: frame watchdog - no new frames, blanking output");
                            SetStatus("Frame watchdog - no new frames, output blanked", IStatusProvider.StatusLevel.Warning);
                        }

                        frame = watchdogBlank ? BlankFrameCopy(_lastFrame) : _lastFrame;
                    }
                }

                if (frame == null)
                {
                    try { await Task.Delay(1, token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                var frameData = EncodeFrame(frame);
                var crc = ComputeChecksum(frameData);
                var chunkCount = ComputeChunkCount(frameData.Length);

                try
                {
                    SendFrame(socket, target, headerBuffer, frameData, crc, chunkCount, token);
                    _consecutiveErrors = 0;
                    RememberFrame(frame);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested)
                        return;

                    _consecutiveErrors++;
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
                               int chunkCount, CancellationToken token)
        {
            // PONK chunks are header-prefixed; each chunk's payload is a slice of the frame data.
            // All chunks of a frame carry the same CRC so receivers can drop incomplete frames.
            var written = 0;
            for (var chunkNumber = 0; chunkNumber < chunkCount; chunkNumber++)
            {
                var payloadSize = Math.Min(MaxChunkPayload, frameData.Length - written);
                var totalSize = HeaderSize + payloadSize;
                if (headerBuffer.Length < totalSize)
                    Array.Resize(ref headerBuffer, totalSize);

                WriteHeader(headerBuffer, _senderName, _senderId, _frameNumber, (byte)chunkCount, (byte)chunkNumber, crc);
                Array.Copy(frameData, written, headerBuffer, HeaderSize, payloadSize);

                var sent = 0;
                while (sent < totalSize)
                {
                    try
                    {
                        sent += socket.SendTo(headerBuffer, sent, totalSize - sent, SocketFlags.None, target);
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.WouldBlock
                                                     || se.SocketErrorCode == SocketError.TryAgain
                                                     || se.SocketErrorCode == SocketError.IOPending
                                                     || se.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                    {
                        if (token.IsCancellationRequested)
                            throw new OperationCanceledException(token);

                        Thread.Sleep(1); // brief yield; the next SendTo resumes
                    }
                }

                written += payloadSize;
            }

            // Frame number wraps mod 256
            unchecked { _frameNumber++; }
        }

        private byte[] EncodeFrame(LaserPoint[] points)
        {
            using var payload = new MemoryStream(MaxDatagramSize);
            using var writer = new BinaryWriter(payload);

            // One path per frame: PATHNUMB identifies the shape across frames so receivers can
            // dispatch; MAXSPEED tells the receiver the requested scan-speed multiplier.
            writer.Write(DataFormatXyF32RgbU8);
            writer.Write((byte)2);
            WriteMetaEntry(writer, "PATHNUMB", 1);
            WriteMetaEntry(writer, "MAXSPEED", _maxScanSpeed);

            // pointCount is a ushort and chunkCount a byte, so cap the frame at a point count
            // whose chunking stays inside both limits.
            var count = Math.Min(points.Length, MaxPointsPerFrame);
            writer.Write((ushort)count);

            for (var i = 0; i < count; i++)
            {
                var p = points[i];
                // LaserPoint.X/Y are ILDA-style 16-bit signed (-32768..32767); normalize to
                // PONK's mandatory float32 [-1, 1] XY + uint8 RGB format. Colors are scaled by
                // the brightness ceiling right before encoding - the final output audit.
                var x = Math.Clamp(p.X / 32767f, -1f, 1f);
                var y = Math.Clamp(p.Y / 32767f, -1f, 1f);
                var r = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.B : p.R, 0) * _maxBrightness) >> 8, 0, 255);
                var g = (byte)Math.Clamp((int)MathF.Round(Math.Max(p.G, 0) * _maxBrightness) >> 8, 0, 255);
                var b = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.R : p.B, 0) * _maxBrightness) >> 8, 0, 255);

                writer.Write(x);
                writer.Write(y);
                writer.Write(r);
                writer.Write(g);
                writer.Write(b);
            }

            return payload.ToArray();
        }

        private static void WriteHeader(byte[] buffer, string senderName, int senderId, byte frameNumber, byte chunkCount, byte chunkNumber, uint crc)
        {
            // All multi-byte fields are little-endian per the PONK reference implementation:
            //  0-7 "PONK-UDP", 8 version, 9-12 sender id LE, 13-44 name (32B null-padded ASCII),
            //  45 frameNumber, 46 chunkCount, 47 chunkNumber, 48-51 dataCrc LE.
            var nameBytes = Encoding.ASCII.GetBytes(HeaderString);
            for (var i = 0; i < 8; i++)
                buffer[i] = i < nameBytes.Length ? nameBytes[i] : (byte)' ';

            buffer[8] = ProtocolVersion;

            buffer[9] = (byte)(senderId & 0xFF);
            buffer[10] = (byte)((senderId >> 8) & 0xFF);
            buffer[11] = (byte)((senderId >> 16) & 0xFF);
            buffer[12] = (byte)((senderId >> 24) & 0xFF);

            var senderBytes = Encoding.ASCII.GetBytes(senderName);
            for (var i = 0; i < MaxSenderNameBytes; i++)
                buffer[13 + i] = i < senderBytes.Length ? senderBytes[i] : (byte)0;

            buffer[45] = frameNumber;
            buffer[46] = chunkCount;
            buffer[47] = chunkNumber;

            buffer[48] = (byte)(crc & 0xFF);
            buffer[49] = (byte)((crc >> 8) & 0xFF);
            buffer[50] = (byte)((crc >> 16) & 0xFF);
            buffer[51] = (byte)((crc >> 24) & 0xFF);
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
            if (frameDataLength <= 0)
                return 0;
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
            for (var i = 0; i < data.Length; i++)
                sum += data[i];
            return sum;
        }

        private void RememberFrame(LaserPoint[] points)
        {
            if (_lastFrame == null || _lastFrame.Length != points.Length)
                _lastFrame = new LaserPoint[points.Length];
            Array.Copy(points, _lastFrame, points.Length);
        }

        /// <summary>Returns a fresh blanked copy (positions kept, colors zeroed) — the watchdog
        /// loop aliases the last frame, so a shared scratch buffer would tear it.</summary>
        private static LaserPoint[] BlankFrameCopy(LaserPoint[] source)
        {
            var copy = new LaserPoint[source.Length];
            LaserSafetyTransforms.BlankCopyInto(source, copy);
            return copy;
        }

        public void Dispose()
        {
            _disposed = true;
            TearDown();
        }

        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        private string _statusMessage = "Disconnected";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;
        private int _consecutiveErrors;
        private long _lastEnqueueTimestamp;
        private volatile bool _watchdogBlanked;
        private bool _masterBlankApplied;
    }
    #endregion

    #region CITP stream - Capture/Depence laser feed over CITP/CAEX (lifted from the former [CITPLaser] operator)

    /// <summary>
    /// Feeds a CITP laser consumer (e.g. Depence/Capture show previz): a TCP control connection
    /// (PINF handshake, LaserFeedList, receiver-driven feed start/stop and fps) plus CAEX
    /// LaserFeedFrame datagrams to the 224.0.0.180:4809 multicast group. The receiver is found by
    /// PLoc multicast discovery unless host and port are both set on the plug (direct connect).
    /// Points travel as 12-bit unsigned XY + RGB565; the conversion from ILDA 16-bit applies the
    /// plug's brightness ceiling and channel order as the final audit, like every transport here.
    /// </summary>
    private sealed class CitpStream : IStream
    {
        private const uint CitpCookie = 0x43495450; // "CITP"
        private const uint PinFCookie = 0x50494E46; // "PINF"
        private const uint PLocCookie = 0x504C6F63; // "PLoc"
        private const uint PnamCookie = 0x504E616D; // "PNam"
        private const uint PtypCookie = 0x50547970; // "PTyp"
        private const uint CaexCookie = 0x43414558; // "CAEX"

        private enum CaexContentType : uint
        {
            GetLaserFeedList = 0x00030100,
            LaserFeedList = 0x00030101,
            LaserFeedControl = 0x00030102,
            LaserFeedFrame = 0x00030200,
            EnterShow = 0x00020100,
        }

        // Verified working with Capture: 224.0.0.180:4809
        private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.180");
        private const int MulticastPort = 4809;
        private const int DefaultCapturePort = 51245;

        /// <summary>A LaserFeedFrame travels as a single UDP datagram, capped to stay inside the IPv4
        /// datagram limit (~65,507 bytes ≈ 13,000 points) — a larger feed would fail every send with
        /// WSAEMSGSIZE and overflow the ushort point count.</summary>
        private const int MaxPointsPerDatagram = 12000;

        private const int MaxQueuedFrames = 5;

        public CitpStream(LaserPlug plug)
        {
            var device = plug.CitpSettings;
            _feedName = string.IsNullOrWhiteSpace(device.FeedName) ? "Laser Feed" : device.FeedName.Trim();
            _sourceKey = device.SourceKey != 0 ? device.SourceKey : (uint)Random.Shared.Next();
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _directHost = plug.Address ?? string.Empty;
            _directPort = plug.Port > 0 ? plug.Port : 0;
            _localAddress = plug.LocalAddress ?? string.Empty;

            StartConnection();
        }

        private readonly string _feedName;
        private readonly uint _sourceKey;
        private readonly float _maxBrightness;
        private readonly bool _swapColorChannels;
        private readonly string _directHost;
        private readonly int _directPort;
        private readonly string _localAddress;

        private readonly object _connectionLock = new();
        private readonly ConcurrentQueue<LaserPoint[]> _frameQueue = new();

        private CancellationTokenSource? _cts;
        private UdpClient? _multicastListener;
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private int _connectInProgress;

        private volatile bool _disposed;
        private volatile bool _feedActive;
        private volatile int _requiredFps;
        private uint _frameSequenceNo;
        private int _captureTcpPort; // staged before StartConnection; 0 = PLoc discovery
        private string _captureIp = string.Empty;

        private string _statusMessage = "Disconnected";
        private IStatusProvider.StatusLevel _statusLevel = IStatusProvider.StatusLevel.Notice;

        public bool IsConnected
        {
            get
            {
                lock (_connectionLock)
                    return _tcpClient is { Connected: true };
            }
        }

        public IStatusProvider.StatusLevel StatusLevel => _statusLevel;
        public string StatusMessage => _statusMessage;

        public void SubmitFrame(LaserPoint[] frame)
        {
            if (_disposed || !IsConnected || !_feedActive)
                return;

            var copy = CopyForQueue(frame, _frameQueue, MaxQueuedFrames);
            if (copy == null)
                return;

            _frameQueue.Enqueue(copy);
        }

        private void SetStatus(string message, IStatusProvider.StatusLevel level)
        {
            _statusMessage = message;
            _statusLevel = level;
        }

        private void StartConnection()
        {
            if (_disposed)
                return;

            var localIp = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(_localAddress) && IPAddress.TryParse(_localAddress, out var parsed))
                localIp = parsed;

            // Host AND port on the plug = direct connect; anything else keeps PLoc discovery.
            _captureIp = _directHost;
            _captureTcpPort = string.IsNullOrWhiteSpace(_directHost) || _directPort <= 0 ? 0 : _directPort;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _feedActive = false;
            _requiredFps = 0;

            if (_captureTcpPort > 0)
            {
                SetStatus($"Connecting to {_captureIp}:{_captureTcpPort}", IStatusProvider.StatusLevel.Notice);
                // TcpClient.Connect blocks for seconds when unreachable - never on the caller's thread.
                _ = Task.Run(ConnectToCapture);
            }
            else
            {
                // Join the CITP discovery multicast group. Bind 0.0.0.0: membership in the group
                // (joined per interface below) is what filters by network, not the bind address.
                try
                {
                    _multicastListener = new UdpClient(AddressFamily.InterNetwork);
                    _multicastListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _multicastListener.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                    try
                    {
                        _multicastListener.JoinMulticastGroup(MulticastAddress, localIp);
                    }
                    catch (SocketException)
                    {
                        // Some adapters don't support joining with a specific interface
                        _multicastListener.JoinMulticastGroup(MulticastAddress);
                    }

                    _multicastListener.Client.ReceiveTimeout = 5000;
                    SetStatus($"Listening for Capture on {MulticastAddress}:{MulticastPort}", IStatusProvider.StatusLevel.Notice);

                    var token = cts.Token;
                    var listener = _multicastListener;
                    var thread = new Thread(() => UdpReceiveLoop(token, listener))
                                     {
                                         IsBackground = true,
                                         Name = "CITP_UDP_Discovery",
                                     };
                    thread.Start();
                }
                catch (Exception ex)
                {
                    SetStatus($"CITP discovery failed to start: {ex.Message}", IStatusProvider.StatusLevel.Error);
                }
            }
        }

        #region Discovery (PINF/PLoc multicast)
        private void UdpReceiveLoop(CancellationToken token, UdpClient listener)
        {
            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var bytes = listener.Receive(ref remoteEndPoint);
                    if (token.IsCancellationRequested || IsConnected)
                        continue;

                    if (bytes.Length < 16)
                        continue;

                    if (ReadBigEndianUint(bytes, 0) != CitpCookie)
                        continue;

                    var remoteIp = remoteEndPoint.Address;
                    if (remoteIp.IsIPv4MappedToIPv6)
                        remoteIp = remoteIp.MapToIPv4();

                    // With a local interface selected, prefer discovery packets from its subnet.
                    if (!IPAddress.TryParse(_localAddress, out var selectedLocal) || IPAddress.IsLoopback(remoteIp) ||
                        selectedLocal.GetAddressBytes().Length != 4 || remoteIp.GetAddressBytes().Length != 4 ||
                        selectedLocal.GetAddressBytes()[0] == remoteIp.GetAddressBytes()[0])
                    {
                        int pInfOffset = -1;
                        int pLocOffset = -1;
                        for (var i = 12; i <= Math.Min(bytes.Length - 4, 24); i += 4)
                        {
                            var val = ReadBigEndianUint(bytes, i);
                            if (val == PinFCookie)
                                pInfOffset = i;
                            if (val == PLocCookie)
                                pLocOffset = i;
                        }

                        if (pInfOffset != -1)
                        {
                            for (var i = pInfOffset + 4; i <= bytes.Length - 8; i++)
                            {
                                if (ReadBigEndianUint(bytes, i) == PLocCookie)
                                {
                                    TryConnect(remoteIp, i, bytes);
                                    break;
                                }
                            }
                        }
                        else if (pLocOffset != -1)
                        {
                            TryConnect(remoteIp, pLocOffset, bytes);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        // No PLoc within the timeout: fall back to a direct localhost Capture.
                        if (!IsConnected && !token.IsCancellationRequested && _captureTcpPort == 0)
                        {
                            _captureIp = "127.0.0.1";
                            _captureTcpPort = DefaultCapturePort;
                            ConnectToCapture();
                        }

                        continue;
                    }

                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                        Thread.Sleep(100);
                }
            }
        }

        private void TryConnect(IPAddress address, int cookieIndex, byte[] buffer)
        {
            if (cookieIndex + 5 >= buffer.Length)
                return;

            // Little-endian per CITP spec
            var tcpPort = (ushort)(buffer[cookieIndex + 4] | (buffer[cookieIndex + 5] << 8));
            if (tcpPort == 0)
                return;

            lock (_connectionLock)
            {
                if (_tcpClient is { Connected: true })
                    return;

                _captureIp = address.ToString();
                _captureTcpPort = tcpPort;
            }

            ConnectToCapture();
        }
        #endregion

        #region TCP control connection
        private void ConnectToCapture()
        {
            string targetIp;
            int targetPort;
            lock (_connectionLock)
            {
                targetIp = _captureIp;
                targetPort = _captureTcpPort;
            }

            if (string.IsNullOrEmpty(targetIp) || targetPort == 0)
                return;
            if (!IPAddress.TryParse(targetIp, out var ip))
                return;

            // Discovery and reconnects can reach this at the same moment - only one
            // attempt may run, or both teardown+reconnect interleave.
            if (Interlocked.Exchange(ref _connectInProgress, 1) == 1)
                return;

            try
            {
                ConnectToCaptureInner(ip, targetPort);
            }
            finally
            {
                Interlocked.Exchange(ref _connectInProgress, 0);
            }
        }

        private void ConnectToCaptureInner(IPAddress ip, int targetPort)
        {
            try
            {
                StopTcpConnection();

                var tcpClient = new TcpClient();
                tcpClient.Connect(ip, targetPort);
                var tcpStream = tcpClient.GetStream();

                lock (_connectionLock)
                {
                    if (_cts == null || _cts.IsCancellationRequested)
                    {
                        tcpStream.Close();
                        tcpClient.Close();
                        return;
                    }

                    _tcpClient = tcpClient;
                    _tcpStream = tcpStream;

                    SetStatus($"Connected to Capture at {ip}:{targetPort}", IStatusProvider.StatusLevel.Success);

                    var token = _cts.Token;
                    var client = tcpClient;
                    var stream = tcpStream;
                    new Thread(() => TcpReceiveLoop(token, client, stream))
                        {
                            IsBackground = true,
                            Name = "CITP_TCP_Receiver",
                        }
                        .Start();
                    new Thread(SendLoop)
                        {
                            IsBackground = true,
                            Name = "CITP_UDP_Sender",
                        }
                        .Start();
                }

                // Handshake first
                SendPeerName();
                SendPeerType();
                SendLaserFeedList();
            }
            catch (Exception ex)
            {
                SetStatus($"Discovery: {ip}:{targetPort} refused connection ({ex.Message})", IStatusProvider.StatusLevel.Notice);
                StopTcpConnection();
            }
        }

        private void StopTcpConnection()
        {
            // Join-free teardown: the loops hold local references and exit the moment their
            // socket is closed / token cancelled; joining here could stall for seconds on a
            // wedged socket.
            lock (_connectionLock)
            {
                try { _tcpStream?.Close(); } catch { /* ignore */ }
                _tcpStream = null;

                try { _tcpClient?.Close(); } catch { /* ignore */ }
                _tcpClient = null;

                _feedActive = false;
                _requiredFps = 0;
                _frameQueue.Clear();

                if (_cts is { IsCancellationRequested: false })
                    SetStatus($"Listening for Capture on {MulticastAddress}:{MulticastPort}", IStatusProvider.StatusLevel.Notice);
            }
        }

        private void TcpReceiveLoop(CancellationToken token, TcpClient client, NetworkStream stream)
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    var bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        StopTcpConnection();
                        break;
                    }

                    if (bytesRead < 24)
                        continue; // 20-byte CITP header + 4-byte layer content type minimum

                    if (ReadBigEndianUint(buffer, 0) != CitpCookie)
                        continue;

                    var contentType = ReadBigEndianUint(buffer, 16); // offset 16 in the 20-byte CITP header
                    if (contentType != CaexCookie)
                        continue;

                    var caexType = ReadLittleEndianUint(buffer, 20); // CAEX ContentCode at offset 20
                    switch ((CaexContentType)caexType)
                    {
                        case CaexContentType.GetLaserFeedList:
                            SendLaserFeedList();
                            break;
                        case CaexContentType.LaserFeedControl:
                            HandleLaserFeedControl(buffer, bytesRead);
                            break;
                        case CaexContentType.EnterShow:
                            SendEnterShow();
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    StopTcpConnection();
                    break;
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                        StopTcpConnection();
                    break;
                }
            }
        }
        #endregion

        #region CAEX message handlers
        private void SendEnterShow()
        {
            NetworkStream? stream;
            lock (_connectionLock) stream = _tcpStream;
            if (stream == null)
                return;

            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                WriteCitpHeader(writer, CaexCookie);
                writer.Write((uint)CaexContentType.EnterShow);

                // Show name, UCS-2 LE, null-terminated
                writer.Write(Encoding.Unicode.GetBytes("TiXL\0"));

                SendTcpMessage(stream, ms);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        private void SendPeerName()
        {
            NetworkStream? stream;
            lock (_connectionLock) stream = _tcpStream;
            if (stream == null)
                return;

            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                WriteCitpHeader(writer, PinFCookie);
                WriteCookieBytes(writer, PnamCookie);
                writer.Write(Encoding.ASCII.GetBytes("TiXL CITP Laser\0"));

                SendTcpMessage(stream, ms);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        private void SendPeerType()
        {
            NetworkStream? stream;
            lock (_connectionLock) stream = _tcpStream;
            if (stream == null)
                return;

            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                WriteCitpHeader(writer, PinFCookie);
                WriteCookieBytes(writer, PtypCookie);
                writer.Write(Encoding.ASCII.GetBytes("Laser\0"));

                SendTcpMessage(stream, ms);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        private void SendLaserFeedList()
        {
            NetworkStream? stream;
            lock (_connectionLock) stream = _tcpStream;
            if (stream == null)
                return;

            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                // CAEX header: 20 (CITP) + 4 (ContentCode) = 24 bytes
                WriteCitpHeader(writer, CaexCookie);
                writer.Write((uint)CaexContentType.LaserFeedList);

                // LaserFeedList body: source key + feed count (1 byte) + feed names (UCS-2 LE)
                writer.Write(_sourceKey);
                writer.Write((byte)1);

                writer.Write(Encoding.Unicode.GetBytes($"{_feedName} 0\0"));

                SendTcpMessage(stream, ms);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        /// <summary>The capture tells us to start/stop sending frames: FeedIndex (1) + FrameRate (1)
        /// after the 24-byte CAEX header. FrameRate 0 stops the feed.</summary>
        private void HandleLaserFeedControl(byte[] buffer, int length)
        {
            if (length < 26)
                return;

            var feedIndex = buffer[24];
            var frameRate = buffer[25];

            _requiredFps = frameRate;
            _feedActive = frameRate != 0;

            SetStatus($"Feed {feedIndex} {(frameRate != 0 ? $"active at {frameRate} fps" : "stopped")}",
                      IStatusProvider.StatusLevel.Success);
        }

        private static void SendTcpMessage(NetworkStream stream, MemoryStream ms)
        {
            var data = ms.ToArray();
            var length = (uint)data.Length;
            // Patch MessageSize into the CITP header (bytes 8-11, little-endian)
            data[8] = (byte)length;
            data[9] = (byte)(length >> 8);
            data[10] = (byte)(length >> 16);
            data[11] = (byte)(length >> 24);

            // The TCP receive thread and any handshake triggered on connect share this stream.
            lock (stream)
                stream.Write(data, 0, data.Length);
        }
        #endregion

        #region Frame sending
        private void SendLoop()
        {
            var token = _cts?.Token ?? default;
            using var udpSender = new UdpClient();
            var target = new IPEndPoint(MulticastAddress, MulticastPort);
            LaserPoint[]? lastFrame = null;
            var blankScratch = Array.Empty<LaserPoint>();

            while (!token.IsCancellationRequested && !_disposed)
            {
                // Master off: settled blanking - keep sending a zero-color copy of the last
                // frame so the beam is off while the geometry keeps moving; UDP is lossy, so
                // a one-off blank would be lost and the receiver keeps its last lit frame.
                if (!LaserMasterEnable.Enabled)
                {
                    _frameQueue.Clear();
                    if (_feedActive && lastFrame is { Length: > 0 })
                    {
                        var blanked = LaserOutputStream.BlankFrame(lastFrame, ref blankScratch);
                        try
                        {
                            SendLaserFeedFrame(udpSender, blanked, target);
                        }
                        catch { /* transient socket errors retry on the next tick */ }
                    }

                    Thread.Sleep(15);
                    continue;
                }

                if (_feedActive && _frameQueue.TryDequeue(out var frame))
                {
                    lastFrame = frame;
                    try
                    {
                        SendLaserFeedFrame(udpSender, frame, target);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"CITP frame send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }

        private void SendLaserFeedFrame(UdpClient udpSender, LaserPoint[] points, IPEndPoint target)
        {
            if (points.Length == 0)
                return;

            var pointCount = Math.Min(points.Length, MaxPointsPerDatagram);

            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                // CAEX header: 20 (CITP) + 4 (ContentCode) = 24 bytes
                WriteCitpHeader(writer, CaexCookie);
                writer.Write((uint)CaexContentType.LaserFeedFrame);

                // LaserFeedFrame body: sourceKey + feedIndex + frameSequence + pointCount
                writer.Write(_sourceKey);
                writer.Write((byte)0);
                writer.Write(_frameSequenceNo++);
                writer.Write((ushort)pointCount);

                for (var i = 0; i < pointCount; i++)
                {
                    var cp = ConvertLaserPoint(points[i]);
                    writer.Write(cp.XLowByte);
                    writer.Write(cp.YLowByte);
                    writer.Write(cp.XyHighNibbles);
                    writer.Write(cp.Color);
                }

                var data = ms.ToArray();
                var length = (uint)data.Length;
                data[8] = (byte)length;
                data[9] = (byte)(length >> 8);
                data[10] = (byte)(length >> 16);
                data[11] = (byte)(length >> 24);

                udpSender.Send(data, data.Length, target);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP frame send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        private struct CitpLaserPoint
        {
            public byte XLowByte;
            public byte YLowByte;
            public byte XyHighNibbles;
            public ushort Color;
        }

        /// <summary>Converts a TiXL LaserPoint (ILDA-style 16-bit signed X/Y, 16-bit R/G/B) to a
        /// CITP/CAEX laser point: 12-bit unsigned X/Y in 0..4095 and RGB565 color. The brightness
        /// ceiling and channel order from the plug are applied here as the final audit.</summary>
        private CitpLaserPoint ConvertLaserPoint(LaserPoint p)
        {
            // ILDA 16-bit signed -> [-1, 1] -> 12-bit unsigned (0..4095)
            var xNorm = Math.Clamp(p.X / 32767f, -1f, 1f);
            var yNorm = Math.Clamp(p.Y / 32767f, -1f, 1f);
            var x = Math.Clamp((int)((xNorm + 1f) * 0.5f * 4095f), 0, 4095);
            var y = Math.Clamp((int)((yNorm + 1f) * 0.5f * 4095f), 0, 4095);

            // 16-bit intensities, ceiling applied, shifted to 8-bit for RGB565 packing.
            var r = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.B : p.R, 0) * _maxBrightness) >> 8, 0, 255);
            var g = (byte)Math.Clamp((int)MathF.Round(Math.Max(p.G, 0) * _maxBrightness) >> 8, 0, 255);
            var b = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.R : p.B, 0) * _maxBrightness) >> 8, 0, 255);

            // RGB565: R[15:11] = top 5 of R, G[10:5] = top 6 of G, B[4:0] = top 5 of B
            var color = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 5) | ((b & 0xF8) >> 3));

            return new CitpLaserPoint
                       {
                           XLowByte = (byte)(x & 0xFF),
                           YLowByte = (byte)(y & 0xFF),
                           XyHighNibbles = (byte)(((x >> 8) & 0x0F) | (((y >> 8) & 0x0F) << 4)),
                           Color = color,
                       };
        }
        #endregion

        #region Helpers - CITP is little-endian ("All fields use little endian byte order" per spec); cookies are ASCII
        private static void WriteCitpHeader(BinaryWriter writer, uint contentType)
        {
            // CITP header: 20 bytes total
            WriteCookieBytes(writer, CitpCookie);   // bytes 0-3: "CITP"
            writer.Write((byte)1);                  // byte 4: VersionMajor
            writer.Write((byte)0);                  // byte 5: VersionMinor
            writer.Write((ushort)0);                // bytes 6-7: RequestIndex
            writer.Write((uint)0);                  // bytes 8-11: MessageSize placeholder
            writer.Write((ushort)1);                // bytes 12-13: MessagePartCount
            writer.Write((ushort)0);                // bytes 14-15: MessagePart
            WriteCookieBytes(writer, contentType);  // bytes 16-19: ContentType
        }

        private static void WriteCookieBytes(BinaryWriter writer, uint cookie)
        {
            writer.Write((byte)((cookie >> 24) & 0xFF));
            writer.Write((byte)((cookie >> 16) & 0xFF));
            writer.Write((byte)((cookie >> 8) & 0xFF));
            writer.Write((byte)(cookie & 0xFF));
        }

        private static uint ReadLittleEndianUint(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | ((uint)buffer[offset + 1] << 8)
                   | ((uint)buffer[offset + 2] << 16)
                   | ((uint)buffer[offset + 3] << 24);
        }

        private static uint ReadBigEndianUint(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                   | ((uint)buffer[offset + 1] << 16)
                   | ((uint)buffer[offset + 2] << 8)
                   | buffer[offset + 3];
        }
        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            var cts = _cts;
            cts?.Cancel();

            lock (_connectionLock)
            {
                try { _multicastListener?.Close(); } catch { /* ignore */ }
                _multicastListener = null;

                try { _tcpStream?.Close(); } catch { /* ignore */ }
                _tcpStream = null;

                try { _tcpClient?.Close(); } catch { /* ignore */ }
                _tcpClient = null;

                _frameQueue.Clear();
            }

            SetStatus("Disconnected", IStatusProvider.StatusLevel.Notice);
        }
    }
    #endregion
}
