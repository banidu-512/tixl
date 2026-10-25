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
/// </summary>
internal sealed class OmtVideoReceiver : IDisposable
{
    public OmtVideoReceiver(string? sourceName)
    {
        if (!OmtRuntime.IsAvailable)
        {
            _lastStatusMessage = OmtRuntime.MissingMessage;
            return;
        }

        Connect(sourceName);
    }

    /// <summary>The texture holding the last uploaded frame; null until the first frame arrives.</summary>
    public Texture2D? Texture => _gpuTexture;

    public string? LastStatusMessage => _lastStatusMessage;

    public bool IsConnected
    {
        get
        {
            var receiver = _receiver;
            if (receiver == null)
                return false;

            try
            {
                return receiver.IsConnected();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Uploads the newest pending frame on the calling thread. Returns true when a new frame was uploaded;
    /// <paramref name="texture"/> is the current texture in both cases (null before the first frame).
    /// </summary>
    public bool TryUpload(out Texture2D? texture)
    {
        FrameSlot? frame;
        lock (_frameLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

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
        finally
        {
            lock (_frameLock)
            {
                _freeSlots.Push(frame);
            }
        }
    }

    private void Connect(string? sourceName)
    {
        Disconnect();

        if (string.IsNullOrEmpty(sourceName))
        {
            _lastStatusMessage = "No OMT source selected";
            return;
        }

        try
        {
            _receiverName = sourceName;
            _receiver = new OMTReceive(sourceName, OMTFrameType.Video, OMTPreferredVideoFormat.BGRA, OMTReceiveFlags.None);
            _exitThread = false;
            _receiveThread = new Thread(ReceiveThreadProc)
                                 {
                                     IsBackground = true,
                                     Name = "OmtVideoReceiver",
                                 };

            // The receiver is the thread's own: a thread that outlived a reconnect must never touch the new one.
            _receiveThread.Start(_receiver);
            _lastStatusMessage = $"Waiting for {sourceName}";
        }
        catch (Exception e)
        {
            _receiver = null;
            _lastStatusMessage = $"Could not receive OMT source {sourceName}: {e.Message}";
            Log.Warning(_lastStatusMessage, this);
        }
    }

    private void Disconnect()
    {
        _exitThread = true;

        if (_receiveThread != null)
        {
            _receiveThread.Join(TimeSpan.FromSeconds(2));
            _receiveThread = null;
        }

        if (_receiver != null)
        {
            try
            {
                _receiver.Dispose();
            }
            catch (Exception e)
            {
                Log.Debug($"Disposing the OMT receiver threw: {e.Message}", this);
            }

            _receiver = null;
        }

        DiscardPendingFrames();
    }

    private void ReceiveThreadProc(object? state)
    {
        if (state is not OMTReceive receiver)
            return;

        while (!_exitThread && ReferenceEquals(_receiver, receiver))
        {
            try
            {
                var frame = new OMTMediaFrame();
                if (!receiver.Receive(OMTFrameType.Video, ReceiveTimeoutMs, ref frame))
                {
                    _lastStatusMessage = receiver.IsConnected() ? ConnectedStatus : $"Waiting for {_receiverName}";
                    continue;
                }

                if (frame.Data == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
                    continue;

                CopyToPendingFrame(frame);
                _lastStatusMessage = null;
            }
            catch (Exception e)
            {
                if (_exitThread)
                    return;

                _lastStatusMessage = $"OMT receive failed: {e.Message}";
                Log.Debug(e.ToString(), this);
                return;
            }
        }
    }

    /// <summary>
    /// OMT decodes into a buffer it reuses on the next frame, so the pixels are copied out before the loop
    /// goes on. Frames the render thread hasn't picked up yet are replaced rather than queued.
    /// </summary>
    private void CopyToPendingFrame(OMTMediaFrame frame)
    {
        var length = frame.Stride * frame.Height;
        var slot = TakeFreeSlot(Math.Max(length, 1));

        Marshal.Copy(frame.Data, slot.Bytes, 0, length);
        slot.Width = frame.Width;
        slot.Height = frame.Height;
        slot.Stride = frame.Stride;

        lock (_frameLock)
        {
            if (_pendingFrame != null)
                _freeSlots.Push(_pendingFrame);

            _pendingFrame = slot;
        }
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

    private FrameSlot TakeFreeSlot(int length)
    {
        lock (_frameLock)
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

    private void DiscardPendingFrames()
    {
        lock (_frameLock)
        {
            _pendingFrame?.Dispose();
            _pendingFrame = null;

            while (_freeSlots.Count > 0)
                _freeSlots.Pop().Dispose();
        }

        T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _gpuTexture);
    }

    public void Dispose()
    {
        Disconnect();
        _lastStatusMessage = null;
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

    private readonly Lock _frameLock = new();
    private readonly Stack<FrameSlot> _freeSlots = new();

    private FrameSlot? _pendingFrame;
    private OMTReceive? _receiver;
    private Thread? _receiveThread;
    private string? _receiverName = string.Empty;
    private volatile bool _exitThread;
    private Texture2D? _gpuTexture;
    private string? _lastStatusMessage;
}
