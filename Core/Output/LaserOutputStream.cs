#nullable enable
using System;
using System.Collections.Concurrent;
using System.Buffers.Binary;
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
    /// the caller's buffer; call from one thread only (the render thread). Callers whose pipeline
    /// already ran the same config this frame pass <paramref name="alreadySafetyApplied"/> to skip
    /// the identical second pass (and its two array copies per frame).
    /// </summary>
    public void SubmitFrame(in LaserFrame frame, in LaserSafetyTransforms.Config safety, bool alreadySafetyApplied = false)
    {
        var count = frame.Count;
        if (count <= 0 || frame.Points == null)
            return;

        if (alreadySafetyApplied)
        {
            _inner.SubmitFrame(frame);
            return;
        }

        // The safety transforms are idempotent (clamps, subdivision, dwell blanking), so
        // re-applying the stage the setup pipeline already ran is safe and never inflates
        // the point count.
        _safetyWorkList.Clear();
        for (var i = 0; i < count; i++)
            _safetyWorkList.Add(frame.Points[i]);

        var result = LaserSafetyTransforms.Apply(_safetyWorkList, in safety, ref _safetyStats);
        var exact = result.ToArray();
        _inner.SubmitFrame(new LaserFrame(exact, exact.Length));
    }

    public bool IsConnected => _inner.IsConnected;

    public IStatusProvider.StatusLevel StatusLevel => _inner.StatusLevel;
    public string StatusMessage => _inner.StatusMessage;

    /// <summary>
    /// True while the stream still runs with the values it was built from. Device settings are
    /// applied when a stream is created (scan rate, brightness ceiling, channel order, keepalive,
    /// address), so the driver rebuilds the stream when they no longer match - otherwise editing
    /// them on the plug card would only take effect after a restart.
    /// </summary>
    public bool MatchesPlug(LaserPlug plug) => _inner.DescribeMismatch(plug) == null;

    /// <summary>Applies the plug settings that a running stream takes without a reconnect (brightness
    /// ceiling, channel order, scan rate, keepalive). Call it each pump, before <see cref="MatchesPlug"/>.</summary>
    public void ApplyLiveSettings(LaserPlug plug) => _inner.ApplyLiveSettings(plug);

    /// <summary>The plug setting that no longer matches this stream, or null when it still does.
    /// A persistent mismatch makes the driver rebuild the stream repeatedly - the reason a plug
    /// card can read "connected, then instantly disconnected" over and over.</summary>
    public string? DescribeMismatch(LaserPlug plug) => _inner.DescribeMismatch(plug);

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
        void SubmitFrame(in LaserFrame frame);
        bool IsConnected { get; }
        IStatusProvider.StatusLevel StatusLevel { get; }
        string StatusMessage { get; }

        /// <summary>The plug setting that needs a new connection, or null when the live stream still serves the plug.</summary>
        string? DescribeMismatch(LaserPlug plug);

        /// <summary>Applies the plug settings the running stream reads per chunk; transports without any do nothing.</summary>
        void ApplyLiveSettings(LaserPlug plug)
        {
        }
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
    internal static bool WatchdogElapsed(ref long lastSubmitTimestamp)
    {
        var last = Interlocked.Read(ref lastSubmitTimestamp);
        if (last == 0)
            return false;
        return Environment.TickCount64 - last > (long)(WatchdogTimeoutSec * 1000f);
    }

    /// <summary>Leads the frame with a dark ramp from the scanners' current position to the
    /// frame's first point: they travel from wherever they are parked (or wherever the previous
    /// frame ended) with the beam off, in bounded steps instead of one maximal-acceleration
    /// jump. A fresh frame after (re)begin starts from the park position (0,0); while looping,
    /// it starts from the previous frame's exit so consecutive passes chain smoothly.</summary>
    internal static LaserPoint[] PrependDarkApproach(LaserPoint[] frame, int fromX, int fromY, ref LaserPoint[] scratch)
    {
        const int approachPoints = 6;
        var total = frame.Length + approachPoints;
        if (scratch.Length != total)
            scratch = new LaserPoint[total];

        var first = frame[0];
        var targetX = first.X;
        var targetY = first.Y;

        // The ramp fills all but the last sample; the final sample sits on the target so the
        // scanners are settled there before the first lit point arrives.
        for (var i = 0; i < approachPoints; i++)
        {
            var t = (i + 1) / (float)approachPoints;
            var x = (int)MathF.Round(fromX + (targetX - fromX) * t);
            var y = (int)MathF.Round(fromY + (targetY - fromY) * t);
            scratch[i] = LaserPoint.CreateBlanked(x, y);
        }

        Array.Copy(frame, 0, scratch, approachPoints, frame.Length);
        return scratch;
    }

    /// <summary>
    /// Builds one data-command payload for a pull-model DAC: the unsent tail of the current frame,
    /// then - when the frame is small next to the DAC's free room - whole repeats of it (each behind
    /// its own dark approach from the previous end point, same as a fresh loop pass). A small frame
    /// (a 180-point aim rectangle is 6 ms of playback at 30 kpps) otherwise empties the DAC buffer
    /// faster than the sender's wake-up cadence can refill it (a Windows timer tick is ~15 ms), and the
    /// DAC drops to idle and has to be re-prepared over and over. Repeats are added whole, never cut
    /// mid-frame, so the stream always ends on the frame's last point and the next pass continues
    /// seamlessly. <paramref name="frameAfterApproach"/> is what the tail was cut from.
    /// </summary>
    internal static LaserPoint[] BuildTopUpChunk(LaserPoint[] frameAfterApproach, int offset, LaserPoint[] rawFrame, int room,
                                                 ref LaserPoint[] scratch, out int consumedFromFrame)
    {
        var remaining = frameAfterApproach.Length - offset;
        consumedFromFrame = Math.Min(room, remaining);

        if (remaining >= room || rawFrame.Length == 0)
        {
            var plain = new LaserPoint[consumedFromFrame];
            Array.Copy(frameAfterApproach, offset, plain, 0, consumedFromFrame);
            return plain;
        }

        var chunk = new LaserPoint[room];
        Array.Copy(frameAfterApproach, offset, chunk, 0, remaining);
        var count = remaining;

        while (true)
        {
            var last = chunk[count - 1];
            var copyLength = rawFrame.Length + 6; // PrependDarkApproach adds a 6-point ramp
            if (count + copyLength > room)
                break;

            var next = PrependDarkApproach(rawFrame, last.X, last.Y, ref scratch);
            Array.Copy(next, 0, chunk, count, next.Length);
            count += next.Length;
        }

        if (count == room)
            return chunk;

        var trimmed = new LaserPoint[count];
        Array.Copy(chunk, 0, trimmed, 0, count);
        return trimmed;
    }

    /// <summary>
    /// The buffer capacity to hand the device library. Its default is the original Ether Dream's
    /// 1799 points; newer hardware advertises a larger buffer in its discovery broadcast, and a
    /// DAC that is already connected stops broadcasting - so the fill level from its own status is
    /// the fallback proof that the buffer is bigger than the assumption. The library paces its
    /// sends against this number, so the stream must never use a different one.
    /// </summary>
    internal static ushort ResolveBufferCapacity(ushort advertisedCapacity, ushort observedFullness = 0)
    {
        var capacity = advertisedCapacity > 0 ? advertisedCapacity : DefaultBufferCapacity;
        return observedFullness > capacity ? observedFullness : capacity;
    }

    /// <summary>The library's own default for hardware that never advertised its capacity.</summary>
    internal const ushort DefaultBufferCapacity = 1799;

    /// <summary>Queue bookkeeping shared by both transports: bounds the queue, copies the frame
    /// out of the upstream buffer (producers reuse their allocation every frame) and resets the
    /// watchdog clock.</summary>
    internal static LaserPoint[]? CopyForQueue(in LaserFrame frame, ConcurrentQueue<LaserPoint[]> queue, int maxQueuedFrames)
    {
        var count = frame.Count;
        if (count <= 0 || frame.Points == null)
            return null;

        while (queue.Count >= maxQueuedFrames)
        {
            if (!queue.TryDequeue(out _))
                break;
        }

        // Exactly the frame, never the producer buffer's capacity: the tail past Count is
        // padding, and streaming it would spend datagram budget on blanked origin dwells.
        var copy = GC.AllocateUninitializedArray<LaserPoint>(count);
        Array.Copy(frame.Points, copy, count);
        return copy;
    }

    /// <summary>
    /// One PONK path-metadata entry: an 8-byte key followed by a little-endian float32. The key field
    /// is exactly 8 bytes with no terminator slot - the protocol's own keys (`PATHNUMB`,
    /// `MAXSPEED`) fill it completely and are matched as 8 bytes by receivers - so a shorter key is
    /// zero-padded and an 8-character key must not lose its last byte.
    /// </summary>
    internal static void WriteMetaEntry(Span<byte> destination, string key, float value)
    {
        destination[..8].Clear();
        Encoding.ASCII.GetBytes(key.AsSpan(0, Math.Min(key.Length, 8)), destination[..8]);
        BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(8, 4), value);
    }

    internal static void WriteMetaEntry(BinaryWriter writer, string key, float value)
    {
        Span<byte> entry = stackalloc byte[MetaEntrySize];
        WriteMetaEntry(entry, key, value);
        writer.Write(entry);
    }

    /** 8 byte key + float32 value. */
    internal const int MetaEntrySize = 12;
    #endregion

    #region EtherDream transport
    /// <summary>
    /// Streams to an Ether Dream DAC over TCP, lifted from the former [EtherDreamOutput] operator.
    /// The unkillable rules of the send loop carry over unchanged: StreamPoints is only called once
    /// a status poll confirmed buffer room (a full buffer blocks inside the library's socket lock
    /// forever when the engine has halted), that room is computed from the SAME capacity the
    /// library was constructed with (otherwise the two disagree and a healthy link looks stalled),
    /// and a halted engine is recovered IN PLACE (Prepare discards the stuck queue; reconnect churn
    /// is what wedges this DAC's firmware).
    /// </summary>
    private sealed class EtherDreamStream : IStream
    {
        public EtherDreamStream(LaserPlug plug)
        {
            _plug = plug;
            var device = plug.EtherDreamSettings;
            _scanRate = NormalizeScanRate(device.ScanRate);
            _plugAddress = plug.Address ?? string.Empty;
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;

            // The plug picker lists DACs while a stream runs: a connected DAC stops its
            // discovery broadcasts, so start the shared listener with the stream.
            LaserPlugDiscovery.EnsureEtherDreamDiscovery();

            StartConnection();
        }

        private readonly LaserPlug _plug;
        private readonly string _plugAddress;

        // Read per chunk by the send loop, so a settings edit applies without a reconnect.
        private volatile int _scanRate;
        private volatile float _maxBrightness;
        private volatile bool _swapColorChannels;

        // Library instances / connection state
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
        private const int PlaybackStallDelayMs = 400;  // not-playing for this long while streaming → explicit re-begin
        private const int RebeginMinIntervalMs = 1000;
        private const int PendingDisposeWaitMs = 2000; // bounded wait for a previous session's socket to close
        private const int MaxQueuedFrames = 2;
        private const int MinChunkPoints = 32;
        private const int DiscoveryPollDelayMs = 500;

        public bool IsConnected => _isConnected;
        public IStatusProvider.StatusLevel StatusLevel => _statusLevel;
        public string StatusMessage => _statusMessage;

        /// <summary>A plug left at its default scan rate (0) streams at 30 kpps.</summary>
        internal static int NormalizeScanRate(int scanRate)
        {
            var clamped = Math.Clamp(scanRate, 100, 100000);
            return clamped == 100 && scanRate < 100 ? 30000 : clamped;
        }

        /// <summary>
        /// Only the device address needs a new connection. Closing a session mid-write leaves the DAC's
        /// firmware holding a stale full buffer until it is power-cycled, so everything else is applied
        /// to the running stream here: scan rate, brightness ceiling and channel order are read per
        /// chunk, and the interface is not used by this transport at all.
        /// </summary>
        public void ApplyLiveSettings(LaserPlug plug)
        {
            var device = plug.EtherDreamSettings;
            _scanRate = NormalizeScanRate(device.ScanRate);
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
        }

        public string? DescribeMismatch(LaserPlug plug)
        {
            var address = plug.Address ?? string.Empty;
            if (address != _plugAddress && !IsAutoSelectedDevice(address))
                return $"address '{_plugAddress}' -> '{plug.Address}'";

            return null;
        }

        /// <summary>A plug left empty streams to the first discovered DAC; the setup card then writes that
        /// DAC's address into the plug, which is the connection the stream already has.</summary>
        private bool IsAutoSelectedDevice(string address)
        {
            return _plugAddress.Length == 0
                   && address.Length > 0
                   && (address == _autoSelectedIp || address == _lastConnectIp);
        }

        public void SubmitFrame(in LaserFrame frame)
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

            var cts = new CancellationTokenSource();
            _connectionCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // A plug without an address connects to the first DAC discovery
                                    // reports - the listener starts together with this stream, so
                                    // wait for its first announcement instead of giving up.
                                    var address = ResolveAddress();
                                    while (string.IsNullOrWhiteSpace(address))
                                    {
                                        SetStatus("No DAC address (discovering...)", IStatusProvider.StatusLevel.Notice);
                                        await Task.Delay(DiscoveryPollDelayMs, token);
                                        address = ResolveAddress();
                                    }

                                    await ConnectionLoopAsync(address, token);
                                }
                                catch (OperationCanceledException)
                                {
                                    // Torn down while still waiting for discovery
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

        private volatile string? _autoSelectedIp;

        /// <summary>
        /// Teardowns in flight, keyed by DAC address. The socket close runs on the thread pool
        /// because it can block on the library's socket lock while an abandoned send unwinds;
        /// without the gate a stream rebuilt onto the same DAC would dial it while the old
        /// session still holds it, and the old session's Stop could halt the new show.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Task> PendingDisposals = new(StringComparer.OrdinalIgnoreCase);

        private static async Task WaitForPreviousSessionAsync(string ipAddress, CancellationToken token)
        {
            if (!PendingDisposals.TryGetValue(ipAddress, out var pending))
                return;

            // Bounded: a teardown stuck on a dead socket must not keep the laser dark.
            await Task.WhenAny(pending, Task.Delay(PendingDisposeWaitMs, token));
            token.ThrowIfCancellationRequested();
        }

        /// <summary>The discovery entry for an address, if the listener heard the DAC while it was idle.</summary>
        private static bool TryGetDiscoveredDevice(string ipAddress, out DacDto device)
        {
            device = default;
            try
            {
                return DeviceDiscovery.DiscoveredDevices.TryGetValue(ipAddress, out device);
            }
            catch
            {
                // The discovery loop mutates the table concurrently
                return false;
            }
        }

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
                var ip = _lastConnectIp ?? string.Empty;
                var disposal = Task.Run(() =>
                                            {
                                                try { DisposeDacQuietly(dac); }
                                                catch { }
                                            });

                // A new stream to the same DAC must wait for this teardown: the device serves one
                // host at a time, and the stop below would otherwise land on the new session.
                if (ip.Length > 0)
                {
                    PendingDisposals[ip] = disposal;
                    _ = disposal.ContinueWith(_ =>
                    {
                        if (PendingDisposals.TryGetValue(ip, out var current) && ReferenceEquals(current, disposal))
                            PendingDisposals.TryRemove(ip, out var removed);
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }

                LaserPlugDiscovery.ClearConnectedDevice(ip);
            }

            _isConnected = false;
            _isPlaying = false;
            _pointQueue.Clear();
            _lastFrame = null;
        }

        /// <summary>
        /// A session that ended while its queue was still full greets the next connect with points
        /// on an idle engine - a Stop (our own <see cref="DisposeDacQuietly"/> among them) leaves
        /// them in place. Prepare is the protocol's own reset: it discards the queue, so the stream
        /// starts empty instead of waiting for the send loop's in-place recovery at every reconnect.
        /// </summary>
        private static void DiscardStalePoints(Dac dac, DacStatusDto status, string ipAddress)
        {
            if (status.BufferFullness == 0 || status.PlayBackState == PlayBackEngineState.Playing)
                return;

            try
            {
                // 'p' is only accepted from Idle; a session that stopped after a begin reports
                // Prepared, where a Stop is the valid step back to Idle first.
                if (status.PlayBackState != PlayBackEngineState.Idle)
                    dac.Stop();

                var ack = DacResponse.ParseAckCode(dac.Prepare().Response);
                Log.Debug($"EtherDream stream: discarded {status.BufferFullness} stale points at {ipAddress} ({ack})");
            }
            catch (Exception e)
            {
                // The send loop's in-place recovery still covers a failed clear
                Log.Debug($"EtherDream stream: clearing stale points at {ipAddress} failed - {e.Message}");
            }
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

                    await WaitForPreviousSessionAsync(ipAddress, token);

                    var discovered = TryGetDiscoveredDevice(ipAddress, out var device);
                    var capacity = ResolveBufferCapacity(discovered ? device.Identity.BufferCapacity : (ushort)0);
                    var maxPointRate = discovered ? device.Identity.MaxPointRate : 0u;

                    dac = new Dac(ipAddress, capacity, maxPointRate);
                    if (token.IsCancellationRequested)
                    {
                        DisposeDacQuietly(dac);
                        return;
                    }

                    try
                    {
                        var helloStatus = dac.Ping().DacStatus;

                        // The status reports the fill level but never the capacity. A DAC that
                        // discovery never heard (a connected device stops broadcasting) leaves the
                        // library on its default 1799-point assumption - its own fullness is the
                        // proof that the buffer is bigger, and the number to pace against.
                        var observedFullness = helloStatus.BufferFullness;
                        var resolved = ResolveBufferCapacity(capacity, observedFullness);
                        if (resolved != capacity)
                        {
                            DisposeDacQuietly(dac);
                            dac = new Dac(ipAddress, resolved, maxPointRate);
                            helloStatus = dac.Ping().DacStatus;
                            Log.Debug($"EtherDream stream: DAC at {ipAddress} reported {observedFullness} buffered points - "
                                      + $"using {resolved} as its buffer capacity instead of {capacity}");
                        }

                        DiscardStalePoints(dac, helloStatus, ipAddress);
                    }
                    catch { /* the normal send path reports connection failures */ }

                    dac.StatusUpdated += HandleStatusUpdate;
                    dac.DeviceConnected += () => HandleConnectionStateChanged(true);
                    dac.DeviceDisconnected += () => HandleConnectionStateChanged(false);
                    dac.SafetyFaultDetected += reason => Log.Warning($"EtherDream stream: safety fault - {reason}");

                    // A connected DAC stops its discovery broadcasts — report it so the plug picker
                    // can still offer the device a show is streaming to.
                    LaserPlugDiscovery.SetConnectedDevice("Ether Dream", ipAddress);

                    _dac = dac;
                    _lastConnectIp = ipAddress;

                    // TearDown cancels before it takes _dac, so a teardown that ran during the
                    // hello above missed this DAC - release it here or its session stays open.
                    if (token.IsCancellationRequested)
                    {
                        if (Interlocked.CompareExchange(ref _dac, null, dac) == dac)
                        {
                            DisposeDacQuietly(dac);
                            LaserPlugDiscovery.ClearConnectedDevice(ipAddress);
                        }

                        return;
                    }

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

                    // Name every dropped session, not only the first failed connect: the next
                    // connect reports the stale full queue this drop left behind, and without a
                    // line here the log only ever shows that symptom.
                    var heldFor = DateTime.UtcNow - connectedAtUtc;
                    if (heldFor > TimeSpan.FromSeconds(30))
                    {
                        Log.Warning($"EtherDream stream: connection to {ipAddress} dropped after {heldFor.TotalSeconds:0}s - "
                                    + $"{e.GetType().Name}: {e.Message}");
                    }
                    else if (attempt == 1)
                    {
                        Log.Warning($"EtherDream stream: connection to {ipAddress} failed - {e.Message}");
                    }
                    else
                    {
                        Log.Debug($"EtherDream stream: connection to {ipAddress} attempt {attempt} failed - {e.Message}");
                    }

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

            // The same capacity the library paces its own sends against. Growing it from observed
            // fullness (as an earlier version did) made the two disagree: the stream then handed
            // StreamPoints chunks the library considered too big for the buffer, and it pinged
            // inside its socket lock until the send watchdog recycled a healthy connection.
            var capacity = dac.BufferCapacity > 0 ? (int)dac.BufferCapacity : DefaultBufferCapacity;

            var recoverEscalation = 0;              // 0 = soft Prepare, 1+ = full Stop+ClearEStop+Prepare
            var lastRecoverUtc = DateTime.MinValue; // rate-limits the in-place recovery
            var lastPingUtc = DateTime.MinValue;
            var pingFailures = 0;

            // Every call is a NEW DAC session: the firmware starts from an empty buffer with the
            // scanners parked, so nothing carried over from the previous connection is valid. An
            // in-flight frame would continue at its mid-frame offset - the first chunk opens the
            // beam on whatever point that is, one lit maximal jump away from park. Forget the
            // partial frame and the loop frame its approach would ramp from, so the next frame
            // starts with a dark approach from center (the same state a fresh stream has).
            ResetSessionStreamingState();

            // Seed the buffer state from the device before the first send: after a connect that
            // found (or failed to clear) a full queue, a zero fullness would compute room the
            // device does not have.
            PollDac(dac, ref lastPingUtc, ref pingFailures);

            while (!token.IsCancellationRequested)
            {
                if (_isPlaying)
                    recoverEscalation = 0;

                // Master off: STOP the DAC. Streaming a blanked copy of the last frame keeps the
                // scanners sweeping the geometry with zeroed colors - correct for ILDA-compliant
                // blanking, but a projector that emits on zero-color points keeps drawing. The
                // stop command halts playback outright (light source off, engine idle) while the
                // TCP session stays warm, so re-enabling the master resumes within one poll.
                var masterOff = !LaserMasterEnable.Enabled;
                if (masterOff)
                {
                    _pointQueue.Clear();
                    _sendFrame = null;
                    _needsDarkApproach = true;

                    if (!_masterStopped)
                    {
                        _masterStopped = true;
                        try
                        {
                            dac.Stop();
                            SetStatus("Laser master off - DAC stopped", IStatusProvider.StatusLevel.Notice);
                        }
                        catch (Exception e)
                        {
                            // A dead stop command takes the normal error ladder; the state below
                            // still skips streaming, so nothing lit can leave this stream.
                            SetStatus($"Master off, stop failed: {e.Message}", IStatusProvider.StatusLevel.Warning);
                        }
                    }

                    // Hold: heartbeat only, no points out. The 5 ms idle poll also detects a
                    // dead link so the connection loop can rebuild it while disabled.
                    PollDac(dac, ref lastPingUtc, ref pingFailures);
                    try { await Task.Delay(5, token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                if (_masterStopped)
                {
                    // Master came back: reset the engine's state machine so the send path's
                    // own prepare/begin cycle starts a clean session.
                    _masterStopped = false;
                    try
                    {
                        dac.ClearEStop();
                        dac.Prepare();
                    }
                    catch
                    {
                        // The normal send path re-prepares per chunk; a failure here surfaces there.
                    }

                    // The stop left the scanners wherever playback happened to be; the engine now
                    // starts over. Forget the loop frame so the first approach ramps from the park
                    // position instead of from the exit of a frame the scanners never finished.
                    ResetSessionStreamingState();
                }

                // Pick the frame to stream: fresh frames first, otherwise loop the last one
                // so the buffer stays fed between the driver's ~30 Hz submissions. Looping
                // bypasses the watchdog clock, which measures FRESH frames only.
                if (_sendFrame == null || _sendOffset >= _sendFrame.Length)
                {
                    LaserPoint[]? frame = null;
                    if (_pointQueue.TryDequeue(out var points) && points.Length > 0)
                    {
                        frame = points;
                        // Every fresh frame leads beam-off: the approach to its first point is
                        // drawn dark no matter what the content's own first point looks like,
                        // so a pattern flip can never streak the beam across the show.
                        _needsDarkApproach = true;
                    }
                    else if (_lastFrame is { Length: > 0 })
                    {
                        // The loop seam (this frame's exit back to its own start) gets the same
                        // dark ramp as a fresh frame: the DAC replays the frame ~40 times a
                        // second, so an unapproached seam would ring the scanners at loop rate.
                        _needsDarkApproach = true;

                        if (WatchdogElapsed(ref _lastEnqueueTimestamp))
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

                    // Galvo safety: the lead-in must be a dark ramp so the travel to the shape
                    // start happens beam-off AND in bounded steps (a single-sample jump is a
                    // maximal-acceleration demand that rings the scanners). After an engine
                    // (re)start the scanners sit parked at center; while streaming they sit
                    // wherever the previous frame ended - ramp from the real position.
                    _sendFrameRaw = frame;
                    if (_needsDarkApproach && frame.Length > 0)
                    {
                        if (_lastFrame is { Length: > 0 } last)
                        {
                            var lastPoint = last[last.Length - 1];
                            frame = PrependDarkApproach(frame, lastPoint.X, lastPoint.Y, ref _approachScratch);
                        }
                        else
                        {
                            frame = PrependDarkApproach(frame, 0, 0, ref _approachScratch);
                        }

                        _needsDarkApproach = false;
                    }

                    _sendFrame = frame;
                    _sendOffset = 0;
                }

                // The library paces against this very number: a fullness above it can only be a
                // transient reading between commands, never extra room to spend.
                var room = capacity - Math.Min(_bufferFullness, capacity);

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

                // Chunked streaming: push exactly as many points as the DAC reports room for,
                // in one data command each - the socket lock is held only for milliseconds.
                if (room < MinChunkPoints)
                {
                    PollDac(dac, ref lastPingUtc, ref pingFailures);
                    try { await Task.Delay(2, token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                var chunk = BuildTopUpChunk(_sendFrame, _sendOffset, _sendFrameRaw ?? _sendFrame, room, ref _approachScratch, out var chunkLength);

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
                        if (_sendFrameRaw is { } rawFrame)
                            RememberFrame(rawFrame);
                        _sendFrame = null;

                        // A completed frame after a warning (stall, halt recovery) means the show
                        // is back - the status line must not stay on the old warning forever.
                        if (_statusLevel != IStatusProvider.StatusLevel.Success)
                            SetStatus("Streaming", IStatusProvider.StatusLevel.Success);
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

        /// <summary>Queued frames are never mutated, so they are remembered by reference. The blank
        /// scratch is rewritten on the next pass and has to be copied.</summary>
        private void RememberFrame(LaserPoint[] points)
        {
            if (!ReferenceEquals(points, _blankFrameScratch))
            {
                _lastFrame = points;
                return;
            }

            if (_lastFrame == null || _lastFrame.Length != points.Length || ReferenceEquals(_lastFrame, points))
                _lastFrame = new LaserPoint[points.Length];

            Array.Copy(points, _lastFrame, points.Length);
        }

        /// <summary>Drops the streaming state that belongs to one DAC session (see
        /// <see cref="SendLoopAsync"/>): the partial frame, its raw source and the loop frame.</summary>
        private void ResetSessionStreamingState()
        {
            _sendFrame = null;
            _sendFrameRaw = null;
            _sendOffset = 0;
            _lastFrame = null;
            _needsDarkApproach = true;
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

            // Ask the DAC to stop, then discard its queue BEFORE the socket dies. Stop alone
            // returns to Idle with the points still buffered - the state the next session then
            // reports as "buffer full while idle"; Prepare clears it. Killing the connection
            // mid-data is what leaves stale points behind in the first place. The stop travels
            // on the still-open socket; the grace lets both commands flush.
            try
            {
                dac.Stop();
                dac.Prepare();
                Thread.Sleep(50);
            }
            catch
            {
                // Already dead - the kill below is all that's left
            }

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

        /// <summary>True while the master is off and the DAC has been commanded to stop; the
        /// send loop holds (heartbeat only) until the master comes back.</summary>
        private bool _masterStopped;

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
        internal static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.10.24");
        private const int DefaultPort = 5583;
        private const int MaxSenderNameBytes = 32; // Null-terminated, so 31 usable chars

        /// <summary>An empty sender name resolves to the plug's name ("Laser 3"), so several TiXL
        /// PONK senders are distinguishable on receivers instead of a pile of identical "TiXL"s.</summary>
        private static string ResolveSenderName(LaserPlug plug, string? senderName)
        {
            if (!string.IsNullOrWhiteSpace(senderName))
                return senderName;

            return string.IsNullOrWhiteSpace(plug.Name) ? "TiXL" : plug.Name.Trim();
        }
        private const int SendRetryDelayMs = 50;
        private const int MaxConsecutiveSendErrors = 20;
        private const int MaxQueuedFrames = 5;
        private const int ConnectRetryDelayMs = 2000;

        /** Between fresh frames the last (or blanked) frame is re-sent at the driver's pump rate so receivers stay current - not at line rate. */
        private const int RepeatIntervalMs = 33;
        #endregion

        public PonkStream(LaserPlug plug)
        {
            _plug = plug;
            var device = plug.PonkSettings;
            _useMulticast = device.UseMulticast;
            _senderName = ResolveSenderName(plug, device.SenderName);
            _maxScanSpeed = MathF.Max(0.01f, device.MaxScanSpeed);
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _keepalive = device.Keepalive;
            if (device.SenderId == 0)
            {
                // First run of a plug that predates persisted ids: generate once, write back so
                // every later session reuses it.
                device.SenderId = Random.Shared.Next(int.MinValue, int.MaxValue);
            }

            _senderId = device.SenderId;
            _localAddress = plug.LocalAddress ?? string.Empty;
            _targetAddress = string.IsNullOrWhiteSpace(plug.Address) ? DefaultMulticastAddress.ToString() : plug.Address;
            _port = plug.Port > 0 ? plug.Port : DefaultPort;

            StartConnection();
        }

        private readonly LaserPlug _plug;
        private readonly bool _keepalive;
        private readonly bool _useMulticast;
        private readonly string _senderName;
        private readonly float _maxScanSpeed;
        private readonly float _maxBrightness;
        private readonly bool _swapColorChannels;
        private readonly string _localAddress;
        private readonly string _targetAddress;
        private readonly int _port;

        // Sender identity persisted on the plug: receivers (MadMapper keys its Ponk inputs on
        // this int) must recognize the plug across editor restarts instead of seeing a new
        // sender per session. 0 (old plugs) = generated once and written back to the device.
        private readonly int _senderId;

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

        public string? DescribeMismatch(LaserPlug plug)
        {
            var device = plug.PonkSettings;
            var senderName = ResolveSenderName(plug, device.SenderName);
            var target = string.IsNullOrWhiteSpace(plug.Address) ? DefaultMulticastAddress.ToString() : plug.Address;
            var port = plug.Port > 0 ? plug.Port : DefaultPort;

            if (device.UseMulticast != _useMulticast)
                return "multicast mode changed";
            if (senderName != _senderName)
                return $"sender name '{_senderName}' -> '{senderName}'";
            if (MathF.Max(0.01f, device.MaxScanSpeed) != _maxScanSpeed)
                return $"scan speed {_maxScanSpeed} -> {device.MaxScanSpeed}";
            if (Math.Clamp(device.MaxBrightness, 0f, 1f) != _maxBrightness)
                return $"brightness {_maxBrightness} -> {device.MaxBrightness}";
            if (device.SwapColorChannels != _swapColorChannels)
                return "swap channels changed";
            if (device.Keepalive != _keepalive)
                return "keepalive changed";
            if (target != _targetAddress)
                return $"target '{_targetAddress}' -> '{target}'";
            if (port != _port)
                return $"port {_port} -> {port}";
            if ((plug.LocalAddress ?? string.Empty) != _localAddress)
                return $"local address '{_localAddress}' -> '{plug.LocalAddress}'";

            return null;
        }

        public void SubmitFrame(in LaserFrame frame)
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
            // so multicast packets leave on the right interface. A pinned interface that can't
            // bind (NIC unplugged, IP changed under us) falls back to OS routing instead of
            // killing the stream - the retry loop keeps the pinned address for when it returns.
            var bindAddr = localIp ?? IPAddress.Any;
            try
            {
                socket.Bind(new IPEndPoint(bindAddr, 0));
            }
            catch (SocketException)
            {
                if (bindAddr.Equals(IPAddress.Any))
                    throw;

                bindAddr = IPAddress.Any;
                socket.Bind(new IPEndPoint(bindAddr, 0));
            }

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
            var headerBuffer = new byte[MaxDatagramSize];
            var payload = new MemoryStream(capacity: MaxDatagramSize);
            var writer = new BinaryWriter(payload);
            var lastSendTicks = 0L;

            while (!token.IsCancellationRequested && !_disposed)
            {
                // Master off: settled blanking. The blanked copy of the last frame must keep
                // being SENT - receivers render whatever arrived last, so going silent leaves
                // the last lit frame up until their own timeout blanks it. With the plug's
                // keepalive on, content streams unblanked even while the master is off - a
                // deliberate preview override (PONK receivers can drive real scanners, so the
                // default is off). The pick below ignores queued frames while blanked.
                var masterOff = !LaserMasterEnable.Enabled && !_keepalive;
                if (masterOff)
                {
                    _pointQueue.Clear();
                    if (_lastFrame is { Length: > 0 } && !_masterBlankApplied)
                    {
                        _lastFrame = BlankFrameCopy(_lastFrame);
                        _lastFrameBlanked = true;
                        _masterBlankApplied = true;
                    }
                }
                else
                {
                    _masterBlankApplied = false;
                }

                LaserPoint[]? frame = null;
                var frameIsBlanked = false;
                if (!masterOff && _pointQueue.TryDequeue(out var points) && points.Length > 0)
                {
                    frame = points;
                }
                else if (_lastFrame is { Length: > 0 } && Environment.TickCount64 - lastSendTicks >= RepeatIntervalMs)
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
                        var watchdogBlank = WatchdogElapsed(ref _lastEnqueueTimestamp);
                        if (watchdogBlank && !_watchdogBlanked)
                        {
                            _watchdogBlanked = true;
                            Log.Warning("PONK stream: frame watchdog - no new frames, blanking output");
                            SetStatus("Frame watchdog - no new frames, output blanked", IStatusProvider.StatusLevel.Warning);
                        }

                        frame = watchdogBlank && !_lastFrameBlanked ? BlankFrameCopy(_lastFrame) : _lastFrame;
                        frameIsBlanked = watchdogBlank || _lastFrameBlanked;
                    }
                }

                if (frame == null)
                {
                    try { await Task.Delay(1, token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                var frameLength = EncodeFrame(frame);
                var crc = ComputeChecksum(_encodeBuffer, frameLength);
                var chunkCount = ComputeChunkCount(frameLength);

                try
                {
                    SendFrame(socket, target, headerBuffer, _encodeBuffer, frameLength, crc, chunkCount, token);
                    lastSendTicks = Environment.TickCount64;
                    _consecutiveErrors = 0;
                    _lastFrame = frame;
                    _lastFrameBlanked = frameIsBlanked;
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

        private void SendFrame(Socket socket, IPEndPoint target, byte[] headerBuffer, byte[] frameData, int frameLength, uint crc,
                               int chunkCount, CancellationToken token)
        {
            // PONK chunks are header-prefixed; each chunk's payload is a slice of the frame data.
            // All chunks of a frame carry the same CRC so receivers can drop incomplete frames.
            var written = 0;
            for (var chunkNumber = 0; chunkNumber < chunkCount; chunkNumber++)
            {
                var payloadSize = Math.Min(MaxChunkPayload, frameLength - written);
                var totalSize = HeaderSize + payloadSize;

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

        /// <summary>Encodes the frame into <see cref="_encodeBuffer"/> and returns the encoded length.</summary>
        private int EncodeFrame(LaserPoint[] points)
        {
            // pointCount is a ushort and chunkCount a byte, so cap the frame at a point count
            // whose chunking stays inside both limits.
            var count = Math.Min(points.Length, MaxPointsPerFrame);
            var length = PayloadHeaderSize + count * BytesPerPoint;
            if (_encodeBuffer.Length < length)
                _encodeBuffer = new byte[length];

            var data = _encodeBuffer;

            // One path per frame: PATHNUMB identifies the shape across frames so receivers can
            // dispatch; MAXSPEED tells the receiver the requested scan-speed multiplier.
            data[0] = DataFormatXyF32RgbU8;
            data[1] = 2;
            WriteMetaEntry(data.AsSpan(2, MetaEntrySize), "PATHNUMB", 1);
            WriteMetaEntry(data.AsSpan(2 + MetaEntrySize, MetaEntrySize), "MAXSPEED", _maxScanSpeed);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 + 2 * MetaEntrySize, 2), (ushort)count);

            var offset = PayloadHeaderSize;
            for (var i = 0; i < count; i++)
            {
                var p = points[i];
                // LaserPoint.X/Y are ILDA-style 16-bit signed (-32768..32767); normalize to
                // PONK's mandatory float32 [-1, 1] XY + uint8 RGB format. Colors are scaled by
                // the brightness ceiling right before encoding - the final output audit.
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), Math.Clamp(p.X / 32767f, -1f, 1f));
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 4, 4), Math.Clamp(p.Y / 32767f, -1f, 1f));
                data[offset + 8] = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.B : p.R, 0) * _maxBrightness) >> 8, 0, 255);
                data[offset + 9] = (byte)Math.Clamp((int)MathF.Round(Math.Max(p.G, 0) * _maxBrightness) >> 8, 0, 255);
                data[offset + 10] = (byte)Math.Clamp((int)MathF.Round(Math.Max(_swapColorChannels ? p.R : p.B, 0) * _maxBrightness) >> 8, 0, 255);
                offset += BytesPerPoint;
            }

            return length;
        }

        /** Data format, path-metadata count, two metadata entries and the point count. */
        private const int PayloadHeaderSize = 2 + 2 * MetaEntrySize + 2;

        /** float32 X, float32 Y, uint8 R, G, B. */
        private const int BytesPerPoint = 11;

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
        private static uint ComputeChecksum(byte[] data, int length)
        {
            uint sum = 0;
            for (var i = 0; i < length; i++)
                sum += data[i];
            return sum;
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

        /** The remembered frame already has its colors zeroed, so repeats need no new blanked copy. */
        private bool _lastFrameBlanked;

        /** Datagram payload of the frame being sent; send thread only. */
        private byte[] _encodeBuffer = new byte[MaxDatagramSize];
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
        private const int ReconnectDelayMs = 2000;
        private const int LocalhostFallbackDelayMs = 6000;
        private const int ConnectTimeoutMs = 3000;

        /// <summary>Closed-session retries before a pinned target yields to what discovery announces.</summary>
        private const int DiscoveryFallbackFailures = 3;

        public CitpStream(LaserPlug plug)
        {
            var device = plug.CitpSettings;
            _feedName = ResolveFeedName(plug, device.FeedName);
            _sourceKey = device.SourceKey != 0 ? device.SourceKey : (uint)Random.Shared.Next();
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _keepalive = device.Keepalive;
            _directHost = plug.Address ?? string.Empty;
            _directPort = plug.Port > 0 ? plug.Port : 0;
            _localAddress = plug.LocalAddress ?? string.Empty;

            StartConnection();
        }

        private readonly string _feedName;
        private readonly uint _sourceKey;

        // Read per frame by the send loop, so a settings edit applies without a new session.
        private volatile float _maxBrightness;
        private volatile bool _swapColorChannels;
        private volatile bool _keepalive;

        private readonly string _directHost;
        private readonly int _directPort;
        private readonly string _localAddress;

        private readonly object _connectionLock = new();
        private readonly ConcurrentQueue<LaserPoint[]> _frameQueue = new();

        private CancellationTokenSource? _cts;
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private int _connectInProgress;

        private volatile bool _disposed;
        private volatile bool _feedActive;
        private volatile int _requiredFps;
        private uint _frameSequenceNo;
        private long _lastConnectFailureLog;
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

        public bool MatchesPlug(LaserPlug plug)
        {
            return DescribeMismatch(plug) == null;
        }

        /// <summary>The field that no longer matches this stream's construction - null when the
        /// stream still serves the plug. A persistent mismatch makes the driver dispose and rebuild
        /// the stream every few hundred ms, which surfaces as "connected, then instantly
        /// disconnected" in the UI; naming the field makes that loop visible in the log.</summary>
        public void ApplyLiveSettings(LaserPlug plug)
        {
            var device = plug.CitpSettings;
            _maxBrightness = Math.Clamp(device.MaxBrightness, 0f, 1f);
            _swapColorChannels = device.SwapColorChannels;
            _keepalive = device.Keepalive;
        }

        public string? DescribeMismatch(LaserPlug plug)
        {
            var device = plug.CitpSettings;
            var feedName = ResolveFeedName(plug, device.FeedName);
            var directPort = plug.Port > 0 ? plug.Port : 0;

            // SourceKey is generated per session when the plug leaves it at 0, so it is not part of
            // the comparison - only what the UI can actually set.
            if (feedName != _feedName)
                return $"feed name '{_feedName}' -> '{feedName}'";

            var address = plug.Address ?? string.Empty;
            if ((address != _directHost || directPort != _directPort) && !IsConnectedPeer(address, directPort))
                return $"target '{_directHost}:{_directPort}' -> '{address}:{directPort}'";
            if ((plug.LocalAddress ?? string.Empty) != _localAddress)
                return $"local address '{_localAddress}' -> '{plug.LocalAddress}'";

            return null;
        }

        /// <summary>A plug left on discovery that the setup card then pins to the peer this stream is already
        /// connected to is the same session - rebuilding it would only drop the feed.</summary>
        private bool IsConnectedPeer(string address, int port)
        {
            if (_directHost.Length != 0 || _directPort != 0 || !IsConnected)
                return false;

            lock (_connectionLock)
                return address == _captureIp && port == _captureTcpPort;
        }

        public void SubmitFrame(in LaserFrame frame)
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

            // Host AND port on the plug = direct connect; anything else keeps PLoc discovery.
            _captureIp = _directHost;
            _captureTcpPort = IsDirectConnect ? _directPort : 0;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _feedActive = false;
            _requiredFps = 0;

            // The discovery listener runs for every CITP stream, including a pinned host: Capture's
            // control port is dynamic and only announced over PLoc, so a pinned port goes stale the
            // moment Capture restarts. Listening lets the connect follow the live port (see
            // AdoptAnnouncedPort) instead of retrying a dead one forever.
            CitpPeerDiscovery.EnsureStarted();
            CitpPeerDiscovery.PeerAnnounced += OnPeerAnnounced;

            if (IsDirectConnect)
            {
                SetStatus($"Connecting to {_captureIp}:{_captureTcpPort}", IStatusProvider.StatusLevel.Notice);
            }
            else
            {
                // The shared PLoc listener feeds peers to every CITP stream; this stream connects
                // to the first peer that announces itself (subnet-filtered like the dropdown).
                SetStatus($"Listening for Capture on {MulticastAddress}:{MulticastPort}", IStatusProvider.StatusLevel.Notice);
            }

            // TcpClient.Connect blocks for seconds when unreachable - never on the caller's thread.
            var token = cts.Token;
            _ = Task.Run(() => ReconnectLoopAsync(token));
        }

        /// <summary>
        /// Capture's control port is dynamic: it re-announces the port it currently listens on, and
        /// the port stored on a plug goes stale after any Capture restart. With a pinned host, adopt
        /// the announced port for that host - that keeps the host pinned (the venue's machine) while
        /// the port follows reality, instead of a session that can never connect.
        /// </summary>
        private bool AdoptAnnouncedPort()
        {
            if (!IsDirectConnect)
                return false;

            foreach (var peer in CitpPeerDiscovery.Peers)
            {
                if (!string.Equals(peer.Ip, _captureIp, StringComparison.Ordinal) || peer.TcpPort <= 0)
                    continue;

                if (peer.TcpPort == _captureTcpPort)
                    return false;

                lock (_connectionLock)
                {
                    if (_disposed || _tcpClient is { Connected: true })
                        return false;

                    Log.Info($"CITP: Capture at {_captureIp} announces port {peer.TcpPort} "
                             + $"(plug has {_captureTcpPort}) - following the announced port");
                    _captureTcpPort = peer.TcpPort;
                }

                return true;
            }

            return false;
        }

        /** Host and port both set on the plug: connect there and ignore PLoc announcements. */
        private bool IsDirectConnect => _directPort > 0 && !string.IsNullOrWhiteSpace(_directHost);

        /// <summary>An empty feed name resolves to the plug's name ("Laser 3"), so several TiXL
        /// laser outputs appear as distinct feeds in Capture instead of a pile of identical
        /// "Laser Feed"s that cannot be told apart or addressed separately.</summary>
        private static string ResolveFeedName(LaserPlug plug, string? feedName)
        {
            if (!string.IsNullOrWhiteSpace(feedName))
                return feedName.Trim();

            return string.IsNullOrWhiteSpace(plug.Name) ? "Laser Feed" : plug.Name.Trim();
        }

        /// <summary>
        /// Keeps the control session up: connects to the current target and reconnects after the
        /// receiver restarts or drops the session. In discovery mode announcements re-target it;
        /// a receiver that announced before the stream started will not repeat immediately, so
        /// the default localhost capture is tried when nothing announced within a few seconds.
        /// </summary>
        private async Task ReconnectLoopAsync(CancellationToken token)
        {
            var startedAt = Environment.TickCount64;
            var consecutiveFailures = 0;
            try
            {
                while (!token.IsCancellationRequested && !_disposed)
                {
                    if (!IsConnected)
                    {
                        // Before retrying a pinned port, take Capture's announced one: a stale port
                        // is the usual reason a plug sits on "connecting" while Capture is up.
                        AdoptAnnouncedPort();

                        if (!IsDirectConnect && Environment.TickCount64 - startedAt > LocalhostFallbackDelayMs)
                        {
                            lock (_connectionLock)
                            {
                                if (_captureTcpPort == 0)
                                {
                                    _captureIp = "127.0.0.1";
                                    _captureTcpPort = DefaultCapturePort;
                                }
                            }
                        }

                        await ConnectToCaptureAsync();

                        if (IsConnected)
                        {
                            consecutiveFailures = 0;
                        }
                        else if (++consecutiveFailures >= DiscoveryFallbackFailures)
                        {
                            // Self-healing: a pinned host that stopped answering (Capture on another
                            // NIC / another machine) falls back to what discovery actually announces,
                            // so opening Capture reconnects on its own — no re-picking in the dropdown.
                            if (AdoptDiscoveredPeer())
                                consecutiveFailures = 0;
                        }
                    }

                    await Task.Delay(ReconnectDelayMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Stream disposed
            }
        }

        /// <summary>Retargets a pinned connection at the peer CITP discovery actually announces -
        /// same /24 first, then the first announced. Returns true when the target changed.</summary>
        private bool AdoptDiscoveredPeer()
        {
            if (!IPAddress.TryParse(_captureIp, out var pinned) || pinned.GetAddressBytes().Length != 4)
                return false;

            var pinnedBytes = pinned.GetAddressBytes();
            CitpPeerDiscovery.Peer? best = null;
            CitpPeerDiscovery.Peer? first = null;

            foreach (var peer in CitpPeerDiscovery.Peers)
            {
                if (peer.TcpPort <= 0 || !IPAddress.TryParse(peer.Ip, out var peerIp) || peerIp.GetAddressBytes().Length != 4)
                    continue;

                first ??= peer;

                var bytes = peerIp.GetAddressBytes();
                if (bytes[0] != pinnedBytes[0] || bytes[1] != pinnedBytes[1] || bytes[2] != pinnedBytes[2])
                    continue;

                best = peer;
                break;
            }

            best ??= first;
            if (best == null)
                return false;

            lock (_connectionLock)
            {
                if (_disposed || _tcpClient is { Connected: true })
                    return false;

                if (best.Ip == _captureIp && best.TcpPort == _captureTcpPort)
                    return false;

                Log.Debug($"CITP: {_captureIp}:{_captureTcpPort} is not answering - using the announced Capture "
                          + $"{best.Ip}:{best.TcpPort} for feed '{_feedName}'");
                _captureIp = best.Ip;
                _captureTcpPort = best.TcpPort;
            }

            return true;
        }

        private void OnPeerAnnounced(CitpPeerDiscovery.Peer peer)
        {
            if (_disposed || IsConnected || IsDirectConnect)
                return;

            // With a local interface selected, prefer peers from its subnet: peers announcing
            // from a different subnet than the chosen interface are ignored (virtual adapters
            // announce on their own subnets - Capture on a vEthernet looks like that).
            // "0.0.0.0" is the picker's "any interface" and has no subnet to prefer - treating it
            // as one rejected every non-loopback peer, so discovery silently fell back to localhost.
            var peerIp = IPAddress.Parse(peer.Ip);
            if (IPAddress.TryParse(_localAddress, out var selectedLocal)
                && !selectedLocal.Equals(IPAddress.Any)
                && !IPAddress.IsLoopback(peerIp)
                && selectedLocal.GetAddressBytes().Length == 4
                && peerIp.GetAddressBytes().Length == 4
                && selectedLocal.GetAddressBytes()[0] != peerIp.GetAddressBytes()[0])
            {
                return;
            }

            lock (_connectionLock)
            {
                if (_disposed || _tcpClient is { Connected: true })
                    return;

                _captureIp = peer.Ip;
                _captureTcpPort = peer.TcpPort;
            }

            // Runs on the shared discovery thread - a blocking connect would stall every stream's discovery.
            _ = Task.Run(ConnectToCaptureAsync);
        }

        #region TCP control connection
        private async Task ConnectToCaptureAsync()
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
                await ConnectToCaptureInnerAsync(ip, targetPort);
            }
            finally
            {
                Interlocked.Exchange(ref _connectInProgress, 0);
            }
        }

        private async Task ConnectToCaptureInnerAsync(IPAddress ip, int targetPort)
        {
            TcpClient? tcpClient = null;
            try
            {
                StopTcpConnection();

                tcpClient = new TcpClient(ip.AddressFamily);

                // With a pinned interface, bind the socket to it so the session leaves on the
                // chosen NIC. A bind that fails (NIC gone since the pick) falls back to OS
                // routing rather than never connecting.
                if (IPAddress.TryParse(_localAddress, out var localIp)
                    && !localIp.Equals(IPAddress.Any)
                    && !IPAddress.IsLoopback(ip))
                {
                    try
                    {
                        tcpClient.Client.Bind(new IPEndPoint(localIp, 0));
                    }
                    catch (Exception bindEx)
                    {
                        Log.Debug($"CITP: interface bind to {_localAddress} failed ({bindEx.Message}) - using OS routing");
                    }
                }

                // A filtered/unreachable host used to stall a thread-pool thread for seconds in
                // a blocking Connect every reconnect cycle - bound now, and the retry loop keeps
                // its cadence.
                using var connectCts = new CancellationTokenSource(ConnectTimeoutMs);
                try
                {
                    await tcpClient.ConnectAsync(ip, targetPort, connectCts.Token);
                }
                catch (OperationCanceledException)
                {
                    SetStatus($"CITP: {ip}:{targetPort} did not answer within {ConnectTimeoutMs / 1000}s",
                              IStatusProvider.StatusLevel.Notice);
                    StopTcpConnection();
                    try { tcpClient.Close(); } catch { /* ignore */ }
                    return;
                }

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
                    Log.Debug($"CITP: control session established with {ip}:{targetPort} (feed '{_feedName}')");

                    var token = _cts.Token;
                    var client = tcpClient;
                    var stream = tcpStream;
                    new Thread(() => TcpReceiveLoop(token, client, stream))
                        {
                            IsBackground = true,
                            Name = "CITP_TCP_Receiver",
                        }
                        .Start();
                    new Thread(() => SendLoop(client))
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

                // Visible, not just in the card status: a plug retrying a dead port every 2 s is
                // the usual reason a laser output never reaches its device.
                var now = Environment.TickCount64;
                if (now - _lastConnectFailureLog > 10000)
                {
                    _lastConnectFailureLog = now;
                    Log.Debug($"CITP: cannot reach Capture at {ip}:{targetPort} ({ex.Message}) - retrying; "
                              + "the plug follows Capture's announced port when one is known");
                }

                StopTcpConnection();
                // A failed Connect leaves its socket open until finalization; retries run every few seconds.
                try { tcpClient?.Close(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Drops the control session. A caller that still holds a session (a receive loop that just
        /// noticed its peer closing) passes it as <paramref name="session"/>: teardown is only
        /// allowed while that is still the live session, or an old loop unwinding would close the
        /// replacement connection that a reconnect already installed.
        /// </summary>
        private void StopTcpConnection(TcpClient? session = null)
        {
            // Join-free teardown: the loops hold local references and exit the moment their
            // socket is closed / token cancelled; joining here could stall for seconds on a
            // wedged socket.
            lock (_connectionLock)
            {
                if (session != null && !ReferenceEquals(_tcpClient, session))
                    return;

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
            // TCP carries no message boundaries: one read may hold several CITP messages or part
            // of one, so messages are cut from an accumulating buffer by their MessageSize.
            var buffer = new byte[4096];
            var buffered = 0;
            var skipRemaining = 0; // rest of a message too large for the buffer (not a laser control message)
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    if (buffered == buffer.Length)
                        buffered = 0;

                    var bytesRead = stream.Read(buffer, buffered, buffer.Length - buffered);
                    if (bytesRead == 0)
                    {
                        Log.Debug($"CITP: receiver closed the control session (feed '{_feedName}') - reconnecting");
                        StopTcpConnection(client);
                        break;
                    }

                    buffered += bytesRead;
                    var consumed = 0;
                    while (consumed < buffered)
                    {
                        if (skipRemaining > 0)
                        {
                            var skipped = Math.Min(skipRemaining, buffered - consumed);
                            skipRemaining -= skipped;
                            consumed += skipped;
                            continue;
                        }

                        var size = ReadCitpMessageSize(buffer, consumed, buffered - consumed);
                        if (size == 0)
                            break;

                        if (size < 0)
                        {
                            // Not a CITP header - drop what we have and resync on the next read
                            consumed = buffered;
                            break;
                        }

                        if (size > buffer.Length)
                        {
                            skipRemaining = size;
                            continue;
                        }

                        if (size > buffered - consumed)
                            break;

                        HandleControlMessage(buffer, consumed, size);
                        consumed += size;
                    }

                    if (consumed > 0)
                    {
                        Array.Copy(buffer, consumed, buffer, 0, buffered - consumed);
                        buffered -= consumed;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    StopTcpConnection(client);
                    break;
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                        StopTcpConnection(client);
                    break;
                }
            }
        }

        /// <summary>Total size of the CITP message starting at <paramref name="offset"/>: 0 while its
        /// 20-byte header is incomplete, -1 when the bytes are not a CITP header. A header without a
        /// usable MessageSize claims everything buffered, the framing of a single read.</summary>
        private static int ReadCitpMessageSize(byte[] buffer, int offset, int available)
        {
            if (available < 4)
                return 0;

            if (ReadBigEndianUint(buffer, offset) != CitpCookie)
                return -1;

            if (available < 20)
                return 0;

            var size = ReadLittleEndianUint(buffer, offset + 8);
            if (size < 20)
                return available;

            return size > int.MaxValue ? -1 : (int)size;
        }

        private void HandleControlMessage(byte[] buffer, int offset, int length)
        {
            if (length < 24)
                return; // 20-byte CITP header + 4-byte layer content type minimum

            var contentType = ReadBigEndianUint(buffer, offset + 16); // offset 16 in the 20-byte CITP header
            if (contentType != CaexCookie)
                return;

            var caexType = ReadLittleEndianUint(buffer, offset + 20); // CAEX ContentCode at offset 20
            switch ((CaexContentType)caexType)
            {
                case CaexContentType.GetLaserFeedList:
                    SendLaserFeedList();
                    break;
                case CaexContentType.LaserFeedControl:
                    HandleLaserFeedControl(buffer, offset, length);
                    break;
                case CaexContentType.EnterShow:
                    SendEnterShow();
                    break;
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
                // A dead control session must be torn down so discovery can reconnect - a
                // lingering half-open socket keeps the feed invisible to the receiver.
                StopTcpConnection();
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
                // A dead control session must be torn down so discovery can reconnect - a
                // lingering half-open socket keeps the feed invisible to the receiver.
                StopTcpConnection();
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
                // A dead control session must be torn down so discovery can reconnect - a
                // lingering half-open socket keeps the feed invisible to the receiver.
                StopTcpConnection();
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
                // A dead control session must be torn down so discovery can reconnect - a
                // lingering half-open socket keeps the feed invisible to the receiver.
                StopTcpConnection();
                SetStatus($"CITP send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
        }

        /// <summary>The capture tells us to start/stop sending frames: FeedIndex (1) + FrameRate (1)
        /// after the 24-byte CAEX header. FrameRate 0 stops the feed.</summary>
        private void HandleLaserFeedControl(byte[] buffer, int offset, int length)
        {
            if (length < 26)
                return;

            var feedIndex = buffer[offset + 24];
            var frameRate = buffer[offset + 25];

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
        /// <summary>How often the feed list is re-announced on the control session: Capture
        /// re-lists the feed after restarts without waiting for its own GetLaserFeedList.</summary>
        private static readonly TimeSpan ReannounceInterval = TimeSpan.FromSeconds(5);

        /// <summary>Runs for the lifetime of one control session: a reconnect starts a new loop, so
        /// this one exits once <paramref name="client"/> is no longer the live connection.</summary>
        private void SendLoop(TcpClient client)
        {
            var token = _cts?.Token ?? default;
            using var udpSender = new UdpClient();
            var target = new IPEndPoint(MulticastAddress, MulticastPort);
            LaserPoint[]? lastFrame = null;
            var blankScratch = Array.Empty<LaserPoint>();
            var lastAnnounceUtc = DateTime.UtcNow;
            var nextSendTimestamp = 0L;

            while (!token.IsCancellationRequested && !_disposed && ReferenceEquals(Volatile.Read(ref _tcpClient), client))
            {
                // Keepalive: CITP is a previz path that never drives projectors, so with the
                // plug's keepalive on the feed keeps streaming content while the LASER master
                // is off. Without it, settled blanking - the geometry keeps moving, zero light.
                var masterOff = !LaserMasterEnable.Enabled && !_keepalive;
                if (masterOff)
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

                    var requiredFps = _requiredFps;
                    Thread.Sleep(requiredFps > 0 ? Math.Max(1, 1000 / requiredFps) : 15);
                }
                else if (_feedActive && !_frameQueue.IsEmpty)
                {
                    // The capture asks for a frame rate and plays what it gets at that rate. The driver
                    // produces a frame per editor frame, which can be well above it; every extra frame
                    // would wait in the receiver's queue and show up as lag. So frames go out at the
                    // requested rate, and the newest one wins: one that waited behind a newer frame is
                    // already out of date.
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now < nextSendTimestamp)
                    {
                        Thread.Sleep(1);
                    }
                    else
                    {
                        LaserPoint[]? newest = null;
                        while (_frameQueue.TryDequeue(out var queued))
                            newest = queued;

                        if (newest != null)
                        {
                            lastFrame = newest;
                            var requiredFps = _requiredFps;
                            var interval = requiredFps > 0 ? System.Diagnostics.Stopwatch.Frequency / requiredFps : 0;
                            nextSendTimestamp = Math.Max(nextSendTimestamp + interval, now);
                            try
                            {
                                SendLaserFeedFrame(udpSender, newest, target);
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"CITP frame send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
                            }
                        }
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }

                // Re-announce the feed on the control session: if Capture restarted (or joined
                // late) it re-lists the feed without anyone pressing anything.
                if (IsConnected && DateTime.UtcNow - lastAnnounceUtc > ReannounceInterval)
                {
                    lastAnnounceUtc = DateTime.UtcNow;
                    SendLaserFeedList();
                }
            }
        }

        /// <summary>Reusable datagram buffer, send thread only - the largest LaserFeedFrame this
        /// stream can produce: 24-byte CITP header + 11-byte body + 5 bytes per point at the
        /// datagram cap. Encoding into it replaces a MemoryStream + BinaryWriter + ToArray per
        /// frame, which at pump rate across a rig was enough garbage to stall the editor on GC.
        /// Byte layout identical to the BinaryWriter version it replaced.</summary>
        private readonly byte[] _sendBuffer = new byte[35 + MaxPointsPerDatagram * 5];

        private void SendLaserFeedFrame(UdpClient udpSender, LaserPoint[] points, IPEndPoint target)
        {
            if (points.Length == 0)
                return;

            var pointCount = Math.Min(points.Length, MaxPointsPerDatagram);

            try
            {
                var data = _sendBuffer;

                // CITP header (20 bytes): cookie, version, request index, size placeholder,
                // part count/part, content type - the same bytes WriteCitpHeader lays down.
                // Locals, not the consts directly: a const-folded shift into a byte cast is a
                // compile-time overflow the runtime cast is not.
                var citpCookie = CitpCookie;
                var caexCookie = CaexCookie;
                data[0] = (byte)(citpCookie >> 24);
                data[1] = (byte)(citpCookie >> 16);
                data[2] = (byte)(citpCookie >> 8);
                data[3] = (byte)citpCookie;
                data[4] = 1;                    // VersionMajor
                data[5] = 0;                    // VersionMinor
                data[6] = 0;                    // RequestIndex
                data[7] = 0;
                // bytes 8-11: MessageSize, patched once the length is known
                data[12] = 1;                   // MessagePartCount
                data[13] = 0;
                data[14] = 0;                   // MessagePart
                data[15] = 0;
                data[16] = (byte)(caexCookie >> 24);
                data[17] = (byte)(caexCookie >> 16);
                data[18] = (byte)(caexCookie >> 8);
                data[19] = (byte)caexCookie;

                // Layer content code (little-endian, as BinaryWriter.Write(uint) wrote it)
                var contentCode = (uint)CaexContentType.LaserFeedFrame;
                data[20] = (byte)contentCode;
                data[21] = (byte)(contentCode >> 8);
                data[22] = (byte)(contentCode >> 16);
                data[23] = (byte)(contentCode >> 24);

                // LaserFeedFrame body: sourceKey + feedIndex + frameSequence + pointCount
                var sourceKey = _sourceKey;
                data[24] = (byte)sourceKey;
                data[25] = (byte)(sourceKey >> 8);
                data[26] = (byte)(sourceKey >> 16);
                data[27] = (byte)(sourceKey >> 24);
                data[28] = 0;                   // feedIndex

                var sequence = _frameSequenceNo++;
                data[29] = (byte)sequence;
                data[30] = (byte)(sequence >> 8);
                data[31] = (byte)(sequence >> 16);
                data[32] = (byte)(sequence >> 24);
                data[33] = (byte)pointCount;
                data[34] = (byte)(pointCount >> 8);

                var maxBrightness = _maxBrightness;
                var swapColor = _swapColorChannels;
                var offset = 35;
                for (var i = 0; i < pointCount; i++)
                {
                    // ConvertLaserPoint inlined: ILDA 16-bit signed -> 12-bit unsigned, RGB565 color
                    var p = points[i];
                    var xNorm = Math.Clamp(p.X / 32767f, -1f, 1f);
                    var yNorm = Math.Clamp(p.Y / 32767f, -1f, 1f);
                    var x = Math.Clamp((int)((xNorm + 1f) * 0.5f * 4095f), 0, 4095);
                    var y = Math.Clamp((int)((yNorm + 1f) * 0.5f * 4095f), 0, 4095);

                    var r = (byte)Math.Clamp((int)MathF.Round(Math.Max(swapColor ? p.B : p.R, 0) * maxBrightness) >> 8, 0, 255);
                    var g = (byte)Math.Clamp((int)MathF.Round(Math.Max(p.G, 0) * maxBrightness) >> 8, 0, 255);
                    var b = (byte)Math.Clamp((int)MathF.Round(Math.Max(swapColor ? p.R : p.B, 0) * maxBrightness) >> 8, 0, 255);
                    var color = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 5) | ((b & 0xF8) >> 3));

                    data[offset] = (byte)x;
                    data[offset + 1] = (byte)y;
                    data[offset + 2] = (byte)(((x >> 8) & 0x0F) | (((y >> 8) & 0x0F) << 4));
                    data[offset + 3] = (byte)color;
                    data[offset + 4] = (byte)(color >> 8);
                    offset += 5;
                }

                var length = offset;
                data[8] = (byte)length;
                data[9] = (byte)(length >> 8);
                data[10] = (byte)(length >> 16);
                data[11] = (byte)(length >> 24);

                udpSender.Send(data, length, target);
            }
            catch (Exception ex)
            {
                SetStatus($"CITP frame send failed: {ex.Message}", IStatusProvider.StatusLevel.Warning);
            }
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
            CitpPeerDiscovery.PeerAnnounced -= OnPeerAnnounced;
            var cts = _cts;
            cts?.Cancel();

            lock (_connectionLock)
            {
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
