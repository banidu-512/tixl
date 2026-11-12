#pragma warning disable CA1416 // Platform compatibility warnings for Windows-specific APIs
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using T3.Core.Animation;
using T3.Core.DataTypes.Vector;
using T3.Core.Logging;
using T3.Graphics.Compat;
using T3.Core.Utils;
using T3.Core.Operator.Attributes;
using t3.streamdiffusion.Onnx;
using t3.streamdiffusion.V2;

namespace Lib.io.ai;

[Guid("9A7B3C8D-4E2F-5A6B-7C8D-9E0F1A2B3C4D")]
[ExportDependencies("Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "onnxruntime_providers_shared.dll",
    "onnxruntime_providers_cuda.dll", "onnxruntime_providers_tensorrt.dll",
    "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll", "cufft64_12.dll",
    "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "curand64_10.dll",
    "cudnn64_9.dll", "cudnn_*64_9.dll", "libonnxruntime*")]
public sealed class StreamDiffusion : Instance<StreamDiffusion>, IStatusProvider, ICustomDropdownHolder
{
    // Expose pipeline readiness for tests
    internal bool IsPipelineReady() => _pipeline != null;

    private enum InputPixelFormat
    {
        Rgba8,
        Bgra8,
        Rgba16F,
        Rgba32F,
    }

    private sealed record GenerationRequest(
        string Prompt,
        string? NegativePrompt,
        int Mode,
        int Steps,
        float Guidance,
        float Strength,
        int Seed,
        int Width,
        int Height,
        int ResizeMode,
        float PreserveDetails,
        byte[]? InputData,
        int InputWidth,
        int InputHeight,
        InputPixelFormat InputFormat);

    // Generation tags stale results from a replaced worker so they cannot be applied to the new model
    private sealed record GenerationResult(
        byte[]? Data,
        int Width,
        int Height,
        float Time,
        string? Error,
        int Generation = 0);

    // Immutable so the render thread can read the failure state while a worker updates it
    private sealed record FailureKey(string? Path, int Device, int Type, int Provider);

    #region Outputs

    [Output(Guid = "D8E2F9A1-7B3C-4D5E-8F9A-1B2C3D4E5F6A", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> Output = new();

    [Output(Guid = "E9F3A2B1-8C4D-5E6F-9A1B-2C3D4E5F6A7B")]
    public readonly Slot<Int2> OutputSize = new();

    [Output(Guid = "F1A4B3C2-9D5E-6F7A-0B2C-3D4E5F6A7B8C")]
    public readonly Slot<float> GenerationTime = new();

    #endregion

    #region Inputs

    [Input(Guid = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    public readonly InputSlot<string> EnginePath = new();

    [Input(Guid = "B2C3D4E5-F6A7-8901-BCDE-F12345678901", MappedType = typeof(ModeOptions))]
    public readonly InputSlot<int> Mode = new(0);

    [Input(Guid = "C3D4E5F6-A7B8-9012-CDEF-123456789012")]
    public readonly InputSlot<string> Prompt = new();

    [Input(Guid = "D4E5F6A7-B8C9-0123-DEF0-234567890123")]
    public readonly InputSlot<string> NegativePrompt = new();

    [Input(Guid = "E5F6A7B8-C9D0-1234-EF01-345678901234")]
    public readonly InputSlot<int> Seed = new(-1);

    [Input(Guid = "F6A7B8C9-D0E1-2345-F012-456789012345")]
    public readonly InputSlot<int> Width = new(512);

    [Input(Guid = "A7B8C9D0-E1F2-3456-0123-567890123456")]
    public readonly InputSlot<int> Height = new(512);

    [Input(Guid = "B8C9D0E1-F2A3-4567-1234-678901234567")]
    public readonly InputSlot<float> Guidance = new(1.0f);

    [Input(Guid = "C9D0E1F2-A3B4-5678-2345-789012345678")]
    public readonly InputSlot<float> Strength = new(0.8f);

    [Input(Guid = "D0E1F2A3-B4C5-6789-3456-890123456789")]
    public readonly InputSlot<int> Steps = new(1);

    [Input(Guid = "E1F2A3B4-C5D6-7890-4567-901234567890")]
    public readonly InputSlot<Texture2D?> InputImage = new();

    [Input(Guid = "F2A3B4C5-D6E7-8901-5678-012345678901", MappedType = typeof(ModelTypeOptions))]
    public readonly InputSlot<int> ModelType = new(1);

    [Input(Guid = "A3B4C5D6-E7F8-9012-6789-123456789012")]
    public readonly InputSlot<int> CudaDevice = new(0);

    [Input(Guid = "C7D8E9F0-A1B2-3456-7890-1234567890AB", MappedType = typeof(t3.streamdiffusion.Onnx.ExecutionProvider))]
    public readonly InputSlot<int> ExecutionProviderParam = new((int)t3.streamdiffusion.Onnx.ExecutionProvider.Cuda);

    [Input(Guid = "B4C5D6E7-F8A9-0123-7890-234567890123")]
    public readonly InputSlot<bool> TriggerGenerate = new();

    [Input(Guid = "C5D6E7F8-A9B0-1234-8901-345678901234")]
    public readonly InputSlot<bool> AutoGenerate = new();

    [Input(Guid = "D1E2F3A4-B5C6-7890-1234-567890123456")]
    public readonly InputSlot<bool> MatchInputSize = new(false);

    [Input(Guid = "E2F3A4B5-C6D7-8901-2345-678901234567", MappedType = typeof(ResizeModeOptions))]
    public readonly InputSlot<int> ResizeMode = new(0);

    [Input(Guid = "F3A4B5C6-D7E8-9012-3456-789012345678")]
    public readonly InputSlot<float> PreserveDetails = new(0.0f);

    /// <summary>
    /// Temporal feedback (like ossia's "Feed prev. output"): blends the newly generated frame with the
    /// previously published one. 0 keeps the pipeline output untouched; values up to 1 smooth out the
    /// frame-to-frame flicker of continuous restyle (trails decay exponentially). Ignored on the
    /// GPU-latent fast path (TIXL_SD_GPU_LATENTS=1), which publishes GPU-resident frames directly.
    /// </summary>
    [Input(Guid = "C9D0E1F2-A3B4-5678-9ABC-DEF012345678")]
    public readonly InputSlot<float> Feedback = new(0.0f);

    [Input(Guid = "D2E3F4A5-B6C7-8901-2345-678901234567")]
    public readonly InputSlot<bool> Debug = new(false);

    [Input(Guid = "A4B5C6D7-E8F9-0123-4567-890123456789")]
    public readonly InputSlot<float> StreamStrength = new(0.6f);

    /// <summary>V2 only: decode one latent instead of a three-latent window. Faster, with less temporal smoothing.</summary>
    [Input(Guid = "B7C8D9E0-F1A2-3456-7890-123456789012")]
    public readonly InputSlot<bool> FastDecode = new();

    #endregion

    #region Constants

    private const string PlaceholderTextModel = "Path to model folder...";
    private const string PlaceholderTextDevice = "Select device...";
    private const int ReadbackTimeoutFrames = 60;

    private static readonly string[] ModeNames = { "Text to Image", "Image to Image", "Stream" };
    private static readonly string[] ModelTypeNames = { "SD 1.5", "SD Turbo", "SDXL Turbo", "FLUX.2 Klein 4B", "SDXL 1.0", "StreamDiffusion V2" };
    private static readonly string[] ResizeModeNames = { "Stretch", "CenterCrop", "Pad" };
    private static readonly string[] ExecutionProviderNames = { "CPU", "DirectML", "CUDA", "TensorRT", "CoreML" };

    public enum ModeOptions { TextToImage, ImageToImage, Stream }
    public enum ModelTypeOptions { SD15, SDTurbo, SDXLTurbo, FLUX2Klein, SDXL10, StreamDiffusionV2 }
    public enum ResizeModeOptions { Stretch, CenterCrop, Pad }

    #endregion

    #region Fields

    // Worker-per-pipeline lifecycle (DepthAnything port)
    private volatile IGenerationPipeline? _pipeline;
    private readonly object _stateLock = new();
    private Task? _processingTask;
    private CancellationTokenSource? _workerCts;
    private volatile bool _isInitializing;
    private volatile bool _initFailed;
    private FailureKey? _failedInputs;
    private int _workerGeneration;
    private string? _activeModelPath;
    private int _activeDeviceId = -1;
    private int _activeModelType = -1;
    private int _activeProvider = -1;
    private readonly AutoResetEvent _workerWake = new(false);

    private readonly ConcurrentQueue<GenerationRequest> _inputQueue = new();
    private readonly ConcurrentQueue<GenerationResult> _outputQueue = new();
    private volatile bool _requestInFlight;
    private volatile bool _debugLogging;

    // Render-thread state
    private bool _hasPendingInput;
    private Texture2D? _outputTexture;
    private Int2 _lastOutputSize;
    private float _lastGenerationTime;
    private string? _lastStatusMessage;
    private string? _lastLoggedError;
    private int _lastHeartbeatFrame;
    private bool _triggerState;
    private int _updating;
    private int _lastSkipLogFrame;
    private int _updateFrameCounter;
    private bool _hasTxtFingerprint;
    private int _lastTxtFingerprint;

    // Deferred GPU readback - the map only runs after the copy query signaled,
    // so the render thread never waits for the GPU.
    private Texture2D? _readbackTexture;
    private bool _readbackPending;
    private InputPixelFormat _readbackFormat;
    private int _readbackWaitFrames;
    private bool _loggedUnsupportedFormat;

    // Linear -> encoded sRGB for float render-target inputs; a table keeps the per-pixel path cheap
    private const int SrgbTableSize = 4096;
    private static readonly byte[] SrgbEncodeTable = CreateSrgbEncodeTable();

    // Worker-side img2img/stream fingerprint (mode 1 and 2); only the current worker reads or writes these
    private int _lastProcessedFingerprint;
    private bool _hasProcessedFingerprint;

    // Staging texture + extract buffer pool
    private Texture2D? _stagingTexture;
    private readonly Stack<byte[]> _extractBufferPool = new();
    private readonly object _poolLock = new();

    // UI dropdown cache
    private int _uiMode;
    private int _uiModelType;
    private int _uiResizeMode;
    private int _uiExecutionProvider;

    #endregion

    public StreamDiffusion()
    {
        Output.UpdateAction += Update;
    }

    private static int AlignTo8(int value) => Math.Max(64, value / 8 * 8);

    private void Update(EvaluationContext context)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
            return;

        try
        {
            var modelPath = EnginePath.GetValue(context);
            var mode = Mode.GetValue(context);
            var prompt = Prompt.GetValue(context);
            var negativePrompt = NegativePrompt.GetValue(context);
            var seed = Seed.GetValue(context);
            var width = AlignTo8(Width.GetValue(context));
            var height = AlignTo8(Height.GetValue(context));
            var guidance = Guidance.GetValue(context);
            var strength = Strength.GetValue(context);
            var streamStrength = StreamStrength.GetValue(context);
            var steps = Steps.GetValue(context);
            var inputImage = InputImage.GetValue(context);
            var matchInputSize = MatchInputSize.GetValue(context);
            var resizeMode = ResizeMode.GetValue(context);
            var preserveDetails = PreserveDetails.GetValue(context);
            var modelType = ModelType.GetValue(context);
            var deviceId = CudaDevice.GetValue(context);
            var provider = ExecutionProviderParam.GetValue(context);

            // The AI settings' DirectML preference flips the default provider on Windows,
            // e.g. for AMD/Intel machines without a usable CUDA stack. An explicit
            // non-default choice is never overridden.
            if (provider == (int)t3.streamdiffusion.Onnx.ExecutionProvider.Cuda
                && T3.Core.Settings.AiModelSettings.Config.PreferDirectMlForOnnx
                && OperatingSystem.IsWindows())
            {
                provider = (int)t3.streamdiffusion.Onnx.ExecutionProvider.DirectMl;
            }

            var triggerGenerate = TriggerGenerate.GetValue(context);
            var autoGenerate = AutoGenerate.GetValue(context);
            var debug = Debug.GetValue(context);
            var fastDecode = FastDecode.GetValue(context);
            var feedback = Math.Clamp(Feedback.GetValue(context), 0f, 1f);

            // No EnginePath: fall back to a model folder from the AI model store that matches the
            // selected model type, so a model installed through the settings window just works.
            if (string.IsNullOrWhiteSpace(modelPath))
                modelPath = FindManagedModelFolder(modelType);

            _debugLogging = debug;
            _updateFrameCounter++;

            if (debug && _updateFrameCounter - _lastHeartbeatFrame >= 120)
            {
                _lastHeartbeatFrame = _updateFrameCounter;
                Log.Debug($"[StreamDiffusion] alive f={_updateFrameCounter} mode={mode} provider={provider} " +
                          $"auto={autoGenerate} trigger={triggerGenerate} pipeline={(_pipeline != null)} " +
                          $"pending={_hasPendingInput} inFlight={_requestInFlight} readback={_readbackPending} " +
                          $"init={_isInitializing} outputNull={Output.Value == null}", this);
            }

            _uiMode = mode;
            _uiModelType = modelType;
            _uiResizeMode = resizeMode;
            _uiExecutionProvider = provider;

            var pipeline = _pipeline;
            if (pipeline != null)
                pipeline.VerboseLogging = debug;

            if (pipeline is StreamDiffusionV2Pipeline v2Pipeline)
                v2Pipeline.FastDecodeEnabled = fastDecode;

            // Re-init trigger: inputs changed OR worker died. A failed initialization is only retried after the
            // inputs change - otherwise a missing model or unsupported provider would reload every frame.
            var inputsChanged = modelPath != _activeModelPath || deviceId != _activeDeviceId
                                || modelType != _activeModelType || provider != _activeProvider;
            var workerGone = _processingTask == null || _processingTask.IsCompleted;
            var failed = _failedInputs;
            var failedWithSameInputs = _initFailed && failed != null
                                       && failed.Path == modelPath && failed.Device == deviceId
                                       && failed.Type == modelType && failed.Provider == provider;

            if (!_isInitializing && !failedWithSameInputs && (inputsChanged || workerGone))
            {
                InitializePipelineWorker(modelPath, deviceId, modelType, provider);
            }

            pipeline = _pipeline;

            if (pipeline == null)
            {
                if (!_isInitializing)
                {
                    if (_outputTexture != null)
                    {
                        _outputTexture.Dispose();
                        _outputTexture = null;
                        _lastOutputSize = Int2.Zero;
                        _lastGenerationTime = 0f;
                        _prevRgba = null;
                    }
                    Output.Value = null;
                    OutputSize.Value = Int2.Zero;
                    GenerationTime.Value = 0f;
                }
                else
                {
                    Output.Value = _outputTexture;
                    OutputSize.Value = _lastOutputSize;
                    GenerationTime.Value = _lastGenerationTime;
                }
                return;
            }

            if (_isInitializing)
            {
                Output.Value = _outputTexture;
                OutputSize.Value = _lastOutputSize;
                GenerationTime.Value = _lastGenerationTime;
                return;
            }

            // V2 bakes its resolution into the exported DiT, so the Width/Height inputs cannot apply
            if (pipeline.TryGetFixedOutputSize(out var fixedWidth, out var fixedHeight))
            {
                width = fixedWidth;
                height = fixedHeight;
                matchInputSize = false;
            }

            // img2img/Stream: continuously chase the latest input frame. Dirty-flag checks can't
            // detect in-place texture updates (GetValue already consumed the flag, and video sources
            // like PlayVideo rewrite the same texture), so with AutoGenerate on we keep a request
            // in flight with the newest frame; _requestInFlight rate-limits to GPU speed.
            if (autoGenerate && mode != 0 && inputImage != null && !inputImage.IsDisposed)
            {
                _hasPendingInput = true;
            }

            var wantGenerate = false;
            if (autoGenerate)
            {
                var promptValid = !string.IsNullOrEmpty(prompt);
                var inputValid = mode == 0 || (inputImage != null && !inputImage.IsDisposed);
                bool modeWants;
                if (mode == 0)
                {
                    modeWants = Txt2ImgParamsChanged(prompt, negativePrompt, seed, width, height, steps, guidance, modelType);
                }
                else
                {
                    modeWants = true;
                }

                wantGenerate = promptValid && inputValid && modeWants;

                if (!wantGenerate && _debugLogging && _updateFrameCounter - _lastSkipLogFrame >= 60)
                {
                    _lastSkipLogFrame = _updateFrameCounter;
                    var reason = !promptValid ? "prompt empty"
                                 : !inputValid ? "no input"
                                 : "input unchanged";
                    Log.Debug($"[StreamDiffusion] AutoGenerate skipped: {reason}", this);
                }
            }
            else
            {
                var wasTriggered = MathUtils.WasTriggered(triggerGenerate, ref _triggerState);
                if (wasTriggered)
                {
                    TriggerGenerate.SetTypedInputValue(false);
                    wantGenerate = true;
                }
            }

            if (wantGenerate)
                _hasPendingInput = true;

            // Start a new readback or enqueue immediately (txt2img / no input)
            if (_hasPendingInput && !_requestInFlight && _inputQueue.IsEmpty && !_readbackPending)
            {
                if (mode != 0 && inputImage != null && !inputImage.IsDisposed)
                {
                    BeginReadback(inputImage);
                }
                else
                {
                    BuildAndEnqueueRequest(
                        prompt: prompt ?? string.Empty,
                        negativePrompt: negativePrompt,
                        mode: mode,
                        steps: steps,
                        guidance: guidance,
                        strength: mode == 2 ? streamStrength : strength,
                        seed: seed,
                        width: width,
                        height: height,
                        resizeMode: resizeMode,
                        preserveDetails: preserveDetails,
                        modelType: modelType,
                        inputData: null,
                        inputWidth: 0,
                        inputHeight: 0,
                        inputFormat: InputPixelFormat.Rgba8);
                }
            }

            // Complete deferred readback and enqueue
            if (_readbackPending)
            {
                var (data, w, h, format) = CompleteReadback();
                if (data != null)
                {
                    _hasPendingInput = false;

                    var useImage = mode != 0 && inputImage != null && !inputImage.IsDisposed;
                    if (!useImage)
                    {
                        ReturnExtractBuffer(data);
                        BuildAndEnqueueRequest(
                            prompt: prompt ?? string.Empty,
                            negativePrompt: negativePrompt,
                            mode: mode,
                            steps: steps,
                            guidance: guidance,
                            strength: mode == 2 ? streamStrength : strength,
                            seed: seed,
                            width: width,
                            height: height,
                            resizeMode: resizeMode,
                            preserveDetails: preserveDetails,
                            modelType: modelType,
                            inputData: null,
                            inputWidth: 0,
                            inputHeight: 0,
                            inputFormat: InputPixelFormat.Rgba8);
                    }
                    else
                    {
                        var effectiveWidth = matchInputSize ? AlignTo8(w) : width;
                        var effectiveHeight = matchInputSize ? AlignTo8(h) : height;
                        BuildAndEnqueueRequest(
                            prompt: prompt ?? string.Empty,
                            negativePrompt: negativePrompt,
                            mode: mode,
                            steps: steps,
                            guidance: guidance,
                            strength: mode == 2 ? streamStrength : strength,
                            seed: seed,
                            width: effectiveWidth,
                            height: effectiveHeight,
                            resizeMode: resizeMode,
                            preserveDetails: preserveDetails,
                            modelType: modelType,
                            inputData: data,
                            inputWidth: w,
                            inputHeight: h,
                            inputFormat: format);
                    }
                }
            }

            // Drain results (newest wins; coalesce while render thread is busy). Results from a replaced worker are
            // dropped: its model may have different dimensions and its buffers belong to the old pipeline.
            var generation = Volatile.Read(ref _workerGeneration);
            GenerationResult? newest = null;
            while (_outputQueue.TryDequeue(out var r))
            {
                if (r != null && r.Generation == generation)
                    newest = r;
            }

            if (newest != null)
            {
                if (newest.Data != null)
                {
                    UploadRgbaTexture(ApplyFeedback(newest.Data, newest.Width, newest.Height, feedback),
                                      newest.Width, newest.Height);
                    _lastOutputSize = new Int2(newest.Width, newest.Height);
                    _lastGenerationTime = newest.Time;
                    _lastStatusMessage = null;
                }
                else if (newest.Error != null)
                {
                    // Generation errors must be visible in the log - they otherwise leave a
                    // blank output with no indication anything was attempted.
                    if (!string.Equals(_lastLoggedError, newest.Error, StringComparison.Ordinal))
                    {
                        _lastLoggedError = newest.Error;
                        Log.Warning($"[StreamDiffusion] Generation failed: {newest.Error}", this);
                    }
                    _lastStatusMessage = newest.Error;
                }
            }

            Output.Value = _outputTexture;
            OutputSize.Value = _lastOutputSize;
            GenerationTime.Value = _lastGenerationTime;
        }
        finally
        {
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    private bool Txt2ImgParamsChanged(string? prompt, string? negativePrompt, int seed, int width, int height, int steps,
                                      float guidance, int modelType)
    {
        // Regenerate only when a parameter actually changes - including a random-seed setting (-1).
        // Re-rolling the random seed every frame flooded the GPU with back-to-back generations;
        // users reroll via TriggerGenerate or by changing the seed value.
        var hash = HashCode.Combine(prompt, negativePrompt, seed, width, height, steps, guidance, modelType);
        if (_hasTxtFingerprint && hash == _lastTxtFingerprint)
            return false;

        _lastTxtFingerprint = hash;
        _hasTxtFingerprint = true;
        return true;
    }

    private void BuildAndEnqueueRequest(string prompt, string? negativePrompt, int mode, int steps, float guidance,
                                        float strength, int seed, int width, int height, int resizeMode,
                                        float preserveDetails, int modelType, byte[]? inputData, int inputWidth,
                                        int inputHeight, InputPixelFormat inputFormat)
    {
        var clampedSteps = steps;
        var clampedGuidance = guidance;
        var pipeline = _pipeline;
        if (pipeline != null)
        {
            var recommended = pipeline.RecommendedSteps;
            var recommendedGuidance = pipeline.RecommendedGuidance;

            if (modelType is (int)ModelTypeOptions.SDTurbo or (int)ModelTypeOptions.SDXLTurbo or (int)ModelTypeOptions.FLUX2Klein)
            {
                clampedSteps = Math.Clamp(steps <= 0 ? recommended : steps, 1, 8);
                clampedGuidance = Math.Clamp(guidance, 0f, 3f);
            }
            else if (modelType == (int)ModelTypeOptions.StreamDiffusionV2)
            {
                clampedSteps = Math.Clamp(steps, 1, 4);
            }
            else if (mode == 1)
            {
                clampedSteps = Math.Max(steps, 10);
            }

            if (_debugLogging)
            {
                Log.Debug($"[StreamDiffusion] Model: {ModelTypeNames[modelType]}, steps={clampedSteps} (recommended: {recommended}), " +
                          $"guidance={clampedGuidance:F1} (recommended: {recommendedGuidance:F1})", this);
            }
        }

        var request = new GenerationRequest(
            prompt, negativePrompt, mode, clampedSteps, clampedGuidance, strength, seed,
            width, height, resizeMode, preserveDetails,
            inputData, inputWidth, inputHeight, inputFormat);

        // The request consumes the pending flag. Without this, a txt2img trigger (or an AutoGenerate
        // parameter change) leaves it set and the render thread re-enqueues every frame, pinning the GPU
        // in back-to-back generations. The Stream path sets it again every frame by design.
        _hasPendingInput = false;

        // Publish "in flight" before the worker can see the request. Enqueueing first let the worker finish
        // (and store false) before this thread stored true, leaving the flag stuck and the op idle forever.
        _requestInFlight = true;
        _inputQueue.Enqueue(request);
        _workerWake.Set();
    }

    #region Pipeline worker lifecycle (DepthAnything port)

    /** Previous published frame for the Feedback blend (evaluation thread only, guarded by _updating). */
    private byte[]? _prevRgba;
    private int _prevWidth;
    private int _prevHeight;

    /** Temporal feedback ("Feed prev. output"): blends the new frame with the last published one.
     *  The stored previous frame is the BLENDED one, so trails decay exponentially. f=0 is a no-op
     *  and never allocates; with f>0 the blend happens in place in the cached previous-frame
     *  buffer (it is both history and published frame), so the steady state allocates nothing. */
    private byte[] ApplyFeedback(byte[] data, int width, int height, float feedback)
    {
        var length = width * height * 4;
        if (feedback <= 0f || data.Length != length)
        {
            _prevRgba = null;
            return data;
        }

        if (_prevRgba is not byte[] prev || _prevWidth != width || _prevHeight != height)
        {
            prev = (byte[])data.Clone();   // first frame / resolution change only
        }
        else
        {
            for (var i = 0; i < length; i++)
            {
                prev[i] = (byte)(data[i] * (1f - feedback) + prev[i] * feedback + 0.5f);
            }
        }

        _prevRgba = prev;
        _prevWidth = width;
        _prevHeight = height;
        return prev;
    }

    /** Model store scan results per model type, cached briefly so an empty EnginePath
     *  does not rescan (and re-log) the store on every frame. */
    private static readonly ConcurrentDictionary<int, (string? Folder, long ScannedAtTicks)> ManagedFolderCache = new();
    private const double ManagedModelScanIntervalMs = 5000;
    private static string? _lastLoggedManagedFolder;

    /** The first folder in the model store that validates for the selected model type, or null. */
    private static string? FindManagedModelFolder(int modelType)
    {
        var nowTicks = Environment.TickCount64;
        if (ManagedFolderCache.TryGetValue(modelType, out var cached)
            && nowTicks - cached.ScannedAtTicks < ManagedModelScanIntervalMs)
        {
            return cached.Folder;
        }

        var found = ScanManagedModelFolders(modelType);
        ManagedFolderCache[modelType] = (found, nowTicks);
        return found;
    }

    private static string? ScanManagedModelFolders(int modelType)
    {
        var familyDirectory = T3.Core.Settings.AiModelSettings.FamilyDirectory(T3.Core.Settings.AiModelSettings.StreamDiffusionFamilyFolder);
        if (!Directory.Exists(familyDirectory))
            return null;

        try
        {
            foreach (var directory in Directory.GetDirectories(familyDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var error = modelType == (int)ModelTypeOptions.StreamDiffusionV2
                                ? StreamDiffusionV2Pipeline.ValidateModelDirectory(directory)
                                : StableDiffusionPipeline.ValidateModelDirectory(directory);
                if (error.Length == 0)
                {
                    if (Interlocked.CompareExchange(ref _lastLoggedManagedFolder, directory, null) != directory)
                    {
                        Log.Debug($"[StreamDiffusion] No engine path set - using managed model folder '{directory}'");
                    }
                    return directory;
                }
            }
        }
        catch
        {
            // An unreadable store folder is not worth failing the update over
        }

        return null;
    }

    private void InitializePipelineWorker(string? modelPath, int deviceId, int modelType, int provider)
    {
        var previousTask = _processingTask;
        StopWorker();

        var validationError = modelType == (int)ModelTypeOptions.StreamDiffusionV2
                                  ? StreamDiffusionV2Pipeline.ValidateModelDirectory(modelPath)
                                  : StableDiffusionPipeline.ValidateModelDirectory(modelPath);
        if (validationError.Length > 0)
        {
            _lastStatusMessage = validationError;
            _initFailed = true;
            _failedInputs = new FailureKey(modelPath, deviceId, modelType, provider);
            Log.Warning($"[StreamDiffusion] {validationError}", this);
            return;
        }

        // The heavier families need their own exports - pointing them at an
        // SD 1.5/Turbo folder loads fine mechanically but produces garbage.
        // Warn instead of blocking: a valid export may just use a layout we
        // don't recognize.
        if (modelType is (int)ModelTypeOptions.SDXLTurbo or (int)ModelTypeOptions.FLUX2Klein or (int)ModelTypeOptions.SDXL10
            && modelPath != null)
        {
            var hasSecondStage = Directory.Exists(Path.Combine(modelPath, "text_encoder_2"))
                                 || Directory.Exists(Path.Combine(modelPath, "tokenizer_2"))
                                 || File.Exists(Path.Combine(modelPath, "text_encoder_2.onnx"));
            if (!hasSecondStage)
            {
                Log.Warning($"[StreamDiffusion] Model type '{ModelTypeNames[modelType]}' selected, but " +
                            $"'{Path.GetFileName(modelPath.TrimEnd('\\', '/'))}' has no second-stage components " +
                            "(text_encoder_2 / tokenizer_2). If this folder is an SD 1.5 / SD Turbo export, " +
                            "output will be wrong - switch to 'SD 1.5' or 'SD Turbo', or point the model path " +
                            "at an export of the selected family.", this);
            }
        }

        _isInitializing = true;
        _initFailed = false;
        _activeModelPath = modelPath;
        _activeDeviceId = deviceId;
        _activeModelType = modelType;
        _activeProvider = provider;
        _failedInputs = null;
        _hasPendingInput = true;

        var generation = Interlocked.Increment(ref _workerGeneration);
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;
        _processingTask = Task.Run(async () =>
        {
            // The previous generation releases its ONNX sessions only when its loop exits. Waiting here keeps the
            // render thread free while still preventing two sessions from holding GPU memory at the same time.
            if (previousTask != null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch
                {
                    // A failing predecessor must not prevent this generation from starting
                }
            }

            if (token.IsCancellationRequested)
                return;

            WorkerLoop(modelPath!, deviceId, modelType, provider, token, generation);
        }, CancellationToken.None);
    }

    private void StopWorker()
    {
        try
        {
            _workerCts?.Cancel();
        }
        catch { }

        _workerWake.Set();

        // Never wait for the worker on the render thread: inference can take seconds. InitializePipelineWorker
        // chains the next worker after this task, so the old pipeline still releases its GPU memory first.

        while (_inputQueue.TryDequeue(out var req))
        {
            if (req.InputData != null)
                ReturnExtractBuffer(req.InputData);
        }

        while (_outputQueue.TryDequeue(out _)) { }

        lock (_stateLock)
        {
            _pipeline = null;
        }

        _requestInFlight = false;
        _hasPendingInput = false;
        _readbackPending = false;
    }

    private void WorkerLoop(string modelPath, int deviceId, int modelType, int provider, CancellationToken token, int generation)
    {
        // Only the current worker touches the fingerprint fields from here on: the previous worker is
        // provably dead (this task awaited it), so the reset cannot be overwritten by a stale write.
        // Folding the generation into the fingerprint (see ComputeFingerprint) also keeps a swapped
        // model/device/provider from skipping the first request.
        _hasProcessedFingerprint = false;

        IGenerationPipeline? pipeline = null;
        var failedToInit = false;
        try
        {
            bool initialized;
            if (modelType == (int)ModelTypeOptions.StreamDiffusionV2)
            {
                var streamPipeline = new StreamDiffusionV2Pipeline();
                pipeline = streamPipeline;
                initialized = streamPipeline.Initialize(modelPath, deviceId, (t3.streamdiffusion.Onnx.ExecutionProvider)provider);
            }
            else
            {
                var stablePipeline = new StableDiffusionPipeline();
                pipeline = stablePipeline;
                initialized = stablePipeline.Initialize(modelPath, deviceId, (ModelType)modelType, (t3.streamdiffusion.Onnx.ExecutionProvider)provider);
            }

            pipeline.VerboseLogging = _debugLogging;

            if (!initialized)
            {
                failedToInit = true;

                // Initialization finished (with failure) - unblock the render thread immediately.
                // _isInitializing must not stay set while the worker keeps serving requests,
                // otherwise Evaluate() early-returns forever and no input ever reaches the pipeline.
                if (generation == Volatile.Read(ref _workerGeneration))
                {
                    _isInitializing = false;
                    _initFailed = true;
                    _failedInputs = new FailureKey(_activeModelPath, _activeDeviceId, _activeModelType, _activeProvider);
                    _lastStatusMessage = pipeline?.InitializationError ?? "Failed to initialize ONNX pipeline. See log for details.";
                }
            }
            else
            {
                lock (_stateLock)
                {
                    _pipeline = pipeline;
                }

                // Initialization finished successfully - see note above.
                if (generation == Volatile.Read(ref _workerGeneration))
                    _isInitializing = false;

                _lastStatusMessage = null;
                Log.Info($"[StreamDiffusion] Pipeline ready ({pipeline.ProviderLabel}, " +
                         $"img2img: {(pipeline.SupportsImg2Img ? "yes" : "no")}, model: {ModelTypeNames[modelType]})", this);
            }

            while (!token.IsCancellationRequested)
            {
                if (_inputQueue.TryDequeue(out var request))
                {
                    if (request != null)
                    {
                        // Float render targets are converted here rather than on the render thread; the raw
                        // staging buffer is returned before the request reaches the pipeline.
                        request = ConvertRequestInput(request);
                        var result = ProcessGenerationRequest(pipeline, request, generation);
                        if (!token.IsCancellationRequested && result != null)
                            _outputQueue.Enqueue(result with { Generation = generation });
                    }

                    if (request?.InputData != null)
                        ReturnExtractBuffer(request.InputData);

                    _requestInFlight = false;
                }
                else
                {
                    _workerWake.WaitOne(15);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log.Error($"[StreamDiffusion] Worker loop error: {ex.Message}", this);
                _outputQueue.Enqueue(new GenerationResult(null, 0, 0, 0, $"Worker error: {ex.Message}", generation));

                // An exception before a pipeline was published is an initialization failure. Without
                // latching it the retry gate sees "worker gone, same inputs" as a fresh attempt and
                // reconstructs every ONNX session on the next frame.
                if (pipeline == null)
                    failedToInit = true;
            }

            _requestInFlight = false;
        }
        finally
        {
            // Worker owns its pipeline - dispose unconditionally; a newer worker may have replaced the field
            pipeline?.Dispose();

            lock (_stateLock)
            {
                if (ReferenceEquals(_pipeline, pipeline))
                    _pipeline = null;
            }

            // Stale generations must not clobber their successor's init state
            if (generation == Volatile.Read(ref _workerGeneration))
            {
                _isInitializing = false;
                if (failedToInit)
                {
                    _initFailed = true;
                    _failedInputs = new FailureKey(_activeModelPath, _activeDeviceId, _activeModelType, _activeProvider);
                    _lastStatusMessage = pipeline?.InitializationError ?? "Failed to initialize ONNX pipeline. See log for details.";
                }
            }

            if (_debugLogging)
                Log.Debug("[StreamDiffusion] Worker loop stopped", this);
        }
    }

    /// <summary>
    /// Converts raw float render-target rows to RGBA8. This runs on the worker: the render thread only copies the
    /// mapped rows out of the staging texture, so neither the per-pixel conversion nor its sRGB encode stalls a frame.
    /// </summary>
    private GenerationRequest ConvertRequestInput(GenerationRequest request)
    {
        if (request.InputData == null
            || request.InputFormat is InputPixelFormat.Rgba8 or InputPixelFormat.Bgra8
            || request.InputWidth <= 0 || request.InputHeight <= 0)
        {
            return request;
        }

        var halfFloats = request.InputFormat == InputPixelFormat.Rgba16F;
        var pixelSize = halfFloats ? 8 : 16;
        var requiredBytes = request.InputWidth * request.InputHeight * pixelSize;
        if (request.InputData.Length < requiredBytes)
            return request;

        var converted = RentExtractBuffer(request.InputWidth * request.InputHeight * 4);
        ConvertFloatToRgba8(request.InputData, converted, request.InputWidth, request.InputHeight, halfFloats);
        ReturnExtractBuffer(request.InputData);
        return request with { InputData = converted, InputFormat = InputPixelFormat.Rgba8 };
    }

    private GenerationResult? ProcessGenerationRequest(IGenerationPipeline pipeline, GenerationRequest request, int generation)
    {
        try
        {
            pipeline.ConfigureStream(request.Steps);

            // Negative seeds randomize per call, so only fixed-seed requests are safely skippable there.
            // Stream mode consumes one new input frame per request regardless of seed: an unchanged frame can
            // reuse the previous result, which also stops a frozen source from keeping the GPU busy indefinitely.
            var canSkipIdentical = request.Seed >= 0 || request.Mode == 2;
            if (canSkipIdentical)
            {
                // A V2 prompt that was swapped in by the background encoder must produce one new frame even when
                // the input pixels did not change; the version is part of the fingerprint. The worker generation
                // and the FastDecode state are folded in as well, so a swapped model or decode-window change
                // always produces one fresh frame - without the render thread writing any worker-side state.
                var promptVersion = 0;
                var fingerprintSalt = generation;
                if (pipeline is StreamDiffusionV2Pipeline v2)
                {
                    promptVersion = v2.PromptVersion;
                    if (v2.FastDecodeEnabled)
                        fingerprintSalt ^= unchecked((int)0x5A5A5A5A);
                }

                var fingerprint = ComputeFingerprint(request, promptVersion, fingerprintSalt);
                if (_hasProcessedFingerprint && fingerprint == _lastProcessedFingerprint)
                    return null; // identical request - keep previous output, spare the GPU

                _lastProcessedFingerprint = fingerprint;
                _hasProcessedFingerprint = true;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[]? imageData = null;
            string? error = null;

            if (request.Mode == 0)
            {
                imageData = pipeline.Txt2Img(
                    request.Prompt, request.NegativePrompt, request.Width, request.Height,
                    request.Steps, request.Guidance, request.Seed, out error);
            }
            else if (request.Mode == 1 && request.InputData != null)
            {
                imageData = pipeline.Img2Img(
                    request.Prompt, request.NegativePrompt, request.InputData,
                    request.InputWidth, request.InputHeight, request.Width, request.Height,
                    request.Steps, request.Guidance, request.Strength, request.Seed,
                    request.ResizeMode, request.PreserveDetails, request.InputFormat == InputPixelFormat.Bgra8, out error);
            }
            else if (request.Mode == 2 && request.InputData != null)
            {
                imageData = pipeline.StreamStep(
                    request.Prompt, request.InputData, request.InputWidth, request.InputHeight,
                    request.Width, request.Height, request.Strength, request.ResizeMode,
                    request.Seed, request.InputFormat == InputPixelFormat.Bgra8, out error);
            }
            else
            {
                error = $"Unsupported mode/input combination (mode={request.Mode}, inputData={(request.InputData != null ? "yes" : "no")})";
            }

            sw.Stop();

            if (imageData == null && error == null)
                error = "Pipeline returned null";

            return new GenerationResult(imageData, request.Width, request.Height, (float)sw.Elapsed.TotalSeconds, error);
        }
        catch (Exception ex)
        {
            Log.Error($"[StreamDiffusion] ProcessGenerationRequest failed for mode={request.Mode}: {ex.Message}", this);
            return new GenerationResult(null, request.Width, request.Height, 0, $"Error: {ex.Message}");
        }
    }

    private int ComputeFingerprint(GenerationRequest request, int promptVersion, int fingerprintSalt)
    {
        unchecked
        {
            var hash = (int)HashCode.Combine(
                request.Prompt, request.NegativePrompt, request.Steps, request.Guidance,
                request.Strength, request.Seed, request.Width, request.Height);

            hash = (hash * 31) ^ fingerprintSalt;
            hash = (hash * 31) ^ request.ResizeMode;
            hash = (hash * 31) ^ request.PreserveDetails.GetHashCode();
            hash = (hash * 31) ^ request.InputWidth;
            hash = (hash * 31) ^ request.InputHeight;
            hash = (hash * 31) ^ request.InputFormat.GetHashCode();
            hash = (hash * 31) ^ promptVersion;

            if (request.InputData == null || request.InputWidth == 0 || request.InputHeight == 0)
                return hash;

            // Hash every word for model-sized frames and a bounded subset for large inputs. The previous
            // 16x16 sample could miss a small moving object and freeze a live feed on a false "identical".
            var pixelCount = request.InputWidth * request.InputHeight;
            var words = MemoryMarshal.Cast<byte, uint>(request.InputData.AsSpan(0, pixelCount * 4));
            var step = Math.Max(1, words.Length / 262144);
            for (var index = 0; index < words.Length; index += step)
                hash = hash * 31 + (int)words[index];

            hash = hash * 31 + (int)words[^1];
            return hash;
        }
    }

    #endregion

    #region Deferred readback (DepthAnything port)

    private void BeginReadback(Texture2D texture)
    {
        if (texture.IsDisposed)
        {
            _hasPendingInput = false;
            return;
        }

        var desc = texture.Description;

        switch (desc.Format)
        {
            case Format.R8G8B8A8_UNorm:
            case Format.R8G8B8A8_UNorm_SRgb:
                _readbackFormat = InputPixelFormat.Rgba8;
                break;
            case Format.B8G8R8A8_UNorm:
            case Format.B8G8R8A8_UNorm_SRgb:
                _readbackFormat = InputPixelFormat.Bgra8;
                break;
            case Format.R16G16B16A16_Float:
                _readbackFormat = InputPixelFormat.Rgba16F;
                break;
            case Format.R32G32B32A32_Float:
                _readbackFormat = InputPixelFormat.Rgba32F;
                break;
            default:
                if (!_loggedUnsupportedFormat)
                {
                    _loggedUnsupportedFormat = true;
                    Log.Warning($"[StreamDiffusion] Unsupported input format {desc.Format} - skipping frames. " +
                                "Connect an RGBA image or a float render target.", this);
                }
                _hasPendingInput = false;
                return;
        }

        var device = ResourceManager.Device;
        _readbackTexture = GetOrCreateStagingTexture(desc.Width, desc.Height, desc.Format);
        device.ImmediateContext.CopyResource(texture, _readbackTexture);

        _readbackPending = true;
        _readbackWaitFrames = 0;
    }

    private (byte[]? data, int width, int height, InputPixelFormat format) CompleteReadback()
    {
        var context = ResourceManager.Device.ImmediateContext;

        // TryMapForRead only succeeds once the copy is finished, so polling never stalls the render thread.
        var stagingTexture = _readbackTexture;
        var format = _readbackFormat;
        if (stagingTexture == null)
        {
            _readbackPending = false;
            return (null, 0, 0, format);
        }

        T3.Graphics.Compat.Texture2D nativeStaging = stagingTexture;
        if (nativeStaging.Native == null
            || !ResourceManager.Device.Backend.TryMapForRead(nativeStaging.Native, 0, out var mapped))
        {
            _readbackWaitFrames++;
            if (_readbackWaitFrames > ReadbackTimeoutFrames)
            {
                Log.Warning("[StreamDiffusion] Readback never completed - restarting readback", this);
                _readbackPending = false;
                _hasPendingInput = true;
            }
            return (null, 0, 0, format);
        }

        _readbackPending = false;
        var desc = stagingTexture.Description;
        int width = desc.Width;
        int height = desc.Height;

        try
        {
            if (mapped.SizeInBytes <= 0)
                return (null, 0, 0, format);

            // Float rows are copied out raw; the worker converts them to RGBA8 so no per-pixel work runs here
            var pixelSize = BytesPerPixel(format);
            var buffer = RentExtractBuffer(width * height * pixelSize);
            unsafe
            {
                var srcPtr = (byte*)mapped.Data;
                var rowPitch = mapped.RowPitch;
                var rowBytes = width * pixelSize;
                fixed (byte* dst = buffer)
                {
                    for (var y = 0; y < height; y++)
                    {
                        System.Buffer.MemoryCopy(srcPtr + (long)y * rowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }

            return (buffer, width, height, format);
        }
        finally
        {
            context.UnmapSubresource(stagingTexture, 0);
        }
    }

    private static int BytesPerPixel(InputPixelFormat format)
    {
        return format switch
        {
            InputPixelFormat.Rgba16F => 8,
            InputPixelFormat.Rgba32F => 16,
            _ => 4
        };
    }

    /// <summary>
    /// Converts linear RGBA render-target rows to encoded 8-bit RGBA. Float render targets hold linear color, while the
    /// image loaders and the diffusion models work in encoded sRGB, so the values have to be encoded on the way out.
    /// Runs on the worker thread.
    /// </summary>
    private static void ConvertFloatToRgba8(byte[] source, byte[] destination, int width, int height, bool halfFloats)
    {
        var pixelSize = halfFloats ? 8 : 16;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * width * pixelSize;
            var targetRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var pixel = source.AsSpan(sourceRow + x * pixelSize, pixelSize);
                var target = targetRow + x * 4;
                if (halfFloats)
                {
                    destination[target + 0] = EncodeSrgb((float)MemoryMarshal.Read<Half>(pixel));
                    destination[target + 1] = EncodeSrgb((float)MemoryMarshal.Read<Half>(pixel[2..]));
                    destination[target + 2] = EncodeSrgb((float)MemoryMarshal.Read<Half>(pixel[4..]));
                }
                else
                {
                    destination[target + 0] = EncodeSrgb(MemoryMarshal.Read<float>(pixel));
                    destination[target + 1] = EncodeSrgb(MemoryMarshal.Read<float>(pixel[4..]));
                    destination[target + 2] = EncodeSrgb(MemoryMarshal.Read<float>(pixel[8..]));
                }

                destination[target + 3] = 255;
            }
        }
    }

    private static byte EncodeSrgb(float linear)
    {
        // Also routes NaN and negative values to black
        if (!(linear > 0f))
            return 0;
        if (linear >= 1f)
            return byte.MaxValue;

        return SrgbEncodeTable[(int)(linear * (SrgbTableSize - 1) + 0.5f)];
    }

    private static byte[] CreateSrgbEncodeTable()
    {
        var table = new byte[SrgbTableSize];
        for (var index = 0; index < table.Length; index++)
        {
            var linear = index / (float)(SrgbTableSize - 1);
            var encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            table[index] = (byte)(encoded * 255f + 0.5f);
        }

        return table;
    }

    #endregion

    #region Persistent output texture (DepthAnything port)

    // Overload: upload RGBA texture directly from a GPU pointer (zero‑copy path).
    private unsafe void UploadRgbaTexture(IntPtr gpuPtr, int width, int height)
    {
        // Ensure the output texture exists with correct dimensions.
        if (_outputTexture == null || _outputTexture.Description.Width != width || _outputTexture.Description.Height != height)
        {
            _outputTexture?.Dispose();
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.None
            };
            _outputTexture = new Texture2D(new T3.Graphics.Compat.Texture2D(ResourceManager.Device, desc));
        }

        // Create a DataBox that points to the GPU memory.
        var dataBox = new DataBox(gpuPtr, width * 4, 0);
        ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _outputTexture);
    }

    // Compatibility wrapper: upload from managed byte[] by pinning and delegating to the pointer overload.
    private void UploadRgbaTexture(byte[] rgbaData, int width, int height)
    {
        unsafe
        {
            fixed (byte* ptr = rgbaData)
            {
                UploadRgbaTexture((IntPtr)ptr, width, height);
            }
        }
    }

    #endregion

    #region Staging + buffer pool

    private Texture2D GetOrCreateStagingTexture(int width, int height, Format format)
    {
        // One staging texture is enough: readbacks are single-in-flight, and a new input size replaces the
        // buffer instead of keeping every size the session has ever seen alive.
        if (_stagingTexture is { IsDisposed: false }
            && _stagingTexture.Description.Width == width
            && _stagingTexture.Description.Height == height
            && _stagingTexture.Description.Format == format)
        {
            return _stagingTexture;
        }

        _stagingTexture?.Dispose();
        _stagingTexture = new Texture2D(new T3.Graphics.Compat.Texture2D(ResourceManager.Device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        }));

        return _stagingTexture;
    }

    private byte[] RentExtractBuffer(int minimumSize)
    {
        lock (_poolLock)
        {
            while (_extractBufferPool.Count > 0)
            {
                var buffer = _extractBufferPool.Pop();
                if (buffer.Length >= minimumSize)
                    return buffer;
            }
        }

        return new byte[minimumSize];
    }

    private void ReturnExtractBuffer(byte[] buffer)
    {
        lock (_poolLock)
        {
            if (_extractBufferPool.Count < 4)
                _extractBufferPool.Push(buffer);
        }
    }

    #endregion

    #region IStatusProvider

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        if (_isInitializing)
            return IStatusProvider.StatusLevel.Warning;

        var pipeline = _pipeline;
        if (pipeline == null || _initFailed)
            return IStatusProvider.StatusLevel.Warning;

        try
        {
            // A pipeline whose provider fallback failed is still published but cannot stream; the status text
            // already says "Initialization failed", so the light must not stay green.
            if (!pipeline.IsInitialized)
                return IStatusProvider.StatusLevel.Warning;
        }
        catch (ObjectDisposedException)
        {
            return IStatusProvider.StatusLevel.Warning;
        }

        return string.IsNullOrEmpty(_lastStatusMessage)
            ? IStatusProvider.StatusLevel.Success
            : IStatusProvider.StatusLevel.Warning;
    }

    public string? GetStatusMessage()
    {
        if (_isInitializing)
            return "Loading model...";

        var pipeline = _pipeline;
        if (pipeline == null)
            return _lastStatusMessage ?? "Not initialized";

        try
        {
            if (!pipeline.IsInitialized)
                return "Initialization failed";

            if (!string.IsNullOrEmpty(_lastStatusMessage))
                return _lastStatusMessage;

            var detail = string.Empty;
            if (pipeline is StreamDiffusionV2Pipeline v2)
            {
                var total = v2.LastEncodeMs + v2.LastDenoiseMs + v2.LastDecodeMs;
                if (total > 0)
                    detail = $", {total:F0} ms (enc {v2.LastEncodeMs:F0} / dit {v2.LastDenoiseMs:F0} / dec {v2.LastDecodeMs:F0})";

                if (v2.IsPromptPending)
                    detail += ", encoding new prompt";
            }

            return $"Ready ({pipeline.ProviderLabel}, img2img: {(pipeline.SupportsImg2Img ? "yes" : "no")}{detail})";
        }
        catch (ObjectDisposedException)
        {
            return "Not initialized";
        }
    }

    #endregion

    #region ICustomDropdownHolder

    public string GetValueForInput(Guid inputId)
    {
        if (inputId == Mode.Id)
        {
            var mode = _uiMode;
            return mode >= 0 && mode < ModeNames.Length ? ModeNames[mode] : "Unknown";
        }

        if (inputId == ModelType.Id)
        {
            var type = _uiModelType;
            return type >= 0 && type < ModelTypeNames.Length ? ModelTypeNames[type] : "Unknown";
        }

        if (inputId == ResizeMode.Id)
        {
            var mode = _uiResizeMode;
            return mode >= 0 && mode < ResizeModeNames.Length ? ResizeModeNames[mode] : "Unknown";
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var provider = _uiExecutionProvider;
            return provider >= 0 && provider < ExecutionProviderNames.Length ? ExecutionProviderNames[provider] : "CUDA";
        }

        if (inputId == EnginePath.Id)
        {
            var value = EnginePath.Value;
            return string.IsNullOrEmpty(value) ? PlaceholderTextModel : value;
        }

        if (inputId == CudaDevice.Id)
        {
            var deviceId = CudaDevice.Value;
            var names = GetDeviceNames();
            return deviceId >= 0 && deviceId < names.Length ? $"{deviceId}: {names[deviceId]}" : PlaceholderTextDevice;
        }

        return "";
    }

    public IEnumerable<string> GetOptionsForInput(Guid inputId)
    {
        if (inputId == Mode.Id)
        {
            foreach (var mode in ModeNames)
                yield return mode;
            yield break;
        }

        if (inputId == ModelType.Id)
        {
            foreach (var type in ModelTypeNames)
                yield return type;
            yield break;
        }

        if (inputId == ResizeMode.Id)
        {
            foreach (var mode in ResizeModeNames)
                yield return mode;
            yield break;
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            foreach (var provider in ExecutionProviderNames)
                yield return provider;
            yield break;
        }

        if (inputId == CudaDevice.Id)
        {
            var names = GetDeviceNames();
            for (var i = 0; i < names.Length; i++)
                yield return $"{i}: {names[i]}";
            yield break;
        }

        yield return "undefined";
    }

    public void HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected))
            return;

        if (inputId == Mode.Id)
        {
            var index = Array.IndexOf(ModeNames, selected);
            if (index >= 0)
                Mode.SetTypedInputValue(index);
            return;
        }

        if (inputId == ModelType.Id)
        {
            var index = Array.IndexOf(ModelTypeNames, selected);
            if (index >= 0)
                ModelType.SetTypedInputValue(index);
            return;
        }

        if (inputId == ResizeMode.Id)
        {
            var index = Array.IndexOf(ResizeModeNames, selected);
            if (index >= 0)
                ResizeMode.SetTypedInputValue(index);
            return;
        }

        if (inputId == EnginePath.Id)
        {
            if (selected != PlaceholderTextModel)
                EnginePath.SetTypedInputValue(selected);
            return;
        }

        if (inputId == CudaDevice.Id && selected != PlaceholderTextDevice)
        {
            var separatorIndex = selected.IndexOf(':');
            if (separatorIndex > 0 && int.TryParse(selected.AsSpan(0, separatorIndex), out var deviceId))
                CudaDevice.SetTypedInputValue(deviceId);
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var index = Array.IndexOf(ExecutionProviderNames, selected);
            if (index >= 0)
                ExecutionProviderParam.SetTypedInputValue(index);
        }
    }

    #endregion

    private static string[]? _cachedDeviceNames;
    private static readonly object DeviceNameCacheLock = new();

    private static string[] GetDeviceNames()
    {
        if (_cachedDeviceNames != null)
            return _cachedDeviceNames;

        // Adapter enumeration can run from any instance's dropdown on any thread, so the lazy cache is built under a lock
        lock (DeviceNameCacheLock)
        {
            if (_cachedDeviceNames != null)
                return _cachedDeviceNames;

            var names = new List<string>();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    AddDxgiAdapterNames(names);
                }
            }
            catch
            {
                // Adapter names are only labels for the device dropdown
            }

            _cachedDeviceNames = names.Count > 0 ? names.ToArray() : ["Default"];
        }

        return _cachedDeviceNames;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddDxgiAdapterNames(List<string> names)
    {
        using var factory = new SharpDX.DXGI.Factory1();
        foreach (var adapter in factory.Adapters)
        {
            names.Add(adapter.Description.Description.Trim());
            adapter.Dispose();
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing)
            return;

        try
        {
            _workerCts?.Cancel();
        }
        catch { }

        _workerWake.Set();

        try
        {
            _processingTask?.Wait(0);
        }
        catch { }

        while (_inputQueue.TryDequeue(out var req))
        {
            if (req.InputData != null)
                ReturnExtractBuffer(req.InputData);
        }

        while (_outputQueue.TryDequeue(out _)) { }

        _outputTexture?.Dispose();
        _outputTexture = null;


        _stagingTexture?.Dispose();
        _stagingTexture = null;

        lock (_poolLock)
        {
            _extractBufferPool.Clear();
        }

        // Do not dispose _workerWake - an exiting worker may still wait on it.
        // Do not dispose _pipeline - the worker owns it.

        base.Dispose(isDisposing);
    }
}
