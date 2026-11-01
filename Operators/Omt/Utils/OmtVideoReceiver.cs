#nullable enable
using libomtnet;
using T3.Graphics.Compat;

namespace Lib.Utils;

/// <summary>
/// Receives one OMT source and publishes its newest frame as a GPU texture. Shared by the <c>OmtInput</c>
/// operator and by boygrouping's remote camera relay, so both use the same receive loop, frame handoff
/// and upload path.
/// <para>The receive thread only copies into memory. <see cref="TryUpload"/> runs on the render thread and
/// uploads the newest frame, replacing one that has not been picked up yet.</para>
/// <para>Lifetime: everything native about a stream lives in a <see cref="ReceiveSession"/>, and the thread
/// started with that session is its only user — it receives, rebuilds a wedged transport and frees the whole
/// session when it stops. The render thread reads volatile status fields and hands frames between the two
/// sides under the session's lock, so no native object is ever freed while a call on another thread is still
/// inside it, no matter how long a teardown waits.</para>
/// </summary>
internal sealed class OmtVideoReceiver : IDisposable
{
    public OmtVideoReceiver(string? sourceName)
    {
        if (!OmtRuntime.IsAvailable)
        {
            _staticStatusMessage = OmtRuntime.MissingMessage;
            return;
        }

        Connect(sourceName);
    }

    /// <summary>The texture holding the last uploaded frame; null until the first frame arrives.</summary>
    public Texture2D? Texture => _gpuTexture;

    public string? LastStatusMessage => _session?.StatusMessage ?? _staticStatusMessage;

    /// <summary>Reported by the receive thread, which asks the transport on its own regular wake-ups.</summary>
    public bool IsConnected => _session?.TransportConnected ?? false;

    /// <summary>
    /// Uploads the newest pending frame on the calling thread. Returns true when a new frame was uploaded;
    /// <paramref name="texture"/> is the current texture in both cases (null before the first frame).
    /// </summary>
    public bool TryUpload(out Texture2D? texture)
    {
        var session = _session;
        var frame = session?.TakePending();
        if (frame == null)
        {
            texture = _gpuTexture;
            return false;
        }

        try
        {
            Upload(frame);
            texture = _gpuTexture;
            return true;
        }
        catch (Exception e)
        {
            Log.Debug($"Uploading an OMT frame failed: {e.Message}", this);
            texture = _gpuTexture;
            return false;
        }
        finally
        {
            session!.ReturnSlot(frame);
        }
    }

    private void Connect(string? sourceName)
    {
        Disconnect();

        if (string.IsNullOrEmpty(sourceName))
        {
            _staticStatusMessage = "No OMT source selected";
            return;
        }

        var session = new ReceiveSession(sourceName);
        try
        {
            session.Receiver = CreateReceiver(sourceName);
        }
        catch (Exception e)
        {
            _staticStatusMessage = $"Could not receive OMT source {sourceName}: {e.Message}";
            Log.Warning(_staticStatusMessage, this);
            return;
        }

        _exitThread = false;
        _session = session;
        try
        {
            _receiveThread = new Thread(ReceiveThreadProc)
                                 {
                                     IsBackground = true,
                                     Name = "OmtVideoReceiver",
                                 };
            _receiveThread.Start(session);
        }
        catch (Exception e)
        {
            // No thread runs the session, so freeing it here is safe.
            _session = null;
            DisposeReceiver(session.Receiver);
            session.DisposeSlots();
            _staticStatusMessage = $"Could not receive OMT source {sourceName}: {e.Message}";
            Log.Warning(_staticStatusMessage, this);
            return;
        }

        _staticStatusMessage = null;
        session.StatusMessage = $"Waiting for {sourceName}";
    }

    private void Disconnect()
    {
        _exitThread = true;
        var thread = _receiveThread;
        _receiveThread = null;
        Volatile.Write(ref _session, null);

        // The session is not freed here: its own thread disposes it, whether that already happened or the
        // thread is stuck inside a native call that outlasts this wait and cleans up when it returns.
        thread?.Join(TimeSpan.FromSeconds(WorkerJoinTimeoutSeconds));

        T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _gpuTexture);
    }

    private void ReceiveThreadProc(object? state)
    {
        if (state is not ReceiveSession session)
            return;

        var consecutiveFailures = 0;
        try
        {
            while (!_exitThread && ReferenceEquals(Volatile.Read(ref _session), session))
            {
                var receiver = session.Receiver!;
                try
                {
                    var frame = new OMTMediaFrame();
                    if (!receiver.Receive(OMTFrameType.Video, ReceiveTimeoutMs, ref frame))
                    {
                        session.TransportConnected = receiver.IsConnected();
                        session.StatusMessage = session.TransportConnected
                                                    ? ConnectedStatus
                                                    : $"Waiting for {session.SourceName}";
                        continue;
                    }

                    if (frame.Data == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
                        continue;

                    CopyToPendingFrame(session, frame);
                    session.TransportConnected = true;
                    session.StatusMessage = null;
                    consecutiveFailures = 0;
                }
                catch (Exception e)
                {
                    if (_exitThread)
                        break;

                    consecutiveFailures++;
                    session.StatusMessage = $"OMT receive failed: {e.Message}";
                    Log.Debug(e.ToString(), this);

                    if (consecutiveFailures < RebuildAfterConsecutiveFailures)
                    {
                        Thread.Sleep(ReceiveRetryDelayMs);
                        continue;
                    }

                    // Failures that keep coming mean the transport is wedged, not the network being slow:
                    // replace it and keep going instead of leaving a frozen frame behind forever.
                    if (!TryRebuildReceiver(session, receiver, out var replacement))
                        break;

                    if (!ReferenceEquals(replacement, receiver))
                    {
                        session.Receiver = replacement;
                        DisposeReceiver(receiver);
                        session.TransportConnected = false;
                        session.StatusMessage = $"Reconnecting to {session.SourceName}";
                        consecutiveFailures = 0;
                    }

                    Thread.Sleep(ReceiveRetryDelayMs);
                }
            }
        }
        finally
        {
            session.Closed = true;
            DisposeReceiver(session.Receiver);
            session.DisposeSlots();
        }
    }

    /// <summary>
    /// Replaces a wedged transport on the receive thread. Returns false when this session was superseded by a
    /// reconnect, which means the thread is done; true keeps the thread going, with a new receiver when one
    /// could be created and the current one retained for another retry round when not.
    /// </summary>
    private bool TryRebuildReceiver(ReceiveSession session, OMTReceive current, out OMTReceive replacement)
    {
        replacement = current;
        if (!ReferenceEquals(Volatile.Read(ref _session), session) || _exitThread)
            return false;

        try
        {
            replacement = CreateReceiver(session.SourceName);
            session.Receiver = replacement;
            return true;
        }
        catch (Exception e)
        {
            session.StatusMessage = $"Could not reconnect to {session.SourceName}: {e.Message}";
            return true;
        }
    }

    private static OMTReceive CreateReceiver(string sourceName)
    {
        return new OMTReceive(sourceName, OMTFrameType.Video, OMTPreferredVideoFormat.BGRA, OMTReceiveFlags.None);
    }

    private static void DisposeReceiver(OMTReceive? receiver)
    {
        if (receiver == null)
            return;

        try
        {
            receiver.Dispose();
        }
        catch (Exception e)
        {
            Log.Debug($"Disposing the OMT receiver threw: {e.Message}");
        }
    }

    /// <summary>
    /// OMT decodes into a buffer it reuses on the next frame, so the pixels are copied out before the loop
    /// goes on. Frames the render thread hasn't picked up yet are replaced rather than queued.
    /// </summary>
    private void CopyToPendingFrame(ReceiveSession session, OMTMediaFrame frame)
    {
        var length = frame.Stride * frame.Height;
        var slot = session.TakeFreeSlot(Math.Max(length, 1));

        Marshal.Copy(frame.Data, slot.Bytes, 0, length);
        slot.Width = frame.Width;
        slot.Height = frame.Height;
        slot.Stride = frame.Stride;

        session.StorePending(slot);
    }

    private void Upload(FrameSlot frame)
    {
        if (_gpuTexture == null
            || _gpuTexture.Description.Width != frame.Width
            || _gpuTexture.Description.Height != frame.Height)
        {
            T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _gpuTexture);

            // Device-local and filled with UpdateSubresource, like the camera, video and NDI operators: a
            // CPU-writable texture can't be sampled on Vulkan, where it is a plain upload buffer.
            _gpuTexture = Texture2D.CreateTexture2D(new Texture2DDescription
                                                        {
                                                            BindFlags = BindFlags.ShaderResource,
                                                            Format = Format.B8G8R8A8_UNorm,
                                                            Width = frame.Width,
                                                            Height = frame.Height,
                                                            MipLevels = 1,
                                                            SampleDescription = new SampleDescription(1, 0),
                                                            Usage = ResourceUsage.Default,
                                                            OptionFlags = ResourceOptionFlags.None,
                                                            CpuAccessFlags = CpuAccessFlags.None,
                                                            ArraySize = 1,
                                                        });
        }

        ResourceManager.Device.ImmediateContext.UpdateSubresource(new DataBox(frame.Pointer, frame.Stride, 0), _gpuTexture, 0);
    }

    public void Dispose()
    {
        Disconnect();
        _staticStatusMessage = null;
    }

    /// <summary>
    /// Everything one receive thread owns: its OMT receiver, its frame slots and the newest frame waiting for
    /// the render thread. Each stream gets its own session, so a thread that outlived a reconnect can never
    /// touch newer state, and its own finally frees the session no matter how late it returns.
    /// </summary>
    private sealed class ReceiveSession
    {
        public ReceiveSession(string sourceName) => SourceName = sourceName;

        public readonly string SourceName;

        /** The transport this thread receives with; replaced only by the thread itself while rebuilding. */
        public OMTReceive? Receiver;

        /** Set by the thread before it starts freeing the session's native objects. */
        public volatile bool Closed;

        /** Refreshed by the thread's regular wake-ups, so the render thread never calls into the transport. */
        public volatile bool TransportConnected;

        public volatile string? StatusMessage;

        /// <summary>Takes the newest received frame; null when the render thread is already up to date.</summary>
        public FrameSlot? TakePending()
        {
            lock (_slotLock)
            {
                var frame = _pendingFrame;
                _pendingFrame = null;
                return frame;
            }
        }

        public void StorePending(FrameSlot slot)
        {
            lock (_slotLock)
            {
                if (_pendingFrame != null)
                    _freeSlots.Push(_pendingFrame);

                _pendingFrame = slot;
            }
        }

        public void ReturnSlot(FrameSlot slot)
        {
            lock (_slotLock)
            {
                // The session may have been torn down while the render thread still held this frame; its
                // pinned memory would never be freed by anyone else.
                if (_slotsDisposed)
                    slot.Dispose();
                else
                    _freeSlots.Push(slot);
            }
        }

        public FrameSlot TakeFreeSlot(int length)
        {
            lock (_slotLock)
            {
                if (_freeSlots.Count > 0)
                {
                    var reusable = _freeSlots.Pop();
                    if (reusable.Capacity >= length)
                        return reusable;

                    reusable.Dispose();
                }
            }

            return new FrameSlot(length);
        }

        /** Only the owning thread calls this, after its receiver is gone and nothing fills the slots anymore. */
        public void DisposeSlots()
        {
            lock (_slotLock)
            {
                _slotsDisposed = true;
                _pendingFrame?.Dispose();
                _pendingFrame = null;

                while (_freeSlots.Count > 0)
                    _freeSlots.Pop().Dispose();
            }
        }

        private readonly object _slotLock = new();
        private readonly Stack<FrameSlot> _freeSlots = new();
        private FrameSlot? _pendingFrame;
        private bool _slotsDisposed;
    }

    /// <summary>A received frame in memory the receive thread can fill while the render thread uploads another.</summary>
    private sealed class FrameSlot : IDisposable
    {
        public FrameSlot(int capacity)
        {
            Bytes = new byte[capacity];
            _pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
            Pointer = _pin.AddrOfPinnedObject();
            Capacity = capacity;
        }

        public readonly byte[] Bytes;
        public readonly IntPtr Pointer;
        public readonly int Capacity;
        public int Width;
        public int Height;
        public int Stride;

        public void Dispose()
        {
            if (_pin.IsAllocated)
                _pin.Free();
        }

        private GCHandle _pin;
    }

    private const string ConnectedStatus = "Connected";
    private const int ReceiveTimeoutMs = 250;

    /** Backoff between receive attempts after an exception, and before rebuilding the transport. */
    private const int ReceiveRetryDelayMs = 250;

    /** Roughly two seconds of back-to-back failures before the transport is rebuilt. */
    private const int RebuildAfterConsecutiveFailures = 8;

    /** How long a teardown waits for the receive thread before leaving the cleanup to the thread itself. */
    private const int WorkerJoinTimeoutSeconds = 2;

    private ReceiveSession? _session;
    private Thread? _receiveThread;
    private volatile bool _exitThread;
    private Texture2D? _gpuTexture;

    /** Statuses set before a stream exists (no source selected, codec missing); a stream's own live on its session. */
    private string? _staticStatusMessage;
}
