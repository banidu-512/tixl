#nullable enable
using System.Threading;
using Sdcb.FFmpeg.Utils;
using T3.Core.Video;
using T3.Graphics.Compat;
using T3.VideoServices;

namespace Lib.io.video;

/// <summary>
/// Captures a webcam or capture card into a texture. Devices are opened through FFmpeg — DirectShow on
/// Windows, V4L2 on Linux — with software decode, mirroring <see cref="VideoStreamInput"/>. This replaces
/// the OpenCV/DirectShowLib capture, which crashed in OpenCV's native initialization and could not run on
/// Linux at all.
/// </summary>
[Guid("cd5a182e-254b-4e65-820b-ff754122614c")]
public class VideoDeviceInput : Instance<VideoDeviceInput>, ICustomDropdownHolder, IStatusProvider
{
    // Output slots
    [Output(Guid = "1d0159cc-33d2-46b1-9c0c-7054aa560df5", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> Texture = new();

    [Output(Guid = "9C2E4C11-09B6-4F4D-8F99-4A7372D5F2B5", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Int2> Resolution = new();

    [Output(Guid = "A1B2C3D4-5678-90EF-1234-567890ABCDEF", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public new readonly Slot<string> Status = new();

    [Output(Guid = "868D5FFE-032C-4522-B56B-D96B30841DB7", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    private const float Epsilon = 1e-4f;

    private readonly Lock _lockObject = new();

    [Input(Guid = "236D4C5C-0022-4416-A22C-D6DF73C306E2")]
    public readonly InputSlot<bool> Active = new();

    [Input(Guid = "3022DE8A-5D88-4A37-9799-780F2A838A6E")]
    public readonly InputSlot<float> ApplyRotationData = new();

    /// <summary>Brightness in device units; applied to the camera when the value changes.</summary>
    [Input(Guid = "D7A2F851-4C93-46B8-8E71-3B95C6E2D740")]
    public readonly InputSlot<float> Brightness = new();

    [Input(Guid = "11F7432D-31F1-44B9-8F75-B1569B314B13")]
    public readonly InputSlot<int> CustomFps = new();

    [Input(Guid = "C9E1C1F6-3A18-4A1C-8A5E-4B4119965B6E")]
    public readonly InputSlot<Int2> CustomResolution = new();

    [Input(Guid = "22513B82-E77A-417A-8A46-24E677F072D4")]
    public readonly InputSlot<bool> DeactivateWhenNotShowing = new();

    [Input(Guid = "A5F4D2B8-63E9-4C17-8D40-52B7E9C6F183")]
    public readonly InputSlot<float> Focus = new();

    [Input(Guid = "84B6E3C9-1A52-4F7D-9E28-67D3F1A5B496")]
    public readonly InputSlot<float> Exposure = new();

    [Input(Guid = "A57E815D-70C9-4D3B-998C-D13506B8F56E")]
    public readonly InputSlot<bool> FlipHorizontally = new();

    [Input(Guid = "3022DE8A-5D88-4A37-9799-780F2A838A5F")]
    public readonly InputSlot<bool> FlipVertically = new();

    [Input(Guid = "f5b900ec-ee17-123e-9972-cdd0580c104e")]
    public readonly InputSlot<string> InputDeviceName = new();

    [Input(Guid = "8D2C28C7-1234-40E2-9388-75574519543D")]
    public readonly InputSlot<bool> OpenSettings = new();

    [Input(Guid = "D91C4A7E-5B36-4E82-9F10-7C64A2D8E513")]
    public readonly InputSlot<float> Pan = new();

    [Input(Guid = "67E149A5-F7B6-47BC-B147-3B9B11C19C29")]
    public readonly InputSlot<bool> Reconnect = new();

    [Input(Guid = "F187A997-7E4A-48C6-81F9-2A27F150A68A")]
    public readonly InputSlot<Vector2> Reposition = new();

    [Input(Guid = "49019D29-873E-4B7C-A897-C575A384A650", MappedType = typeof(ResolutionFpsTypeEnum))]
    public readonly InputSlot<int> ResolutionFpsType = new();

    [Input(Guid = "805602D5-52B2-4A73-A337-12E00C3C91F2")]
    public readonly InputSlot<Vector2> Scale = new();

    [Input(Guid = "2E75B9C3-8D41-4A6F-B520-93E78F1C4A62")]
    public readonly InputSlot<float> Tilt = new();

    [Input(Guid = "6C38A1F4-92D7-4B5E-8C63-1F94B6D3A758")]
    public readonly InputSlot<float> Zoom = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _captureThread;

    // Capture threads whose join timed out. They observe cancellation and exit on their own; the references
    // keep their token sources alive and let a restart wait for the device to be released.
    private readonly List<Thread> _orphanedThreads = [];

    // Instance variables
    private Texture2D? _gpuTexture;
    private int _height;
    private volatile string _lastStatusMessage = "";
    private int _storeDeviceIndex = -1;
    private int _width;

    // The most recent frame as packed RGBA, shared between the capture thread and the render thread.
    private byte[]? _sharedRgba;
    private int _sharedWidth;
    private int _sharedHeight;
    private bool _hasFrame;
    private string? _lastLoggedOpenError;

    // Frame handoff bookkeeping: the capture thread bumps the counter under the lock; the eval thread
    // uploads only when it sees a counter it has not uploaded yet.
    private int _sharedFrameCounter;
    private int _uploadedFrameCounter;
    private byte[]? _uploadBuffer;

    public VideoDeviceInput()
    {
        _basicConfigs =
            [
                (Pan, CameraControl.Pan),
                (Tilt, CameraControl.Tilt),
                (Zoom, CameraControl.Zoom),
                (Focus, CameraControl.Focus),
                (Exposure, CameraControl.Exposure),
                (Brightness, CameraControl.Brightness),
            ];

        Texture.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
        Resolution.UpdateAction = Update;
        Status.UpdateAction = Update;
    }

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return _lastStatusMessage.StartsWith("Error") ? IStatusProvider.StatusLevel.Error
                   : _lastStatusMessage.StartsWith("Warning") ? IStatusProvider.StatusLevel.Warning
                   : IStatusProvider.StatusLevel.Success;
    }

    public string GetStatusMessage()
    {
        return _lastStatusMessage;
    }

    private void SetStatus(string message)
    {
        _lastStatusMessage = message;
    }

    private void Update(EvaluationContext context)
    {
        WebcamScan.Scan();

        var deviceName = InputDeviceName.GetValue(context);
        if (string.IsNullOrEmpty(deviceName) && WebcamScan.Devices.Count > 0)
        {
            deviceName = WebcamScan.Devices[0].Name;
        }

        var reconnectTriggered = Reconnect.GetValue(context);
        if (reconnectTriggered)
        {
            Reconnect.SetTypedInputValue(false);

            // The device scan is cached once per session; a reconnect is also the chance to pick up a
            // camera that was plugged in after startup.
            WebcamScan.Invalidate();
            CameraControls.InvalidateCache();
            WebcamScan.Scan();
        }

        if (!WebcamScan.TryGetIndexForName(deviceName, out var selectedDeviceIndex))
        {
            SetStatus(WebcamScan.Devices.Count > 0
                          ? $"Error: Camera '{deviceName}' not found."
                          : "Error: No capture devices found.");
            StopCaptureThread();
        }
        else
        {
            if (_lastSeenDeviceIndex != selectedDeviceIndex)
            {
                // A different camera: forget what was applied and send the configured values to it once.
                // (Tracked separately from _storeDeviceIndex, which only moves when capture actually
                // starts — the re-apply must also work while the capture is idle.)
                var hadPreviousDevice = _lastSeenDeviceIndex != -1;
                _lastSeenDeviceIndex = selectedDeviceIndex;
                for (var i = 0; i < _lastAppliedConfigValues.Length; i++)
                    _lastAppliedConfigValues[i] = float.NaN;
                if (hadPreviousDevice)
                    _forceConfigApply = true;
            }

            ApplyBasicConfigs(context, selectedDeviceIndex);

            var deviceIsActive = Active.GetValue(context);
            var deactivateWhenNotShowing = DeactivateWhenNotShowing.GetValue(context);

            bool shouldBeCapturing = deviceIsActive && (!deactivateWhenNotShowing || IsActiveInGraph());

            if (shouldBeCapturing)
            {
                if (IsCaptureThreadRunning() && (selectedDeviceIndex != _storeDeviceIndex || reconnectTriggered))
                {
                    StopCaptureThread();
                }

                if (!IsCaptureThreadRunning())
                {
                    _storeDeviceIndex = selectedDeviceIndex;
                    StartCaptureThread(context);
                }
            }
            else
            {
                StopCaptureThread();
            }
        }

        if (OpenSettings.GetValue(context))
        {
            OpenVideoSettings();
            OpenSettings.SetTypedInputValue(false);
        }

        // New frame? The camera delivers ~30 fps while the editor evaluates ~60 — uploading every update
        // would send each frame twice. The handoff copies under the lock (cheap memcpy) so the GPU work
        // below never blocks the capture thread mid-copy.
        var hasNewFrame = false;
        var frameWidth = 0;
        var frameHeight = 0;
        lock (_lockObject)
        {
            if (_hasFrame && _sharedRgba != null && _sharedFrameCounter != _uploadedFrameCounter)
            {
                var byteCount = _sharedWidth * _sharedHeight * 4;
                if (_uploadBuffer == null || _uploadBuffer.Length < byteCount)
                    _uploadBuffer = new byte[byteCount];

                Array.Copy(_sharedRgba, _uploadBuffer, byteCount);
                frameWidth = _sharedWidth;
                frameHeight = _sharedHeight;
                _uploadedFrameCounter = _sharedFrameCounter;
                hasNewFrame = true;
            }
        }

        if (hasNewFrame)
        {
            UploadFrameToGpu(_uploadBuffer!, frameWidth, frameHeight);
            Texture.Value = _gpuTexture!;
            UpdateCount.Value++;
            Resolution.Value = new Int2(frameWidth, frameHeight);
        }

        Status.Value = _lastStatusMessage;
    }

    private bool IsCaptureThreadRunning()
    {
        return _captureThread != null && _captureThread.IsAlive;
    }

    // The basic hardware configs, applied automatically whenever their input changes. Each entry remembers
    // the last value sent to the device (NaN = not sent yet), so unchanged inputs never touch the camera —
    // including at graph load, until a value is actually edited or patched. All entries are independent, so
    // several settings can be configured at once. Initialized in the constructor (references inputs).
    private readonly (InputSlot<float> Input, CameraControl Control)[] _basicConfigs = null!;

    private readonly float[] _lastAppliedConfigValues =
        [float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN];

    // Set when the device changed: the next ApplyBasicConfigs sends the configured values even though they
    // are fresh baselines for the new camera.
    private bool _forceConfigApply;
    private int _lastSeenDeviceIndex = -1;

    private void ApplyBasicConfigs(EvaluationContext context, int deviceIndex)
    {
        var forceApply = _forceConfigApply;
        _forceConfigApply = false;

        for (var i = 0; i < _basicConfigs.Length; i++)
        {
            // GetValue (not .Value): the input's slot only picks up edited/patched values when evaluated.
            var value = _basicConfigs[i].Input.GetValue(context);
            if (float.IsNaN(value))
                continue;

            // The first sighting is the baseline, not a command — untouched inputs never force anything
            // onto the camera (and graph-load defaults like Focus=0 stay harmless).
            var last = _lastAppliedConfigValues[i];
            if (float.IsNaN(last) && !forceApply)
            {
                _lastAppliedConfigValues[i] = value;
                continue;
            }

            if (value == last)
                continue;

            var control = _basicConfigs[i].Control;
            if (CameraControls.IsSupported(deviceIndex, control, out var range))
            {
                var requestedValue = (int)MathF.Round(value);
                if (CameraControls.TrySetValue(deviceIndex, control, requestedValue))
                {
                    var actualValue = CameraControls.TryGetValue(deviceIndex, control, out var readBack) ? readBack : requestedValue;
                    var clampedNote = actualValue != requestedValue ? $" (device clamped to {actualValue})" : "";
                    var rangeNote = CameraControls.IsRangeEstimated(deviceIndex, control) ? " — device did not report a range" : "";
                    SetStatus($"{control} set to {requestedValue}{clampedNote}. Range {range.Minimum}..{range.Maximum}, step {range.Step}{rangeNote}.");
                }
                else
                {
                    SetStatus($"Warning: Failed to set {control} to {requestedValue}.");
                }
            }
            else
            {
                SetStatus($"Warning: Camera does not support {control}.");
            }

            _lastAppliedConfigValues[i] = value;
        }
    }

    private bool IsActiveInGraph() => true;

    private void StartCaptureThread(EvaluationContext context)
    {
        if (IsCaptureThreadRunning()) return;

        // A timed-out capture thread from a previous stop may still hold the device. Wait for it to wind
        // down (it observes cancellation within ~100 ms) so two threads never fight over one camera.
        foreach (var orphan in _orphanedThreads)
        {
            var waited = 0;
            while (orphan.IsAlive && waited < 2000)
            {
                Thread.Sleep(50);
                waited += 50;
            }
        }
        _orphanedThreads.RemoveAll(t => !t.IsAlive);

        var device = WebcamScan.Devices.FirstOrDefault(d => d.Index == _storeDeviceIndex);
        if (device == null)
            return;

        var settings = new CaptureThreadSettings(this, context);
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _captureThread = new Thread(() => CaptureLoop(settings, device, token))
                         {
                             IsBackground = true,
                             Name = "Video Capture Thread",
                             Priority = ThreadPriority.BelowNormal
                         };
        _captureThread.Start();
    }

    private void StopCaptureThread()
    {
        if (!IsCaptureThreadRunning()) return;

        _cancellationTokenSource?.Cancel();

        if (!_captureThread!.Join(TimeSpan.FromSeconds(2)))
        {
            // The thread is stuck somewhere slow (device teardown, blocked driver call). It observes
            // cancellation on its own and exits; keep its token source alive until then — disposing it
            // under a running thread is the one way this op could ever take the editor down.
            Log.Debug("Video capture thread did not exit in time; leaving it to finish on its own.");
            _orphanedThreads.Add(_captureThread);
        }
        else
        {
            _cancellationTokenSource?.Dispose();
        }

        _captureThread = null;
        _cancellationTokenSource = null;
    }

    private void CaptureLoop(CaptureThreadSettings settings, WebcamWithIndex device, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var shouldRetry = TryCaptureFromDevice(settings, device, token);
            if (!shouldRetry)
                return;

            // The device never opened or died mid-capture (unplugged, driver hiccup). The old OpenCV
            // implementation gave up here and needed a manual reconnect; retrying keeps live sessions alive.
            SetStatus(_lastStatusMessage + " Retrying...");
            // Poll instead of WaitOne: unlike a wait handle, reading the token is safe even if its source
            // got disposed on the eval thread while this thread was busy.
            for (var waited = 0; waited < 1000 && !token.IsCancellationRequested; waited += 100)
                Thread.Sleep(100);
        }
    }

    /// <summary>Returns true when the device should be retried (open/read failure); false when done.</summary>
    private bool TryCaptureFromDevice(CaptureThreadSettings settings, WebcamWithIndex device, CancellationToken token)
    {
        VideoDecoderSession? session = null;
        SoftwareFrameConverter? converter = null;
        try
        {
            session = TryOpenDevice(settings, device, out var customFormatAccepted, out var error);
            if (session == null)
            {
                // Another capture op or application streaming the camera is the common cause of a busy
                // rejection — say so, or the retry loop reads like a mystery.
                if (error != null && (error.Contains("-16") || error.Contains("busy", StringComparison.OrdinalIgnoreCase)))
                    error += " — the camera is in use by another capture op or application.";

                SetStatus($"Error: Failed to open camera '{device.Name}'. {error}");
                // The retry loop can run for a long time (device unplugged); log only state changes.
                if (_lastLoggedOpenError != error)
                {
                    _lastLoggedOpenError = error;
                    Log.Warning($"VideoDeviceInput: Could not open '{device.Name}': {error}");
                }

                return true;
            }
            _lastLoggedOpenError = null;

            // Capture devices deliver SDR; forcing RGBA8 keeps flips/warp and the texture upload on one
            // well-defined 4-byte layout.
            converter = new SoftwareFrameConverter(hdr: false);

            var requestedWidth = settings.CustomResolution.X;
            var requestedHeight = settings.CustomResolution.Y;
            // The Custom inputs default to 0 — an unset part means "no preference", not a 0x0@0fps request.
            var hasRequestedSize = requestedWidth > 0 && requestedHeight > 0;
            var hasRequestedFps = settings.CustomFps > 0;
            var fpsText = session.FrameRate > 0 ? $"@{session.FrameRate:F2}fps" : "";

            if (settings.ResolutionFpsType == ResolutionFpsTypeEnum.Custom && (hasRequestedSize || hasRequestedFps))
            {
                if (!hasRequestedSize || (session.Width == requestedWidth && session.Height == requestedHeight))
                {
                    SetStatus($"Success: Running at {session.Width}x{session.Height}{fpsText}" +
                              (hasRequestedFps ? $" (requested {requestedWidth}x{requestedHeight}@{settings.CustomFps}fps)."
                                               : " (requested device default)."));
                }
                else
                {
                    SetStatus($"Warning: Using {session.Width}x{session.Height}{fpsText} " +
                              $"(requested {requestedWidth}x{requestedHeight}@{settings.CustomFps}fps). (Software resize)");
                }
            }
            else
            {
                SetStatus($"Running at {session.Width}x{session.Height}{fpsText} (device default).");
            }

            // When the device didn't take the requested size, swscale resizes each frame during conversion.
            var wantsResize = settings.ResolutionFpsType == ResolutionFpsTypeEnum.Custom
                              && hasRequestedSize
                              && (session.Width != requestedWidth || session.Height != requestedHeight);
            var targetWidth = wantsResize ? requestedWidth : session.Width;
            var targetHeight = wantsResize ? requestedHeight : session.Height;

            while (!token.IsCancellationRequested)
            {
                if (!session.TryReadNextFrame(out _))
                {
                    SetStatus($"Error: Lost camera '{device.Name}'.");
                    return true;
                }

                var rgba = converter.Convert(session.CurrentFrame, targetWidth, targetHeight);
                var byteCount = rgba.Width * rgba.Height * converter.BytesPerPixel;

                lock (_lockObject)
                {
                    if (_sharedRgba == null || _sharedRgba.Length < byteCount)
                        _sharedRgba = new byte[byteCount];

                    CopyTransformedPixels(rgba, converter.BytesPerPixel, settings, _sharedRgba);
                    _sharedWidth = rgba.Width;
                    _sharedHeight = rgba.Height;
                    _hasFrame = true;
                    _sharedFrameCounter++;
                }

                // Only rewrite on transition — keep the last notable event visible instead of
                // stomping it 30x per second with a static message.
                if (_lastStatusMessage != "Running")
                    SetStatus("Running");
            }

            return false;
        }
        catch (Exception e)
        {
            SetStatus($"Error: Capture failed - {e.Message}");
            Log.Debug($"Video capture thread failed: {e}");
            return true;
        }
        finally
        {
            converter?.Dispose();
            session?.Dispose();
        }
    }

    private static VideoDecoderSession? TryOpenDevice(CaptureThreadSettings settings, WebcamWithIndex device,
                                                      out bool customFormatAccepted, out string? error)
    {
        customFormatAccepted = false;
        var url = BuildCaptureUrl(device, out var inputFormat);

        if (settings.ResolutionFpsType == ResolutionFpsTypeEnum.Custom)
        {
            var session = VideoDecoderSession.TryOpen(url, VideoPlaybackOptimization.FastSeeking, out error,
                                                       BuildDemuxerOptions(settings, includeFormatSettings: true), inputFormat,
                                                       lowLatency: true);
            if (session != null)
            {
                customFormatAccepted = true;
                return session;
            }

            // V4L2 cameras commonly offer their higher resolutions only as MJPEG; YUYV is bandwidth-limited.
            if (!OperatingSystem.IsWindows())
            {
                var mjpegOptions = BuildDemuxerOptions(settings, includeFormatSettings: true)!;
                mjpegOptions["input_format"] = "mjpeg";
                session = VideoDecoderSession.TryOpen(url, VideoPlaybackOptimization.FastSeeking, out error, mjpegOptions, inputFormat,
                                                      lowLatency: true);
                if (session != null)
                {
                    customFormatAccepted = true;
                    return session;
                }
            }
        }

        // Device-default format — when a custom size was requested it is resized in software instead.
        return VideoDecoderSession.TryOpen(url, VideoPlaybackOptimization.FastSeeking, out error,
                                           BuildDemuxerOptions(settings, includeFormatSettings: false), inputFormat,
                                           lowLatency: true);
    }

    private static string BuildCaptureUrl(WebcamWithIndex device, out string inputFormat)
    {
        if (OperatingSystem.IsWindows())
        {
            inputFormat = "dshow";
            return $"video={device.Name}";
        }

        inputFormat = "v4l2";
        return $"/dev/video{device.Index}";
    }

    private static Dictionary<string, string>? BuildDemuxerOptions(CaptureThreadSettings settings, bool includeFormatSettings)
    {
        if (!includeFormatSettings || settings.ResolutionFpsType != ResolutionFpsTypeEnum.Custom)
            return null;

        // Omit unset parts (0) instead of requesting 0x0@0 — the demuxer rejects those outright.
        var options = new Dictionary<string, string>();
        var (width, height) = (settings.CustomResolution.X, settings.CustomResolution.Y);
        if (width > 0 && height > 0)
            options["video_size"] = $"{width}x{height}";
        if (settings.CustomFps > 0)
            options["framerate"] = settings.CustomFps.ToString();

        return options.Count > 0 ? options : null;
    }

    #region Frame transforms (replaces the OpenCV flip/resize/warp pipeline)

    /// <summary>
    /// Copies the converted frame into the shared buffer, applying flips and — when a rotation/scale/
    /// reposition transform is active — the affine warp. Resizing is handled upstream by swscale.
    /// </summary>
    private static unsafe void CopyTransformedPixels(Frame rgba, int bytesPerPixel, CaptureThreadSettings settings, byte[] destination)
    {
        var stride = rgba.Linesize[0];
        var source = (byte*)rgba.Data[0];
        var rowBytes = rgba.Width * bytesPerPixel;

        if (settings.HasTransformation() && bytesPerPixel == 4)
        {
            WarpAffine(source, stride, rgba.Width, rgba.Height, settings, destination);
            return;
        }

        fixed (byte* target = destination)
        {
            if (stride == rowBytes && !settings.FlipHorizontally && !settings.FlipVertically)
            {
                var total = (long)rowBytes * rgba.Height;
                System.Buffer.MemoryCopy(source, target, total, total);
                return;
            }

            for (var y = 0; y < rgba.Height; y++)
            {
                var srcRow = source + (long)(settings.FlipVertically ? rgba.Height - 1 - y : y) * stride;
                var dstRow = target + (long)y * rowBytes;

                if (!settings.FlipHorizontally)
                {
                    System.Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
                    continue;
                }

                // RGBA pixels are 4-byte aligned — reverse as uints instead of per-byte (4x fewer loops).
                if (bytesPerPixel == 4)
                {
                    var src32 = (uint*)srcRow;
                    var dst32 = (uint*)dstRow;
                    for (var x = 0; x < rgba.Width; x++)
                        dst32[x] = src32[rgba.Width - 1 - x];
                    continue;
                }

                for (var x = 0; x < rgba.Width; x++)
                {
                    var s = srcRow + (rgba.Width - 1 - x) * bytesPerPixel;
                    var d = dstRow + (long)x * bytesPerPixel;
                    for (var b = 0; b < bytesPerPixel; b++)
                        d[b] = s[b];
                }
            }
        }
    }

    /// <summary>
    /// Bilinear affine warp with the same destination-to-source matrix the old OpenCV implementation built
    /// (GetRotationMatrix2D-style with separate X/Y scale, plus reposition). The flips are composed in as
    /// coordinate inversions — they used to run as a separate pass before the warp, which this matches.
    /// Samples outside the source read as transparent black, like OpenCV's constant border. Pure managed
    /// code, so it only runs when a transform is actually requested; the identity path stays a memcpy.
    /// </summary>
    private static unsafe void WarpAffine(byte* source, int stride, int width, int height,
                                          CaptureThreadSettings settings, byte[] destination)
    {
        var centerX = width / 2f;
        var centerY = height / 2f;
        var angleRad = settings.Rotation * Math.PI / 180.0;
        var cos = (float)Math.Cos(angleRad);
        var sin = (float)Math.Sin(angleRad);

        var m00 = cos * settings.Scale.X;
        var m01 = sin * settings.Scale.Y;
        var m10 = -sin * settings.Scale.X;
        var m11 = cos * settings.Scale.Y;
        var m02 = settings.Reposition.X + (1 - m00) * centerX - m01 * centerY;
        var m12 = settings.Reposition.Y + (1 - m11) * centerY - m10 * centerX;

        var flipHorizontally = settings.FlipHorizontally;
        var flipVertically = settings.FlipVertically;

        fixed (byte* target = destination)
        {
            var dstRow = target;
            for (var y = 0; y < height; y++, dstRow += width * 4)
            {
                var baseU = m01 * y + m02;
                var baseV = m11 * y + m12;
                var dstPixel = dstRow;
                for (var x = 0; x < width; x++, dstPixel += 4)
                {
                    var u = baseU + m00 * x;
                    var v = baseV + m10 * x;
                    if (flipHorizontally) u = width - 1 - u;
                    if (flipVertically) v = height - 1 - v;

                    if (u < 0 || v < 0 || u > width - 1 || v > height - 1)
                    {
                        *(uint*)dstPixel = 0;
                        continue;
                    }

                    var x0 = (int)u;
                    var y0 = (int)v;
                    var x1 = x0 + 1 < width ? x0 + 1 : x0;
                    var y1 = y0 + 1 < height ? y0 + 1 : y0;
                    var fx = u - x0;
                    var fy = v - y0;
                    var w00 = (1 - fx) * (1 - fy);
                    var w10 = fx * (1 - fy);
                    var w01 = (1 - fx) * fy;
                    var w11 = fx * fy;

                    var p00 = source + (long)y0 * stride + (long)x0 * 4;
                    var p10 = source + (long)y0 * stride + (long)x1 * 4;
                    var p01 = source + (long)y1 * stride + (long)x0 * 4;
                    var p11 = source + (long)y1 * stride + (long)x1 * 4;

                    for (var c = 0; c < 4; c++)
                    {
                        var value = p00[c] * w00 + p10[c] * w10 + p01[c] * w01 + p11[c] * w11;
                        dstPixel[c] = (byte)(value + 0.5f);
                    }
                }
            }
        }
    }

    #endregion

    private unsafe void UploadFrameToGpu(byte[] buffer, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        if (_gpuTexture == null || _width != width || _height != height)
        {
            T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _gpuTexture);
            _width = width;
            _height = height;

            _gpuTexture = Texture2D.CreateTexture2D(new Texture2DDescription
                                                       {
                                                           Width = width,
                                                           Height = height,
                                                           MipLevels = 1,
                                                           ArraySize = 1,
                                                           Format = Format.R8G8B8A8_UNorm,
                                                           SampleDescription = new SampleDescription(1, 0),
                                                           Usage = ResourceUsage.Default,
                                                           BindFlags = BindFlags.ShaderResource,
                                                           CpuAccessFlags = CpuAccessFlags.None,
                                                           OptionFlags = ResourceOptionFlags.None
                                                       });
        }

        fixed (byte* pixels = buffer)
        {
            var dataBox = new DataBox((IntPtr)pixels, width * 4, 0);
            ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _gpuTexture, 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
            return;

        if (!disposing)
            return;

        StopCaptureThread();
        T3.Graphics.Compat.GraphicsUtilities.Dispose(ref _gpuTexture);

        lock (_lockObject)
        {
            _sharedRgba = null;
            _hasFrame = false;
        }
    }

    private sealed record CaptureThreadSettings
    {
        public readonly int CustomFps;
        public readonly Int2 CustomResolution;
        public readonly bool FlipHorizontally;
        public readonly bool FlipVertically;
        public readonly Vector2 Reposition;
        public readonly ResolutionFpsTypeEnum ResolutionFpsType;
        public readonly float Rotation;
        public readonly Vector2 Scale;

        public CaptureThreadSettings(VideoDeviceInput owner, EvaluationContext context)
        {
            ResolutionFpsType = (ResolutionFpsTypeEnum)owner.ResolutionFpsType.GetValue(context);
            CustomResolution = owner.CustomResolution.GetValue(context);
            CustomFps = owner.CustomFps.GetValue(context);
            FlipVertically = owner.FlipVertically.GetValue(context);
            FlipHorizontally = owner.FlipHorizontally.GetValue(context);
            Rotation = owner.ApplyRotationData.GetValue(context);
            Reposition = owner.Reposition.GetValue(context);
            Scale = owner.Scale.GetValue(context);
        }

        public bool HasTransformation()
        {
            return Math.Abs(Rotation) > Epsilon ||
                   Math.Abs(Scale.X - 1.0f) > Epsilon || Math.Abs(Scale.Y - 1.0f) > Epsilon ||
                   Math.Abs(Reposition.X) > Epsilon || Math.Abs(Reposition.Y) > Epsilon;
        }
    }

    private enum ResolutionFpsTypeEnum
    {
        DeviceDefault = 0,
        Custom = 1
    }

    #region Device Configuration UI
    private void OpenVideoSettings()
    {
        // The DirectShow property-page dialog went away with the OpenCV backend; FFmpeg's dshow/v4l2
        // demuxers expose no driver-settings UI.
        SetStatus("Warning: Device settings dialogs are not supported by the FFmpeg capture backend.");
    }

    public void OpenCrossbarSettings()
    {
        Log.Debug("Crossbar settings not implemented for this version.");
    }
    #endregion

    #region Dropdown UI
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == InputDeviceName.Id) return InputDeviceName.Value;
        if (inputId == CustomResolution.Id) return $"{CustomResolution.Value.X}x{CustomResolution.Value.Y}";
        if (inputId == CustomFps.Id) return CustomFps.Value.ToString();
        return string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == InputDeviceName.Id)
        {
            if (WebcamScan.Devices.Count == 0)
            {
                yield return "No devices found";
                yield break;
            }

            foreach (var (webcam, _) in WebcamScan.Devices)
                yield return webcam;
        }
        else if (inputId == CustomResolution.Id)
        {
            var deviceName = InputDeviceName.Value;
            foreach (var (w, h, _) in WebcamScan.GetCapabilities(deviceName).DistinctBy(c => (c.Width, c.Height)))
                yield return $"{w}x{h}";

            if (!WebcamScan.GetCapabilities(deviceName).Any())
                yield return "640x480";
        }
        else if (inputId == CustomFps.Id)
        {
            var deviceName = InputDeviceName.Value;
            foreach (var (_, _, fps) in WebcamScan.GetCapabilities(deviceName).DistinctBy(c => c.Fps))
                yield return $"{fps:F0}";

            if (!WebcamScan.GetCapabilities(deviceName).Any())
                yield return "30";
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (inputId == InputDeviceName.Id && selected != null)
            InputDeviceName.SetTypedInputValue(selected);
        else if (inputId == CustomResolution.Id)
        {
            if (selected != null)
            {
                var parts = selected.Split('x');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var w) &&
                    int.TryParse(parts[1], out var h))
                {
                    CustomResolution.SetTypedInputValue(new Int2(w, h));

                    var deviceName = InputDeviceName.Value;
                    var caps = WebcamScan.GetCapabilities(deviceName);
                    if (caps.Any() && !caps.Any(c => c.Width == w && c.Height == h))
                    {
                        SetStatus("Warning: Unsupported resolution—may fallback.");
                    }
                }
            }
        }
        else if (inputId == CustomFps.Id)
        {
            if (double.TryParse(selected, out var fps))
            {
                CustomFps.SetTypedInputValue((int)fps);
            }
        }
    }
    #endregion

}
