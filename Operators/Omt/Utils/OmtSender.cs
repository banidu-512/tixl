#nullable enable
using System.Buffers;
using System.Diagnostics;
using T3.Core.Output;
using T3.Core.Output.Streaming;
using T3.Core.Resource.Assets;
using T3.Core.Stats;
using T3.Core.Utils;
using libomtnet;

namespace Lib.Utils;

/// <summary>
/// One OMT sender. Used by the <c>OmtOutput</c> op and offered to the output setup through
/// <see cref="OmtStreamProvider"/>, so both paths share one implementation.
/// <para>Encoding a frame with the VMX codec costs several milliseconds, so it happens on a worker thread: the
/// render loop only copies the frame to a staging texture, reads one from two copies ago into a pooled buffer (the
/// GPU has finished with it by then) and hands that over. Frames queue up at most two deep and the oldest is
/// dropped, which keeps the stream live instead of falling behind.</para>
/// <para>Like the NDI sender, the frame is packed to UYVY on the GPU unless alpha is sent — that halves the
/// readback and spares the encoder a colour conversion. With alpha, frames are sent as BGRA, the only 32-bit
/// format OMT has.</para>
/// </summary>
internal sealed class OmtSender : IOutputStreamSender
{
    public OmtSender(string name)
    {
        _requestedName = SanitizeName(name);
    }

    public string Name => _requestedName;

    public string? LastError => _lastError;

    /// <summary>
    /// The name receivers discover, as "MACHINE (Source Name)"; null until the sender is open. A user picks this
    /// in a receiver, so showing it is the only way to know what this machine advertises.
    /// </summary>
    public string? AdvertisedAddress => _sender?.Address;

    /// <summary>Frames per second sent and advertised to receivers; rendered frames in between are skipped.</summary>
    public int FrameRate = 60;

    /// <summary>Whether the alpha channel is sent, or receivers get an opaque frame.</summary>
    public bool EnableAlpha;

    public void Configure(OutputStreamSettings settings)
    {
        FrameRate = settings.FrameRate;
        EnableAlpha = settings.EnableAlpha;
    }

    /// <summary>Re-targets the sender; takes effect on the next <see cref="Send"/>.</summary>
    public void Rename(string name)
    {
        _requestedName = SanitizeName(name);
    }

    public bool Send(Texture2D frame)
    {
        if (!OmtRuntime.IsAvailable)
        {
            _lastError = OmtRuntime.MissingMessage;
            return false;
        }

        if (frame == null)
        {
            _lastError = "No texture";
            return false;
        }

        var description = frame.Description;
        var format = description.Format;
        if (!IsSupported(format))
        {
            _lastError = """
                         Texture format must be one of the following:
                         B8G8R8A8_UNorm
                         R8G8B8A8_UNorm
                         B8G8R8A8_Typeless
                         R8G8B8A8_Typeless

                         Please use [ConvertFormat] to convert the texture to a supported format.
                         """;
            return false;
        }

        if (description.SampleDescription.Count > 1)
        {
            _lastError = "Multisampled textures are not supported";
            return false;
        }

        if (description.ArraySize != 1)
        {
            _lastError = "Texture arrays are not supported";
            return false;
        }

        var width = description.Width;
        var height = description.Height;

        // VMX refuses anything smaller, and a 1-pixel-wide frame is a mistake rather than a stream.
        if (width < MinimumDimension || height < MinimumDimension)
        {
            _lastError = $"Frames must be at least {MinimumDimension}x{MinimumDimension} pixels (got {width}x{height})";
            return false;
        }

        var packsToUyvy = !EnableAlpha;

        if (!EnsureOpen())
            return false;

        _lastError = null;

        // Nothing to encode for while nobody is connected. Probing occasionally still lets the first frame reach a
        // receiver that connects later, at the cost of at most one probe interval of latency.
        if (_sender!.Connections == 0 && !IsDueForIdleProbe())
            return true;

        if (!IsDueAtFrameRate())
            return true;

        // A frame of another shape than the pipeline was built for starts over. The sender itself survives, since
        // OMT rebuilds its codec when the frame size or rate changes.
        var shapeChanged = _stagingTextures.Count > 0
                           && (width != _width || height != _height || packsToUyvy != _packsToUyvy || format != _sourceFormat);
        if (shapeChanged)
            ReleasePipeline();

        if (_stagingTextures.Count == 0 && !TryCreatePipeline(frame, width, height, packsToUyvy))
            return false;

        var context = ResourceManager.Device.ImmediateContext;
        Texture2D copySource = frame;
        if (packsToUyvy)
        {
            if (!TryPackToUyvy(frame))
                return false;

            copySource = _packedTexture!;
        }

        var freeSlot = FindFreeSlot();
        if (freeSlot < 0)
            return false;

        context.CopySubresourceRegion(copySource, 0, null, _stagingTextures[freeSlot], 0);
        _queuedSlots.Enqueue(freeSlot);

        // Read back the copy from two frames ago: by now the GPU has finished with it, so mapping doesn't stall.
        if (_queuedSlots.Count <= LatencyInFrames)
            return true;

        var readSlot = _queuedSlots.Dequeue();
        TryQueueReadback(context, readSlot, width, height, packsToUyvy, format);
        return true;
    }

    public void Dispose()
    {
        StopWorker();
        Release();
    }

    private void TryQueueReadback(T3.Graphics.Compat.DeviceContext context, int slot, int width, int height, bool packsToUyvy,
                                  Format format)
    {
        var staging = _stagingTextures[slot];
        var dataBox = context.MapSubresource(staging, 0, T3.Graphics.Compat.MapMode.Read, T3.Graphics.Compat.MapFlags.None, out _);
        try
        {
            var stride = dataBox.RowPitch;
            var length = stride * height;
            var buffer = ArrayPool<byte>.Shared.Rent(length);

            Marshal.Copy(dataBox.DataPointer, buffer, 0, length);

            // OMT has no RGBA codec; a raw RGBA frame would reach receivers with red and blue swapped.
            if (!packsToUyvy && format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_Typeless)
                SwapRedAndBlue(buffer, width * 4, stride, height);

            Enqueue(new PendingFrame(buffer, length, stride, width, height, EnableAlpha, _clock.Elapsed.Ticks));
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
    }

    /// <summary>
    /// BGRA stored in memory as RGBA: swapping the two end bytes of every pixel makes it what OMT expects. Rows
    /// are <paramref name="rowPitch"/> apart, which a staging texture may pad beyond the pixels themselves.
    /// </summary>
    private static void SwapRedAndBlue(byte[] buffer, int rowBytes, int rowPitch, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowPitch;
            for (var x = rowStart; x < rowStart + rowBytes; x += 4)
                (buffer[x], buffer[x + 2]) = (buffer[x + 2], buffer[x]);
        }
    }

    private bool EnsureOpen()
    {
        if (_sender != null && _openedName == _requestedName)
            return true;

        Release();

        try
        {
            _sender = new OMTSend(_requestedName, OMTQuality.Default);
            _openedName = _requestedName;
            StartWorker();
            return true;
        }
        catch (Exception e)
        {
            _lastError = $"Could not start the OMT sender: {e.Message}";
            Log.Debug(e.ToString(), this);
            _sender = null;
            return false;
        }
    }

    private bool TryCreatePipeline(Texture2D frame, int width, int height, bool packsToUyvy)
    {
        _width = width;
        _height = height;
        _packsToUyvy = packsToUyvy;
        _sourceFormat = frame.Description.Format;
        _packedWidth = (width + 1) / 2;

        Texture2DDescription stagingDescription;
        if (packsToUyvy)
        {
            if (!TryLoadPackShader())
                return false;

            var packedDescription = new Texture2DDescription
                                        {
                                            Width = _packedWidth,
                                            Height = height,
                                            MipLevels = 1,
                                            ArraySize = 1,
                                            Format = Format.R8G8B8A8_UNorm,
                                            SampleDescription = new SampleDescription(1, 0),
                                            Usage = ResourceUsage.Default,
                                            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                                            CpuAccessFlags = CpuAccessFlags.None,
                                            OptionFlags = ResourceOptionFlags.None,
                                        };
            _packedTexture = Texture2D.CreateTexture2D(packedDescription);
            _packedUav = new UnorderedAccessView(ResourceManager.Device, _packedTexture);
            stagingDescription = packedDescription;
        }
        else
        {
            stagingDescription = frame.Description;
            stagingDescription.MipLevels = 1;
        }

        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.OptionFlags = ResourceOptionFlags.None;
        for (var i = 0; i < SlotCount; i++)
        {
            _stagingTextures.Add(Texture2D.CreateTexture2D(stagingDescription));
        }

        return true;
    }

    private bool TryLoadPackShader()
    {
        if (_packShaderResource?.Value != null)
            return true;

        if (!AssetRegistry.TryResolveAddress(PackShaderAddress, null, out var shaderPath, out _))
        {
            _lastError = $"Can't find {PackShaderAddress}";
            return false;
        }

        _packShaderResource = ResourceManager.CreateShaderResource<ComputeShader>(shaderPath, null, () => "main");
        if (_packShaderResource.Value != null)
            return true;

        _lastError = "The UYVY packing shader failed to compile";
        return false;
    }

    private bool TryPackToUyvy(Texture2D frame)
    {
        var shader = _packShaderResource?.Value;
        var sourceView = SrvManager.GetSrvForTexture(frame);
        if (shader == null || sourceView == null || _packedUav == null)
            return false;

        var context = ResourceManager.Device.ImmediateContext;
        var computeStage = context.ComputeShader;

        // Everything touched is put back: this runs inside whatever the host is rendering.
        context.PushState(StateGroups.ComputeShader | StateGroups.OutputMerger);

        // The frame is often still bound as a render target (the output compositor leaves its target bound), and
        // D3D11 silently drops a view onto a resource that is bound for writing — the shader would read black.
        context.OutputMerger.SetRenderTargets((DepthStencilView?)null, (RenderTargetView?)null);

        computeStage.Set(shader);
        computeStage.SetShaderResource(0, sourceView);
        computeStage.SetUnorderedAccessView(0, _packedUav);
        context.Dispatch((_packedWidth + 15) / 16, (_height + 15) / 16, 1);

        // Unbound before anything else touches the packed texture, which is read next as a copy source.
        context.PopState();

        return true;
    }

    /// <summary>
    /// Whether this frame is the one to send for the next 1/<see cref="FrameRate"/> step of the visual clock. The
    /// render loop runs at the display's rate, so a 30 fps stream from a 60 Hz loop takes every second frame.
    /// </summary>
    private bool IsDueAtFrameRate()
    {
        if (FrameRate <= 0)
            return true;

        var periodSec = 1.0 / FrameRate;
        var frameTimeSec = FrameTiming.VisualFrameTimeSec;
        var halfFrameSec = FrameTiming.VisualDeltaSec * 0.5;

        // After a stall (or the first frame) the schedule restarts from now instead of catching up in a burst.
        if (_nextSendTimeSec == 0 || frameTimeSec - _nextSendTimeSec > periodSec)
            _nextSendTimeSec = frameTimeSec;

        if (frameTimeSec + halfFrameSec < _nextSendTimeSec)
            return false;

        _nextSendTimeSec += periodSec;
        return true;
    }

    /// <summary>With no receiver connected, capture a frame this often so a late connection is picked up quickly.</summary>
    private bool IsDueForIdleProbe()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastIdleProbeMs < IdleProbeIntervalMs)
            return false;

        _lastIdleProbeMs = now;
        return true;
    }

    /** A slot neither queued for readback nor holding a frame the worker still sends. */
    private int FindFreeSlot()
    {
        for (var i = 0; i < _stagingTextures.Count; i++)
        {
            if (!_queuedSlots.Contains(i))
                return i;
        }

        return -1;
    }

    private void Enqueue(PendingFrame frame)
    {
        lock (_queueLock)
        {
            // Live video: a frame that waited behind two others is stale, so it is dropped rather than sent late.
            while (_queue.Count >= MaxQueuedFrames)
            {
                var stale = _queue.Dequeue();
                ArrayPool<byte>.Shared.Return(stale.Buffer);
            }

            _queue.Enqueue(frame);
        }

        _frameAvailable.Set();
    }

    private void StartWorker()
    {
        if (_worker != null)
            return;

        _exit = false;
        _worker = new Thread(WorkerProc)
                      {
                          IsBackground = true,
                          Name = "OmtStreamSender",
                          Priority = ThreadPriority.AboveNormal,
                      };
        _worker.Start();
    }

    private void StopWorker()
    {
        if (_worker == null)
            return;

        _exit = true;
        _frameAvailable.Set();
        _worker.Join(TimeSpan.FromSeconds(2));
        _worker = null;

        lock (_queueLock)
        {
            while (_queue.Count > 0)
                ArrayPool<byte>.Shared.Return(_queue.Dequeue().Buffer);
        }
    }

    private void WorkerProc()
    {
        while (!_exit)
        {
            _frameAvailable.WaitOne(100);

            PendingFrame? pending = null;
            lock (_queueLock)
            {
                if (_queue.Count > 0)
                    pending = _queue.Dequeue();
            }

            if (pending == null)
                continue;

            try
            {
                SendFrame(pending);
            }
            catch (Exception e)
            {
                _lastError = $"OMT send failed: {e.Message}";
                Log.Debug(e.ToString(), this);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pending.Buffer);
            }
        }
    }

    private void SendFrame(PendingFrame pending)
    {
        var handle = GCHandle.Alloc(pending.Buffer, GCHandleType.Pinned);
        try
        {
            var frame = new OMTMediaFrame
                            {
                                Type = OMTFrameType.Video,
                                Codec = (int)(pending.PacksToUyvy ? OMTCodec.UYVY : OMTCodec.BGRA),
                                Width = pending.Width,
                                Height = pending.Height,
                                Stride = pending.Stride,
                                FrameRateN = Math.Max(1, FrameRate),
                                FrameRateD = 1,
                                AspectRatio = (float)pending.Width / pending.Height,
                                ColorSpace = pending.Height >= 720 ? OMTColorSpace.BT709 : OMTColorSpace.BT601,
                                Flags = pending.HasAlpha ? OMTVideoFlags.Alpha : OMTVideoFlags.None,

                                // -1 would make OMT pace the caller to the frame rate, which the render loop already does.
                                Timestamp = pending.Timestamp,
                                Data = handle.AddrOfPinnedObject(),
                                DataLength = pending.Length,
                            };

            _sender?.Send(frame);
        }
        finally
        {
            handle.Free();
        }
    }

    private void Release()
    {
        if (_sender != null)
        {
            try
            {
                _sender.Dispose();
            }
            catch (Exception e)
            {
                Log.Debug($"Disposing the OMT sender threw: {e.Message}", this);
            }

            _sender = null;
            _openedName = null;
        }

        ReleasePipeline();
    }

    private void ReleasePipeline()
    {
        _queuedSlots.Clear();
        foreach (var stagingTexture in _stagingTextures)
            stagingTexture.Dispose();

        _stagingTextures.Clear();
        T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _packedUav);
        T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _packedTexture);
    }

    /// <summary>
    /// OMT derives the discoverable name from the machine name, so a source name carrying the separator or a
    /// newline would make the source undiscoverable on the network.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var sanitized = OMTAddress.SanitizeName(name ?? string.Empty);
        return string.IsNullOrWhiteSpace(sanitized) ? "TiXL" : sanitized;
    }

    private static bool IsSupported(Format format)
    {
        return format is Format.B8G8R8A8_UNorm or Format.R8G8B8A8_UNorm or Format.B8G8R8A8_Typeless or Format.R8G8B8A8_Typeless;
    }

    private sealed record PendingFrame(byte[] Buffer, int Length, int Stride, int Width, int Height, bool HasAlpha, long Timestamp)
    {
        public bool PacksToUyvy => !HasAlpha;
    }

    /** Frames read back but not sent yet; older ones are dropped, since a late frame is worse than a missing one. */
    private const int MaxQueuedFrames = 2;

    /** Copies between writing a staging texture and reading it: enough for the GPU to be done with it. */
    private const int LatencyInFrames = 2;

    /** Queued copies plus the one being written. */
    private const int SlotCount = LatencyInFrames + 1;

    private const int MinimumDimension = 16;

    /** How long to keep capturing frames while no receiver is connected. */
    private const int IdleProbeIntervalMs = 250;

    private const string PackShaderAddress = "Lib:shaders/img/rgba-to-uyvy-cs.hlsl";

    private readonly Lock _queueLock = new();
    private readonly Queue<PendingFrame> _queue = new();
    private readonly AutoResetEvent _frameAvailable = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _worker;
    private volatile bool _exit;

    private OMTSend? _sender;
    private string _requestedName;
    private string? _openedName;
    private string? _lastError;

    private int _width;
    private int _height;
    private int _packedWidth;
    private bool _packsToUyvy;
    private Format _sourceFormat;

    private Texture2D? _packedTexture;
    private UnorderedAccessView? _packedUav;
    private Resource<ComputeShader>? _packShaderResource;

    private readonly List<Texture2D> _stagingTextures = [];
    private readonly Queue<int> _queuedSlots = new();

    /** Visual-clock time the next frame is due at; 0 restarts the schedule. */
    private double _nextSendTimeSec;

    private long _lastIdleProbeMs;
}
