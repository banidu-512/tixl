using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using T3.Core.Logging;

namespace t3.streamdiffusion.Onnx;

public enum ModelType { SD15, SDTurbo, SDXLTurbo, FLUX2Klein, SDXL10 }

/// <summary>
/// Stable Diffusion 1.x / SD-Turbo ONNX pipeline: CLIP text encoding, DDIM UNet
/// denoising loop, and VAE decoding. Models are loaded from a directory that
/// contains unet.onnx, text_encoder.onnx, vae_decoder.onnx (vae_encoder.onnx
/// enables img2img) and tokenizer files.
/// </summary>
public sealed class StableDiffusionPipeline : IDisposable
{
    public const float VaeScalingFactor = 0.18215f;

    private static readonly string[] RequiredComponents =
    {
        "unet",
        "text_encoder",
        "vae_decoder",
    };

    /// <summary>
    /// Resolves a model component in either layout:
    /// flat (<dir>/unet.onnx) or diffusers export (<dir>/unet/model.onnx with
    /// optional external weights.pb alongside, which ORT resolves automatically).
    /// </summary>
    public static string? ResolveModelFile(string modelDirectory, string component)
    {
        var flat = Path.Combine(modelDirectory, component + ".onnx");
        if (File.Exists(flat))
            return flat;

        var diffusers = Path.Combine(modelDirectory, component, "model.onnx");
        if (File.Exists(diffusers))
            return diffusers;

        return null;
    }

    private OnnxModelSession? _textEncoder;
    private OnnxModelSession? _unet;
    private OnnxModelSession? _vaeDecoder;
    private OnnxModelSession? _vaeEncoder;
    private BoundFloatSession? _boundVaeDecoder;
    private BoundFloatSession? _boundVaeEncoder;

    // GPU-resident latent flow: latents stay in GPU memory across
    // encode -> denoise -> decode via lat_scale/lat_combine graphs (see
    // tools/make-latent-ops.py). Any failure permanently falls back to the
    // CPU-latent flow until the next re-initialization.
    private GpuLatentOps? _gpuLatents;
    private OrtIoBinding? _unetGpuBinding;
    private OrtIoBinding? _encodeGpuBinding;
    private OrtIoBinding? _decodeGpuBinding;
    private GpuTensor? _streamGpuLatents;
    private bool _gpuFlowFailed;

    /// <summary>True when latents are kept in GPU memory across the whole frame.</summary>
    public bool UseGpuLatentFlow => _gpuLatents != null;
    private ClipTokenizer? _tokenizer;
    private readonly object _lock = new();

    // Cached UNet input-name discovery (resolved once per session)
    private string? _unetSampleName;
    private string? _unetEmbeddingName;
    private string? _unetTimestepName;
    private bool _unetTimestepIsFloat;

    // Persistent IO-binding state for the UNet (reused across steps and frames)
    private OrtIoBinding? _unetBinding;
    private OrtValue? _unetSampleValue;
    private OrtValue? _unetEmbeddingValue;
    private OrtValue? _unetTimestepValue;
    private OrtValue? _unetOutputValue;
    private float[] _unetSampleData = Array.Empty<float>();
    private float[] _unetEmbeddingData = Array.Empty<float>();
    private readonly float[] _unetTimestepFData = new float[1];
    private readonly long[] _unetTimestepLData = new long[1];
    private int _unetBoundLatentWidth;
    private int _unetBoundLatentHeight;
    private bool _unetBindingFailed;

    // Cached scheduler (one per pipeline; rebuilt only when the model type changes).
    // The schedule arrays and alpha-cumprod tables are fixed per scheduler instance,
    // so rebuilding per call is wasted work.
    private StreamScheduler? _scheduler;

    // Reusable scratch for the CFG epsilon combine (avoids a fresh ~256 KB alloc
    // per UNet step at 512x512). Lengths always equal `latents.Length` within a
    // generation so it can be reused across steps.
    private float[]? _epsScratch;

    // One-shot NaN warning: a single log per pipeline instance prevents log spam
    // when the same encoder produces NaN on every call.
    private bool _loggedNaNWarning;

    public bool IsInitialized => _unet != null && _textEncoder != null && _vaeDecoder != null && _tokenizer != null;
    public bool SupportsImg2Img => _vaeEncoder != null;
    public string ProviderLabel => _unet?.ProviderLabel ?? "CPU";

    /// <summary>
    /// When false, suppresses per-step / latent-stats info logs and one-shot
    /// "all zeros" encoder warnings. Bound to the operator's Debug input each
    /// frame so per-generation verbosity can be toggled without rebuilding the pipeline.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Per-stage wall time of the most recent generation (milliseconds).</summary>
    public double LastEncodeMs { get; private set; }
    public double LastDenoiseMs { get; private set; }
    public double LastDecodeMs { get; private set; }

    public ModelType CurrentModelType { get; private set; } = ModelType.SD15;

    /// <summary>True when a TAESD tiny autoencoder replaced the full VAE (realtime mode).</summary>
    public bool UsingTinyVae { get; private set; }

    /// <summary>
    /// Recommended default steps for the current model type.
    /// </summary>
    public int RecommendedSteps => CurrentModelType switch
    {
        ModelType.SDTurbo => 2,
        ModelType.SDXLTurbo => 4,
        ModelType.FLUX2Klein => 4,
        _ => 20
    };

    /// <summary>
    /// Recommended guidance scale for the current model type.
    /// </summary>
    public float RecommendedGuidance => CurrentModelType switch
    {
        ModelType.SDTurbo or ModelType.SDXLTurbo => 1.0f,
        ModelType.FLUX2Klein => 2.5f,
        _ => 7.5f
    };

    /// <summary>
    /// Recommended scheduler for the current model type.
    /// </summary>
    public SchedulerType RecommendedScheduler => CurrentModelType switch
    {
        ModelType.SDTurbo or ModelType.SDXLTurbo or ModelType.FLUX2Klein => SchedulerType.EulerAncestral,
        _ => SchedulerType.DDIM
    };

    /// <summary>
    /// Validates a model directory and returns a description of anything missing.
    /// Empty string means the directory is complete.
    /// </summary>
    public static string ValidateModelDirectory(string? modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
            return "Model path is empty. Select a folder containing unet.onnx, text_encoder.onnx and vae_decoder.onnx.";

        if (!Directory.Exists(modelDirectory))
            return $"Model directory not found: {modelDirectory}";

        var missing = RequiredComponents
            .Where(component => ResolveModelFile(modelDirectory, component) == null)
            .Select(component => component + ".onnx")
            .ToList();

        if (!HasTokenizerFiles(modelDirectory))
        {
            missing.Add("tokenizer.json (or vocab.json + merges.txt)");
        }

        return missing.Count == 0
            ? string.Empty
            : $"Missing in '{Path.GetFileName(modelDirectory.TrimEnd('\\', '/'))}': {string.Join(", ", missing)}";
    }

    private static bool HasTokenizerFiles(string modelDirectory)
    {
        foreach (var dir in new[] { modelDirectory, Path.Combine(modelDirectory, "tokenizer") })
        {
            if (File.Exists(Path.Combine(dir, "tokenizer.json")))
                return true;

            if (File.Exists(Path.Combine(dir, "vocab.json")) && File.Exists(Path.Combine(dir, "merges.txt")))
                return true;
        }

        return false;
    }

    public bool Initialize(string modelDirectory, int deviceId, ModelType modelType = ModelType.SD15,
        ExecutionProvider provider = ExecutionProvider.Cuda)
    {
        lock (_lock)
        {
            DisposeSessions();

            var error = ValidateModelDirectory(modelDirectory);
            if (error.Length > 0)
            {
                Log.Error($"[StreamDiffusion] {error}");
                return false;
            }

            CurrentModelType = modelType;

            // TAESD (tiny autoencoder) replaces the full VAE when present: ~18 ms
            // vs ~143 ms decode @512², the difference between slideshow and
            // realtime. Its exported graphs have the VaeScalingFactor compensation
            // baked in, so both boundaries behave identically.
            var vaeDecoderPath = ResolveModelFile(modelDirectory, "taesd_decoder")
                                 ?? ResolveModelFile(modelDirectory, "vae_decoder")!;
            UsingTinyVae = vaeDecoderPath.Contains("taesd", StringComparison.OrdinalIgnoreCase);

            _textEncoder = OnnxModelSession.Create(ResolveModelFile(modelDirectory, "text_encoder")!, deviceId, "TextEncoder", provider);
            _unet = OnnxModelSession.Create(ResolveModelFile(modelDirectory, "unet")!, deviceId, "UNet", provider);
            _vaeDecoder = OnnxModelSession.Create(vaeDecoderPath, deviceId,
                UsingTinyVae ? "VaeDecoder(TAESD)" : "VaeDecoder", provider);

            var vaeEncoderPath = ResolveModelFile(modelDirectory, "taesd_encoder")
                                 ?? ResolveModelFile(modelDirectory, "vae_encoder");
            if (File.Exists(vaeEncoderPath))
            {
                _vaeEncoder = OnnxModelSession.Create(vaeEncoderPath, deviceId,
                    UsingTinyVae ? "VaeEncoder(TAESD)" : "VaeEncoder", provider);
                Log.Info($"[StreamDiffusion] VAE encoder loaded successfully from: {vaeEncoderPath}", this);
            }
            else
            {
                Log.Warning($"[StreamDiffusion] VAE encoder NOT FOUND at: {vaeEncoderPath ?? "no path resolved"} - img2img disabled", this);
            }

            _tokenizer = ClipTokenizer.FromModelDirectory(modelDirectory);

            if (!IsInitialized)
            {
                Log.Error("[StreamDiffusion] Pipeline initialization failed - see log for details");
                DisposeSessions();
                return false;
            }

            // Rebuild the cached scheduler against the loaded model type. The
            // alpha-cumprod table depends only on the model type, not on the
            // per-call step count, so this is done once per Initialize.
            _scheduler = new StreamScheduler(schedulerType: RecommendedScheduler);

            // GPU-resident latent flow (CUDA/TRT only): needs the lat_scale /
            // lat_combine graphs next to the models. Opt-in via TIXL_SD_GPU_LATENTS=1 -
            // on Windows WDDM the per-run device allocations and cross-session
            // syncs currently cost far more than the CPU-latent round-trips they
            // replace (measured 65ms -> 539-888ms per frame), so the default is
            // the CPU-latent flow. Resets the sticky failure flag per init so an
            // experiment can be retried without restarting the pipeline.
            _gpuLatents?.Dispose();
            _gpuLatents = null;
            _gpuFlowFailed = false;
            if (provider is ExecutionProvider.Cuda or ExecutionProvider.TensorRt
                && Environment.GetEnvironmentVariable("TIXL_SD_GPU_LATENTS") == "1")
            {
                _gpuLatents = GpuLatentOps.TryCreate(modelDirectory, deviceId);
                if (_gpuLatents != null)
                    Log.Info("[StreamDiffusion] GPU-resident latent flow enabled (lat_scale/lat_combine)", this);
            }

            WarmupSessions();

Log.Info($"[StreamDiffusion] Pipeline ready ({_unet!.ProviderLabel}, " +
                      $"img2img: {(_vaeEncoder != null ? "yes" : "no")}, model: {CurrentModelType}) from {modelDirectory}");
            return true;
        }
    }

    /// <summary>
    /// Text-to-image generation. Returns RGBA pixel data or null (with <paramref name="error"/> set).
    /// </summary>
    public byte[]? Txt2Img(string prompt, string? negativePrompt, int width, int height,
                           int steps, float guidance, int seed, out string? error)
    {
        lock (_lock)
        {
            if (!TryPrepareDenoise(prompt, negativePrompt, steps, guidance, 1f, seed,
                                   out var condEmbedding, out var uncondEmbedding, out var scheduler,
                                   out var effectiveGuidance, out error))
            {
                return null;
            }

            try
            {
                if (_gpuLatents != null)
                {
                    try
                    {
                        long[] latentShape = { 1, 4, height / 8, width / 8 };
                        var x = GpuTensor.Cpu(CreateInitialLatents(width, height, seed), latentShape);
                        DenoiseLoopGpu(ref x, condEmbedding, uncondEmbedding, scheduler, effectiveGuidance, 0, latentShape);
                        using (x)
                        {
                            var z = _gpuLatents!.Scale(x, 1f / VaeScalingFactor);
                            using (z)
                                return DecodeToRgbaGpu(z, width, height);
                        }
                    }
                    catch (Exception ex)
                    {
                        DisableGpuFlow(ex);
                        // fall through to the CPU-latent path below
                    }
                }

                var latents = CreateInitialLatents(width, height, seed);
                DenoiseLoop(latents, condEmbedding, uncondEmbedding, scheduler, effectiveGuidance, width, height, 0);
                return DecodeToRgba(latents, width, height);
            }
            catch (Exception ex)
            {
                error = $"txt2img failed: {ex.Message}";
                Log.Error($"[StreamDiffusion] {error}");
                return null;
            }
        }
    }

    /// <summary>
    /// Image-to-image generation. Returns RGBA pixel data or null (with <paramref name="error"/> set).
    /// </summary>
    public byte[]? Img2Img(string prompt, string? negativePrompt, byte[] rgbaInput, int inputWidth, int inputHeight,
                            int width, int height, int steps, float guidance, float strength, int seed,
                            int resizeMode, float preserveDetails,
                            out string? error)
    {
        return Img2Img(prompt, negativePrompt, rgbaInput, inputWidth, inputHeight, width, height,
                       steps, guidance, strength, seed, resizeMode, preserveDetails, isBgra: false, out error);
    }

    public byte[]? Img2Img(string prompt, string? negativePrompt, byte[] rgbaInput, int inputWidth, int inputHeight,
                            int width, int height, int steps, float guidance, float strength, int seed,
                            int resizeMode, float preserveDetails, bool isBgra,
                            out string? error)
    {
        lock (_lock)
        {
            if (_vaeEncoder == null)
            {
                error = (IsInitialized ? "img2img requires vae_encoder.onnx in the model directory" : "Pipeline not initialized");
                return null;
            }

            var prepareSw = System.Diagnostics.Stopwatch.StartNew();

            if (!TryPrepareDenoise(prompt, negativePrompt, steps, guidance, strength, seed,
                                   out var condEmbedding, out var uncondEmbedding, out var scheduler,
                                   out var effectiveGuidance, out error))
            {
                if (VerboseLogging)
                    Log.Warning($"[StreamDiffusion] TryPrepareDenoise failed after {prepareSw.ElapsedMilliseconds}ms: {error}", this);
                return null;
            }

            // Derive startStep from the scheduler's ACTUAL timestep count: TryPrepareDenoise may have
            // clamped effectiveSteps below the requested `steps` (e.g. SD-Turbo at strength>=0.5),
            // and indexing Timesteps with a step computed from the raw request overflows the array.
            var startStep = scheduler.GetImg2ImgStartStepIndex(strength, scheduler.Timesteps.Length);
            if (VerboseLogging)
                Log.Info($"[StreamDiffusion] Img2img start: strength={strength}, steps={steps}, startStep={startStep}, inputSize={inputWidth}x{inputHeight}, outputSize={width}x{height}", this);

            var encodeSw = System.Diagnostics.Stopwatch.StartNew();

            if (_gpuLatents != null)
            {
                try
                {
                    return Img2ImgGpuCore(rgbaInput, inputWidth, inputHeight, width, height, strength, seed,
                        resizeMode, isBgra, condEmbedding, uncondEmbedding, scheduler, effectiveGuidance, startStep);
                }
                catch (Exception ex)
                {
                    DisableGpuFlow(ex);
                    // fall through to the CPU-latent path below
                }
            }

            var latents = strength >= 1f
                ? CreateInitialLatents(width, height, seed)
                : EncodeImage(rgbaInput, inputWidth, inputHeight, width, height, resizeMode, isBgra, out error);

            if (encodeSw.ElapsedMilliseconds > 10000)
                Log.Warning($"[StreamDiffusion] EncodeImage/CreateInitialLatents took {encodeSw.ElapsedMilliseconds}ms - slow operation detected", this);

            if (latents == null)
            {
                Log.Error("[StreamDiffusion] Failed to initialize latents for img2img", this);
                return null;
            }
            LastEncodeMs = encodeSw.Elapsed.TotalMilliseconds;

            if (VerboseLogging)
            {
                var latentType = strength >= 1f ? "random" : "encoded + noise";
                Log.Info($"[StreamDiffusion] Latents initialized: strength={strength} ({latentType}), {latents.Length} latents", this);

                Log.Info($"[StreamDiffusion] Latent stats: min={latents.Min():F4}, max={latents.Max():F4}, avg={latents.Average():F4}, " +
                         $"samples=[{latents[0]:F4}, {latents[1]:F4}, {latents[2]:F4}, {latents[3]:F4}]", this);
            }

            if (strength < 1f)
            {
                var startTimestep = scheduler.Timesteps[startStep];
                var noise = CreateGaussianLatents(latents.Length, seed);
                scheduler.AddNoiseAt(latents, noise, startTimestep);
            }

            try
            {
                var denoiseSw = System.Diagnostics.Stopwatch.StartNew();
                DenoiseLoop(latents, condEmbedding, uncondEmbedding, scheduler, effectiveGuidance, width, height, startStep);
                denoiseSw.Stop();
                LastDenoiseMs = denoiseSw.Elapsed.TotalMilliseconds;

                if (VerboseLogging)
                {
                    Log.Info($"[StreamDiffusion] Final latent stats: min={latents.Min():F4}, max={latents.Max():F4}, avg={latents.Average():F4}, " +
                             $"samples=[{latents[0]:F4}, {latents[1]:F4}, {latents[2]:F4}, {latents[3]:F4}]", this);
                }

                var decodeSw = System.Diagnostics.Stopwatch.StartNew();
                var decoded = DecodeToRgba(latents, width, height);
                decodeSw.Stop();
                LastDecodeMs = decodeSw.Elapsed.TotalMilliseconds;
                if (VerboseLogging)
                    Log.Debug($"[StreamDiffusion] VAE decode: {decodeSw.ElapsedMilliseconds}ms", this);
                if (preserveDetails > 0f && strength < 1f)
                {
                    var resampled = ResizeRgba(rgbaInput, inputWidth, inputHeight, width, height, resizeMode);
                    decoded = BlendRgba(decoded, resampled, preserveDetails, isBgra);
                }
                if (VerboseLogging)
                    Log.Info($"[StreamDiffusion] Img2img completed successfully. Output size: {width}x{height}", this);
                return decoded;
            }
            catch (Exception ex)
            {
                error = $"img2img failed: {ex.Message}";
                Log.Error($"[StreamDiffusion] {error}");
                return null;
            }
        }
    }

    // Cached text-encoder embeddings (keyed by prompt strings, invalidated on Initialize)
    private float[]? _cachedPromptEmbedding;
    private float[]? _cachedUncondEmbedding;
    private string? _cachedPrompt;
    private string? _cachedNegativePrompt;

    // Streaming mode state (residual denoise, latent reuse between frames)
    private float[]? _streamLatents;
    private string? _streamPrompt;
    private float[]? _streamEmbedding;
    private int _streamFrameIndex;
    private int _streamWidth;
    private int _streamHeight;

    /// <summary>Clears streaming state so the next <see cref="StreamStep"/> starts fresh.</summary>
    public void ResetStream()
    {
        lock (_lock)
        {
            _streamLatents = null;
            _streamPrompt = null;
            _streamEmbedding = null;
            _streamFrameIndex = 0;
        }
    }

    /// <summary>
    /// One residual-denoise streaming frame: blends the freshly encoded input
    /// latents with the previous frame's latents, runs a single UNet pass at a
    /// cyclic timestep, decodes and returns RGBA. Reuses latents between calls.
    /// </summary>
    public byte[]? StreamStep(string prompt, byte[] rgbaInput, int inputWidth, int inputHeight,
                              int width, int height, float strength, int resizeMode, int seed,
                              out string? error)
    {
        return StreamStep(prompt, rgbaInput, inputWidth, inputHeight, width, height,
                          strength, resizeMode, seed, isBgra: false, out error);
    }

    public byte[]? StreamStep(string prompt, byte[] rgbaInput, int inputWidth, int inputHeight,
                              int width, int height, float strength, int resizeMode, int seed, bool isBgra,
                              out string? error)
    {
        lock (_lock)
        {
            error = null;

            if (!IsInitialized)
            {
                error = "Pipeline not initialized";
                return null;
            }

            if (_vaeEncoder == null)
            {
                error = (IsInitialized ? "streaming requires vae_encoder.onnx in the model directory" : "Pipeline not initialized");
                return null;
            }

            try
            {
                var latentWidth = width / 8;
                var latentHeight = height / 8;
                var scheduler = _scheduler ?? new StreamScheduler(schedulerType: RecommendedScheduler);

                var hasStreamState = _streamLatents != null || _streamGpuLatents != null;
                var reset = !hasStreamState || _streamPrompt != prompt
                            || _streamWidth != width || _streamHeight != height
                            || _streamEmbedding == null;
                if (reset)
                {
                    if (_cachedPrompt == prompt && _cachedPromptEmbedding != null)
                    {
                        _streamEmbedding = _cachedPromptEmbedding;
                    }
                    else
                    {
                        _streamEmbedding = RunTextEncoder(prompt);
                        _cachedPrompt = prompt;
                        _cachedPromptEmbedding = _streamEmbedding;
                    }

                    _streamPrompt = prompt;
                    _streamWidth = width;
                    _streamHeight = height;
                    _streamFrameIndex = 0;
                    Log.Info($"[StreamDiffusion] Stream reset (prompt or size changed, {width}x{height})", this);
                }

                if (_gpuLatents != null)
                {
                    try
                    {
                        return StreamStepGpuCore(rgbaInput, inputWidth, inputHeight, width, height,
                            strength, resizeMode, seed, isBgra, scheduler, reset, _streamEmbedding!);
                    }
                    catch (Exception ex)
                    {
                        DisableGpuFlow(ex);
                        // fall through to the CPU-latent path below (reset is
                        // now effectively true since GPU stream state is gone)
                        reset = true;
                    }
                }

                var inputLatents = EncodeImage(rgbaInput, inputWidth, inputHeight, width, height, resizeMode, isBgra, out error);
                if (inputLatents == null)
                    return null;

                // Residual noise strength scales the cyclic timestep window
                var windowScale = Math.Clamp(strength, 0.05f, 1f);
                var cyclic = scheduler.GetCyclicTimestep(_streamFrameIndex);
                var timestep = Math.Clamp(
                    (int)(StreamScheduler.StreamWindowLow + (cyclic - StreamScheduler.StreamWindowLow) * windowScale),
                    1, scheduler.TrainTimestepCount - 1);
                var alpha = scheduler.AlphaCumprodAt(timestep);
                var sqrtAlpha = MathF.Sqrt(alpha);
                var sqrtOneMinusAlpha = MathF.Sqrt(1f - alpha);

                var noise = CreateGaussianLatents(inputLatents.Length, seed >= 0 ? seed + _streamFrameIndex : -1);
                var x = new float[inputLatents.Length];
                for (var i = 0; i < x.Length; i++)
                {
                    // Noise the fresh input latents, then blend with the previous frame
                    var noisyInput = sqrtAlpha * inputLatents[i] + sqrtOneMinusAlpha * noise[i];
                    x[i] = reset
                        ? noisyInput
                        : sqrtAlpha * _streamLatents![i] + sqrtOneMinusAlpha * noisyInput;
                }

                var eps = RunUnet(x, timestep, _streamEmbedding!, latentWidth, latentHeight);

                var prevTimestep = Math.Max(0, timestep - 100);
                scheduler.StepAt(x, eps, timestep, prevTimestep);

                _streamLatents = x;
                _streamFrameIndex++;

                var streamSw = System.Diagnostics.Stopwatch.StartNew();
                var decoded = DecodeToRgba(x, width, height);
                if (VerboseLogging)
                    Log.Debug($"[StreamDiffusion] StreamStep frame={_streamFrameIndex}: t={timestep}, VAE decode {streamSw.ElapsedMilliseconds}ms", this);
                return decoded;
            }
            catch (Exception ex)
            {
                error = $"stream step failed: {ex.Message}";
                Log.Error($"[StreamDiffusion] {error}");
                return null;
            }
        }
    }

    private bool TryPrepareDenoise(string prompt, string? negativePrompt, int steps, float guidance, float strength, int seed,
                                   out float[] condEmbedding, out float[]? uncondEmbedding,
                                   out StreamScheduler scheduler, out float effectiveGuidance, out string? error)
    {
        condEmbedding = Array.Empty<float>();
        uncondEmbedding = null;
        scheduler = null!;
        effectiveGuidance = 0f;
        error = null;

        if (!IsInitialized)
        {
            error = "Pipeline not initialized";
            return false;
        }

        // Use recommended steps/guidance for model type if not explicitly set
        var effectiveSteps = steps > 0 ? steps : RecommendedSteps;
        effectiveGuidance = Math.Abs(guidance) > 0.01f ? guidance : RecommendedGuidance;

        // For Turbo/LCM models, enforce step limits
        if (CurrentModelType is ModelType.SDTurbo or ModelType.SDXLTurbo or ModelType.FLUX2Klein)
        {
            effectiveSteps = Math.Clamp(effectiveSteps, 1, 8);
            effectiveGuidance = Math.Clamp(effectiveGuidance, 0f, 3f);

            // SD-Turbo needs very few steps at high strength; cap so img2img stays interactive
            if (CurrentModelType == ModelType.SDTurbo && strength >= 0.5f)
            {
                effectiveSteps = Math.Min(effectiveSteps, Math.Max(1, (int)(strength * 4)));
            }
        }

        if (_cachedPrompt == prompt && _cachedPromptEmbedding != null)
        {
            condEmbedding = _cachedPromptEmbedding;
        }
        else
        {
            condEmbedding = RunTextEncoder(prompt);
            _cachedPrompt = prompt;
            _cachedPromptEmbedding = condEmbedding;
        }

        if (effectiveGuidance > 1.001f)
        {
            var negativeKey = negativePrompt ?? string.Empty;
            if (_cachedNegativePrompt == negativeKey && _cachedUncondEmbedding != null)
            {
                uncondEmbedding = _cachedUncondEmbedding;
            }
            else
            {
                uncondEmbedding = RunTextEncoder(negativeKey);
                _cachedNegativePrompt = negativeKey;
                _cachedUncondEmbedding = uncondEmbedding;
            }
        }
        else
        {
            uncondEmbedding = null;
        }

        scheduler = _scheduler ?? new StreamScheduler(schedulerType: RecommendedScheduler);
        scheduler.SetTimesteps(effectiveSteps);

        // Decorrelate the ancestral noise stream from the initial-latent stream so the
        // two Random(seed) sequences don't reuse the same samples
        scheduler.SetSeed(seed >= 0 ? unchecked(seed + (int)0x9E3779B9) : -1);

        if (RecommendedScheduler == SchedulerType.EulerAncestral)
        {
            scheduler.SetEta(0.3f);  // Moderate stochasticity for Turbo
        }

        if (VerboseLogging)
            Log.Info($"[StreamDiffusion] Using scheduler: {RecommendedScheduler}, steps={effectiveSteps}, guidance={effectiveGuidance:F1}", this);
        return true;
    }

 private void DenoiseLoop(float[] latents, float[] condEmbedding, float[]? uncondEmbedding,
                              StreamScheduler scheduler, float guidance, int width, int height, int startStep)
    {
        var latentWidth = width / 8;
        var latentHeight = height / 8;
        var totalSteps = scheduler.Timesteps.Length;
        var stepsToRun = totalSteps - startStep;
        if (VerboseLogging)
            Log.Info($"[StreamDiffusion] DenoiseLoop: startStep={startStep}, totalSteps={totalSteps}, stepsToRun={stepsToRun}", this);

        var denoiseSw = System.Diagnostics.Stopwatch.StartNew();

        for (var step = startStep; step < scheduler.Timesteps.Length; step++)
        {
            if (denoiseSw.ElapsedMilliseconds > 30000)  // 30 second timeout per step
            {
                Log.Warning($"[StreamDiffusion] DenoiseLoop timeout after {denoiseSw.ElapsedMilliseconds}ms at step={step}", this);
                throw new TimeoutException($"DenoiseLoop timeout at step {step}");
            }

            var timestep = scheduler.Timesteps[step];
            float[] eps;

            var stepSw = System.Diagnostics.Stopwatch.StartNew();

            if (uncondEmbedding != null)
            {
                var epsUncond = RunUnet(latents, timestep, uncondEmbedding, latentWidth, latentHeight);
                var epsCond = RunUnet(latents, timestep, condEmbedding, latentWidth, latentHeight);
                // Reuse a scratch buffer for the CFG combine to avoid a fresh
                // ~256 KB allocation per step at 512x512.
                if (_epsScratch == null || _epsScratch.Length != epsCond.Length)
                    _epsScratch = new float[epsCond.Length];
                eps = _epsScratch;
                for (var i = 0; i < eps.Length; i++)
                {
                    eps[i] = epsUncond[i] + guidance * (epsCond[i] - epsUncond[i]);
                }
            }
            else
            {
                eps = RunUnet(latents, timestep, condEmbedding, latentWidth, latentHeight);
            }

            stepSw.Stop();
            if (VerboseLogging)
                Log.Debug($"[StreamDiffusion] UNet step {step}/{totalSteps}: {stepSw.ElapsedMilliseconds}ms", this);

            scheduler.Step(latents, eps, step);
        }

        denoiseSw.Stop();
        if (VerboseLogging)
            Log.Info($"[StreamDiffusion] DenoiseLoop finished: {stepsToRun} steps in {denoiseSw.ElapsedMilliseconds}ms " +
                     $"({(stepsToRun > 0 ? denoiseSw.ElapsedMilliseconds / stepsToRun : 0)}ms/step avg)", this);
    }

    private float[] CreateInitialLatents(int width, int height, int seed)
    {
        var latentWidth = width / 8;
        var latentHeight = height / 8;
        return CreateGaussianLatents(4 * latentHeight * latentWidth, seed);
    }

    private static float[] CreateGaussianLatents(int length, int seed)
    {
        var random = new Random(seed >= 0 ? seed : Random.Shared.Next());
        var latents = new float[length];

        for (var i = 0; i < latents.Length; i += 2)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            var theta = 2.0 * Math.PI * u2;
            latents[i] = (float)(radius * Math.Cos(theta));
            if (i + 1 < latents.Length)
            {
                latents[i + 1] = (float)(radius * Math.Sin(theta));
            }
        }

        return latents;
    }

    private float[] RunTextEncoder(string prompt)
    {
        var tokenIds = _tokenizer!.Encode(prompt);
        var inputName = _textEncoder!.FindInput(2, "input") ?? "input_ids";

        // Exports differ: standard diffusers uses int64 ids, some DML exports use int32
        var isInt32 = _textEncoder.Session.InputMetadata.TryGetValue(inputName, out var metadata)
                      && metadata.ElementDataType == TensorElementType.Int32;

        if (isInt32)
        {
            var int32Tensor = new DenseTensor<int>(new[] { 1, ClipTokenizer.MaxLength });
            for (var i = 0; i < tokenIds.Length; i++)
            {
                int32Tensor[0, i] = (int)tokenIds[i];
            }

            return RunEncoderTyped(int32Tensor, inputName);
        }
        else
        {
            var int64Tensor = new DenseTensor<long>(new[] { 1, ClipTokenizer.MaxLength });
            for (var i = 0; i < tokenIds.Length; i++)
            {
                int64Tensor[0, i] = tokenIds[i];
            }

            return RunEncoderTyped(int64Tensor, inputName);
        }
    }

    private float[] RunEncoderTyped<T>(DenseTensor<T> inputTensor, string inputName) where T : unmanaged
    {
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        using var outputs = _textEncoder!.Session.Run(inputs, new[] { _textEncoder.PrimaryOutputName });

        foreach (var output in outputs)
        {
            if (output.AsTensor<float>() is DenseTensor<float> dense)
            {
                var result = new float[dense.Length];
                dense.Buffer.Span.CopyTo(result);
                return result;
            }
        }

        throw new InvalidOperationException("Text encoder produced no float output");
    }

    private void ResolveUnetInputNames()
    {
        if (_unetSampleName != null)
            return;

        _unetSampleName = _unet!.FindInput(4, "sample")!;
        _unetEmbeddingName = _unet.FindInput(3, "hidden") ?? _unet.FindInput(3, "encoder");
        _unetTimestepName = ResolveTimestepInputName(out _unetTimestepIsFloat);
    }

    private void DisposeUnetBinding()
    {
        _unetBinding?.Dispose();
        _unetSampleValue?.Dispose();
        _unetEmbeddingValue?.Dispose();
        _unetTimestepValue?.Dispose();
        _unetOutputValue?.Dispose();
        _unetBinding = null;
        _unetSampleValue = null;
        _unetEmbeddingValue = null;
        _unetTimestepValue = null;
        _unetOutputValue = null;
        _unetBoundLatentWidth = 0;
        _unetBoundLatentHeight = 0;
    }

    private void EnsureUnetBinding(int latentWidth, int latentHeight, int embeddingLength)
    {
        if (_unetBinding != null && _unetBoundLatentWidth == latentWidth
            && _unetBoundLatentHeight == latentHeight && _unetEmbeddingData.Length == embeddingLength)
        {
            return;
        }

        DisposeUnetBinding();

        _unetSampleData = new float[4 * latentWidth * latentHeight];
        _unetEmbeddingData = new float[embeddingLength];

        _unetSampleValue = OrtValue.CreateTensorValueFromMemory(
            _unetSampleData, new long[] { 1, 4, latentHeight, latentWidth });
        _unetEmbeddingValue = OrtValue.CreateTensorValueFromMemory(
            _unetEmbeddingData, new long[] { 1, ClipTokenizer.MaxLength, embeddingLength / ClipTokenizer.MaxLength });
        _unetTimestepValue = _unetTimestepIsFloat
            ? OrtValue.CreateTensorValueFromMemory(_unetTimestepFData, new long[] { 1 })
            : OrtValue.CreateTensorValueFromMemory(_unetTimestepLData, new long[] { 1 });
        _unetOutputValue = OrtValue.CreateTensorValueFromMemory(
            new float[4 * latentWidth * latentHeight], new long[] { 1, 4, latentHeight, latentWidth });

        _unetBinding = _unet!.CreateIoBinder();
        _unetBoundLatentWidth = latentWidth;
        _unetBoundLatentHeight = latentHeight;
    }

    private float[] RunUnet(float[] latents, int timestep, float[] embedding, int latentWidth, int latentHeight)
    {
        ResolveUnetInputNames();

        if (!_unetBindingFailed)
        {
            try
            {
                EnsureUnetBinding(latentWidth, latentHeight, embedding.Length);

                latents.CopyTo(_unetSampleData, 0);
                embedding.CopyTo(_unetEmbeddingData, 0);
                if (_unetTimestepIsFloat)
                {
                    _unetTimestepFData[0] = timestep;
                }
                else
                {
                    _unetTimestepLData[0] = timestep;
                }

                var binding = _unetBinding!;
                binding.ClearBoundInputs();
                binding.ClearBoundOutputs();
                binding.BindInput(_unetSampleName!, _unetSampleValue!);
                binding.BindInput(_unetTimestepName!, _unetTimestepValue!);
                if (_unetEmbeddingName != null)
                {
                    binding.BindInput(_unetEmbeddingName, _unetEmbeddingValue!);
                }

                binding.BindOutput(_unet!.PrimaryOutputName, _unetOutputValue!);
                _unet.RunBound(binding);

                var span = _unetOutputValue!.GetTensorDataAsSpan<float>();
                var eps = new float[span.Length];
                span.CopyTo(eps);
                return eps;
            }
            catch (Exception ex)
            {
                _unetBindingFailed = true;
                DisposeUnetBinding();
                Log.Warning($"[StreamDiffusion] UNet IO binding failed, falling back to plain Run: {ex.Message}");
            }
        }

        return RunUnetUnbound(latents, timestep, embedding, latentWidth, latentHeight);
    }

    private float[] RunUnetUnbound(float[] latents, int timestep, float[] embedding, int latentWidth, int latentHeight)
    {
        var sampleName = _unetSampleName!;
        var embeddingName = _unetEmbeddingName;
        var timestepName = _unetTimestepName!;
        var timestepIsFloat = _unetTimestepIsFloat;

        var sampleTensor = new DenseTensor<float>(latents, new[] { 1, 4, latentHeight, latentWidth });
        var embeddingTensor = new DenseTensor<float>(embedding,
            new[] { 1, ClipTokenizer.MaxLength, embedding.Length / ClipTokenizer.MaxLength });

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(sampleName, sampleTensor) };

        if (timestepIsFloat)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(timestepName, new DenseTensor<float>(new[] { (float)timestep }, new[] { 1 })));
        }
        else
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(timestepName, new DenseTensor<long>(new[] { (long)timestep }, new[] { 1 })));
        }

        if (embeddingName != null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(embeddingName, embeddingTensor));
        }

        using var outputs = _unet!.Session.Run(inputs, new[] { _unet.PrimaryOutputName });

        foreach (var output in outputs)
        {
            if (output.AsTensor<float>() is DenseTensor<float> dense)
            {
                var eps = new float[dense.Length];
                dense.Buffer.Span.CopyTo(eps);
                return eps;
            }
        }

        throw new InvalidOperationException("UNet produced no float output");
    }

    private string ResolveTimestepInputName(out bool isFloat)
    {
        foreach (var (name, metadata) in _unet!.Session.InputMetadata)
        {
            if (name.Contains("time", StringComparison.OrdinalIgnoreCase)
                || name.Contains("step", StringComparison.OrdinalIgnoreCase))
            {
                isFloat = metadata.ElementDataType == TensorElementType.Float;
                return name;
            }
        }

        // Fall back to any remaining rank 0/1 input that is not the sample or embeddings
        foreach (var (name, metadata) in _unet.Session.InputMetadata)
        {
            if (metadata.Dimensions.Length is 0 or 1)
            {
                isFloat = metadata.ElementDataType == TensorElementType.Float;
                return name;
            }
        }

        isFloat = false;
        return "timestep";
    }

    private float[]? EncodeImage(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
                                 int resizeMode, bool isBgra, out string? error)
    {
        byte[] resampled;
        if (inputWidth == width && inputHeight == height)
        {
            resampled = rgbaInput;
        }
        else
        {
            resampled = ResizeRgba(rgbaInput, inputWidth, inputHeight, width, height, resizeMode);
        }

        _boundVaeEncoder ??= new BoundFloatSession(_vaeEncoder!, 4, "sample");
        var input = _boundVaeEncoder.Prepare(new[] { 1, 3, height, width },
                                             new[] { 1, 4, height / 8, width / 8 });
        FillEncodeInput(_boundVaeEncoder.InputBuffer, resampled, width, height, isBgra);

        _boundVaeEncoder.Run();

        var output = _boundVaeEncoder.OutputSpan;
        var latents = new float[output.Length];
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = output[i] * VaeScalingFactor;
        }

        // Validate latents - one-shot NaN keeps log spam down; zeros scan is expensive so
        // gated behind VerboseLogging.
        bool anyNaN = false;
        bool anyNonZero = false;
        for (var i = 0; i < latents.Length; i++)
        {
            if (float.IsNaN(latents[i]))
                anyNaN = true;
            else if (latents[i] != 0f)
                anyNonZero = true;
        }
        if (anyNaN && !_loggedNaNWarning)
        {
            _loggedNaNWarning = true;
            Log.Warning("[StreamDiffusion] VAE encoder output contains NaN values!", this);
        }
        if (VerboseLogging && !anyNonZero)
            Log.Warning("[StreamDiffusion] VAE encoder output is all zeros!", this);

        error = null;
        return latents;
    }

    private static void FillEncodeInput(float[] inputData, byte[] resampled, int width, int height, bool isBgra)
    {
        var plane = width * height;
        int rOffset = isBgra ? 2 : 0;
        int bOffset = isBgra ? 0 : 2;
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = rowStart + x;
                inputData[i] = resampled[i * 4 + rOffset] / 127.5f - 1f;
                inputData[plane + i] = resampled[i * 4 + 1] / 127.5f - 1f;
                inputData[2 * plane + i] = resampled[i * 4 + bOffset] / 127.5f - 1f;
            }
        });
    }

    private static byte[] ResizeRgba(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height, int resizeMode)
    {
        if (resizeMode == 2)
        {
            return ResizeRgbaPad(rgbaInput, inputWidth, inputHeight, width, height);
        }

        if (resizeMode == 1)
        {
            return ResizeRgbaCenterCrop(rgbaInput, inputWidth, inputHeight, width, height);
        }

        return ResizeRgbaStretch(rgbaInput, inputWidth, inputHeight, width, height);
    }

    private static byte[] ResizeRgbaStretch(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height)
    {
        var output = new byte[width * height * 4];
        float scaleX = (float)inputWidth / width;
        float scaleY = (float)inputHeight / height;

        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            float srcYF = y * scaleY;
            int srcY0 = (int)Math.Floor(srcYF);
            int srcY1 = Math.Min(srcY0 + 1, inputHeight - 1);
            float fy = srcYF - srcY0;

            for (var x = 0; x < width; x++)
            {
                float srcXF = x * scaleX;
                int srcX0 = (int)Math.Floor(srcXF);
                int srcX1 = Math.Min(srcX0 + 1, inputWidth - 1);
                float fx = srcXF - srcX0;

                srcX0 = Math.Clamp(srcX0, 0, inputWidth - 1);
                srcY0 = Math.Clamp(srcY0, 0, inputHeight - 1);

                var dstIdx = (y * width + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    float v00 = rgbaInput[(srcY0 * inputWidth + srcX0) * 4 + c];
                    float v10 = rgbaInput[(srcY0 * inputWidth + srcX1) * 4 + c];
                    float v01 = rgbaInput[(srcY1 * inputWidth + srcX0) * 4 + c];
                    float v11 = rgbaInput[(srcY1 * inputWidth + srcX1) * 4 + c];

                    float top = v00 + fx * (v10 - v00);
                    float bottom = v01 + fx * (v11 - v01);
                    float val = top + fy * (bottom - top);

                    output[dstIdx + c] = (byte)Math.Clamp(val, 0, 255);
                }
            }
        });

        return output;
    }

    private static byte[] ResizeRgbaCenterCrop(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height)
    {
        float targetAspect = (float)width / height;
        float sourceAspect = (float)inputWidth / inputHeight;

        int cropX, cropY, cropW, cropH;
        if (sourceAspect > targetAspect)
        {
            cropH = inputHeight;
            cropW = (int)(inputHeight * targetAspect);
            cropX = (inputWidth - cropW) / 2;
            cropY = 0;
        }
        else
        {
            cropW = inputWidth;
            cropH = (int)(inputWidth / targetAspect);
            cropX = 0;
            cropY = (inputHeight - cropH) / 2;
        }

        return ResizeRgbaBilinear(rgbaInput, inputWidth, inputHeight, cropX, cropY, cropW, cropH, width, height);
    }

    private static byte[] ResizeRgbaPad(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (int i = 0; i < output.Length; i += 4)
        {
            output[i] = 127;
            output[i + 1] = 127;
            output[i + 2] = 127;
            output[i + 3] = 255;
        }

        float scale = Math.Min((float)width / inputWidth, (float)height / inputHeight);
        int contentW = Math.Max(1, (int)(inputWidth * scale));
        int contentH = Math.Max(1, (int)(inputHeight * scale));
        int offsetX = (width - contentW) / 2;
        int offsetY = (height - contentH) / 2;

        var content = ResizeRgbaBilinear(rgbaInput, inputWidth, inputHeight, 0, 0, inputWidth, inputHeight, contentW, contentH);

        for (var y = 0; y < contentH; y++)
        {
            Array.Copy(content, (y * contentW) * 4, output, ((y + offsetY) * width + offsetX) * 4, contentW * 4);
        }

        return output;
    }

    private static byte[] ResizeRgbaBilinear(byte[] rgbaInput, int inputWidth, int inputHeight,
                                              int srcX, int srcY, int srcW, int srcH,
                                              int outWidth, int outHeight)
    {
        var output = new byte[outWidth * outHeight * 4];
        float scaleX = (float)srcW / outWidth;
        float scaleY = (float)srcH / outHeight;

        System.Threading.Tasks.Parallel.For(0, outHeight, y =>
        {
            float srcYF = srcY + y * scaleY;
            int srcY0 = (int)Math.Floor(srcYF);
            int srcY1 = Math.Min(srcY0 + 1, inputHeight - 1);
            float fy = srcYF - srcY0;
            srcY0 = Math.Clamp(srcY0, 0, inputHeight - 1);

            for (var x = 0; x < outWidth; x++)
            {
                float srcXF = srcX + x * scaleX;
                int srcX0 = (int)Math.Floor(srcXF);
                int srcX1 = Math.Min(srcX0 + 1, inputWidth - 1);
                float fx = srcXF - srcX0;
                srcX0 = Math.Clamp(srcX0, 0, inputWidth - 1);

                var dstIdx = (y * outWidth + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    float v00 = rgbaInput[(srcY0 * inputWidth + srcX0) * 4 + c];
                    float v10 = rgbaInput[(srcY0 * inputWidth + srcX1) * 4 + c];
                    float v01 = rgbaInput[(srcY1 * inputWidth + srcX0) * 4 + c];
                    float v11 = rgbaInput[(srcY1 * inputWidth + srcX1) * 4 + c];

                    float top = v00 + fx * (v10 - v00);
                    float bottom = v01 + fx * (v11 - v01);
                    float val = top + fy * (bottom - top);

                    output[dstIdx + c] = (byte)Math.Clamp(val, 0, 255);
                }
            }
        });

        return output;
    }

    private static byte[] BlendRgba(byte[] a, byte[] b, float t, bool bIsBgra)
    {
        var output = new byte[a.Length];
        var invT = 1f - t;
        int rOff = bIsBgra ? 2 : 0;
        int bOff = bIsBgra ? 0 : 2;
        for (var i = 0; i < a.Length; i += 4)
        {
            output[i + 0] = (byte)Math.Clamp(a[i + 0] * invT + b[i + rOff] * t, 0, 255);
            output[i + 1] = (byte)Math.Clamp(a[i + 1] * invT + b[i + 1] * t, 0, 255);
            output[i + 2] = (byte)Math.Clamp(a[i + 2] * invT + b[i + bOff] * t, 0, 255);
            output[i + 3] = (byte)Math.Clamp(a[i + 3] * invT + b[i + 3] * t, 0, 255);
        }

        return output;
    }

    private byte[] DecodeToRgba(float[] latents, int width, int height)
    {
        var latentWidth = width / 8;
        var latentHeight = height / 8;

        _boundVaeDecoder ??= new BoundFloatSession(_vaeDecoder!, 4, "latent");
        var input = _boundVaeDecoder.Prepare(new[] { 1, 4, latentHeight, latentWidth },
                                             new[] { 1, 3, height, width });

        // Undo the VAE scaling while filling the bound input (no temp array)
        for (var i = 0; i < latents.Length; i++)
        {
            input[i] = latents[i] / VaeScalingFactor;
        }

        _boundVaeDecoder.Run();
        return FloatsToRgba(_boundVaeDecoder.Output, width, height);
    }

    private static byte[] FloatsToRgba(ReadOnlyMemory<float> nchwMemory, int width, int height)
    {
        var plane = width * height;
        var rgba = new byte[plane * 4];
        Parallel.For(0, height, y =>
        {
            var nchw = nchwMemory.Span;
            var rowStart = y * width;
            var dstRow = rowStart * 4;
            for (var x = 0; x < width; x++)
            {
                var i = rowStart + x;
                rgba[dstRow + x * 4 + 0] = (byte)(Math.Clamp(nchw[i] + 1f, 0f, 2f) * 127.5f);
                rgba[dstRow + x * 4 + 1] = (byte)(Math.Clamp(nchw[plane + i] + 1f, 0f, 2f) * 127.5f);
                rgba[dstRow + x * 4 + 2] = (byte)(Math.Clamp(nchw[2 * plane + i] + 1f, 0f, 2f) * 127.5f);
                rgba[dstRow + x * 4 + 3] = 255;
            }
        });
        return rgba;
    }

    #region GPU-resident latent flow

    /// <summary>
    /// Permanently (for this initialization) drops the GPU-resident flow and
    /// returns to the CPU-latent path. Called on any GPU-flow exception; the
    /// failing request is then re-run on the CPU path by the caller.
    /// </summary>
    private void DisableGpuFlow(Exception ex)
    {
        Log.Warning($"[StreamDiffusion] GPU-resident latent flow failed, falling back to the CPU-latent flow " +
                    $"until the next re-initialization: {ex.Message}", this);
        _gpuFlowFailed = true;
        DisposeGpuFlow();
    }

    private void DisposeGpuFlow()
    {
        _unetGpuBinding?.Dispose();
        _unetGpuBinding = null;
        _encodeGpuBinding?.Dispose();
        _encodeGpuBinding = null;
        _decodeGpuBinding?.Dispose();
        _decodeGpuBinding = null;
        _streamGpuLatents?.Dispose();
        _streamGpuLatents = null;
        _gpuLatents?.Dispose();
        _gpuLatents = null;
    }

    private GpuTensor? EncodeImageGpu(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
        int resizeMode, bool isBgra)
    {
        byte[] resampled;
        if (inputWidth == width && inputHeight == height)
        {
            resampled = rgbaInput;
        }
        else
        {
            resampled = ResizeRgba(rgbaInput, inputWidth, inputHeight, width, height, resizeMode);
        }

        _boundVaeEncoder ??= new BoundFloatSession(_vaeEncoder!, 4, "sample");
        var input = _boundVaeEncoder.Prepare(new[] { 1, 3, height, width },
                                             new[] { 1, 4, height / 8, width / 8 });
        FillEncodeInput(_boundVaeEncoder.InputBuffer, resampled, width, height, isBgra);

        _encodeGpuBinding ??= _vaeEncoder!.CreateIoBinder();
        var binding = _encodeGpuBinding;
        binding.ClearBoundInputs();
        binding.ClearBoundOutputs();
        binding.BindInput(_boundVaeEncoder.InputName, _boundVaeEncoder.InputValue!);
        binding.BindOutputToDevice(_boundVaeEncoder.OutputName, _gpuLatents!.DeviceMemory);
        _vaeEncoder.RunBound(binding);
        return GpuTensor.FromBoundOutput(binding.GetOutputValues());
    }

    private byte[] DecodeToRgbaGpu(GpuTensor z, int width, int height)
    {
        _boundVaeDecoder ??= new BoundFloatSession(_vaeDecoder!, 4, "latent");
        // Prepare allocates/reuses the CPU output buffer the decoder writes into.
        _ = _boundVaeDecoder.Prepare(new[] { 1, 4, height / 8, width / 8 },
                                     new[] { 1, 3, height, width });

        _decodeGpuBinding ??= _vaeDecoder!.CreateIoBinder();
        var binding = _decodeGpuBinding;
        binding.ClearBoundInputs();
        binding.ClearBoundOutputs();
        binding.BindInput(_boundVaeDecoder.InputName, z.Value);
        binding.BindOutput(_boundVaeDecoder.OutputName, _boundVaeDecoder.OutputValue!);
        _vaeDecoder.RunBound(binding);
        return FloatsToRgba(_boundVaeDecoder.Output, width, height);
    }

    private GpuTensor RunUnetGpu(GpuTensor x, int timestep, float[] embedding, long[] latentShape)
    {
        ResolveUnetInputNames();
        // Reuses the CPU timestep/embedding OrtValues the standard binding
        // maintains; only the sample tensor and output live on the device.
        EnsureUnetBinding((int)latentShape[3], (int)latentShape[2], embedding.Length);

        embedding.CopyTo(_unetEmbeddingData, 0);
        if (_unetTimestepIsFloat)
        {
            _unetTimestepFData[0] = timestep;
        }
        else
        {
            _unetTimestepLData[0] = timestep;
        }

        _unetGpuBinding ??= _unet!.CreateIoBinder();
        var binding = _unetGpuBinding;
        binding.ClearBoundInputs();
        binding.ClearBoundOutputs();
        binding.BindInput(_unetSampleName!, x.Value);
        binding.BindInput(_unetTimestepName!, _unetTimestepValue!);
        if (_unetEmbeddingName != null)
        {
            binding.BindInput(_unetEmbeddingName, _unetEmbeddingValue!);
        }
        binding.BindOutputToDevice(_unet!.PrimaryOutputName, _gpuLatents!.DeviceMemory);
        _unet.RunBound(binding);
        return GpuTensor.FromBoundOutput(binding.GetOutputValues());
    }

    private void DenoiseLoopGpu(ref GpuTensor x, float[] condEmbedding, float[]? uncondEmbedding,
        StreamScheduler scheduler, float guidance, int startStep, long[] latentShape)
    {
        var totalSteps = scheduler.Timesteps.Length;
        var denoiseSw = System.Diagnostics.Stopwatch.StartNew();

        for (var step = startStep; step < totalSteps; step++)
        {
            if (denoiseSw.ElapsedMilliseconds > 30000)
            {
                throw new TimeoutException($"GPU denoise loop timeout at step {step}");
            }

            var timestep = scheduler.Timesteps[step];

            GpuTensor eps;
            if (uncondEmbedding != null)
            {
                // CFG: eps = (1 - guidance)·epsUncond + guidance·epsCond
                var epsUncond = RunUnetGpu(x, timestep, uncondEmbedding, latentShape);
                var epsCond = RunUnetGpu(x, timestep, condEmbedding, latentShape);
                eps = _gpuLatents!.Combine(epsUncond, epsCond, null, 1f - guidance, guidance, 0f);
                epsUncond.Dispose();
                epsCond.Dispose();
            }
            else
            {
                eps = RunUnetGpu(x, timestep, condEmbedding, latentShape);
            }

            var prevTimestep = step < totalSteps - 1 ? scheduler.Timesteps[step + 1] : 0;
            float xScale, epsScale, noiseScale;
            switch (scheduler.SchedulerType)
            {
                case SchedulerType.Euler:
                    scheduler.GetEulerScales(timestep, prevTimestep, out xScale, out epsScale);
                    noiseScale = 0f;
                    break;

                case SchedulerType.EulerAncestral:
                    scheduler.GetAncestralScales(timestep, prevTimestep, step >= totalSteps - 1,
                        out xScale, out epsScale, out noiseScale);
                    break;

                default:
                    scheduler.GetDDIMScales(timestep, step < totalSteps - 1 ? prevTimestep : -1,
                        out xScale, out epsScale);
                    noiseScale = 0f;
                    break;
            }

            GpuTensor? gpuNoise = null;
            if (noiseScale != 0f)
            {
                var length = (int)(latentShape[0] * latentShape[1] * latentShape[2] * latentShape[3]);
                gpuNoise = GpuTensor.Cpu(scheduler.CreateAncestralNoise(length), latentShape);
            }

            var next = _gpuLatents!.Combine(x, eps, gpuNoise, xScale, epsScale, noiseScale);
            eps.Dispose();
            gpuNoise?.Dispose();
            x.Dispose();
            x = next;
        }
    }

    private byte[] Img2ImgGpuCore(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
        float strength, int seed, int resizeMode, bool isBgra,
        float[] condEmbedding, float[]? uncondEmbedding, StreamScheduler scheduler, float guidance, int startStep)
    {
        long[] latentShape = { 1, 4, height / 8, width / 8 };
        var encodeSw = System.Diagnostics.Stopwatch.StartNew();

        GpuTensor x;
        if (strength >= 1f)
        {
            x = GpuTensor.Cpu(CreateInitialLatents(width, height, seed), latentShape);
        }
        else
        {
            var raw = EncodeImageGpu(rgbaInput, inputWidth, inputHeight, width, height, resizeMode, isBgra);
            x = _gpuLatents!.Scale(raw, VaeScalingFactor);
            raw.Dispose();
        }
        encodeSw.Stop();
        LastEncodeMs = encodeSw.Elapsed.TotalMilliseconds;

        if (strength < 1f)
        {
            var startTimestep = scheduler.Timesteps[startStep];
            var alpha = scheduler.AlphaCumprodAt(startTimestep);
            var length = (int)(latentShape[0] * latentShape[1] * latentShape[2] * latentShape[3]);
            using var noise = GpuTensor.Cpu(CreateGaussianLatents(length, seed), latentShape);
            var blended = _gpuLatents!.Combine(x, noise, null, MathF.Sqrt(alpha), MathF.Sqrt(1f - alpha), 0f);
            x.Dispose();
            x = blended;
        }

        var denoiseSw = System.Diagnostics.Stopwatch.StartNew();
        DenoiseLoopGpu(ref x, condEmbedding, uncondEmbedding, scheduler, guidance, startStep, latentShape);
        denoiseSw.Stop();
        LastDenoiseMs = denoiseSw.Elapsed.TotalMilliseconds;

        var decodeSw = System.Diagnostics.Stopwatch.StartNew();
        byte[] decoded;
        using (x)
        {
            var z = _gpuLatents!.Scale(x, 1f / VaeScalingFactor);
            using (z)
                decoded = DecodeToRgbaGpu(z, width, height);
        }
        decodeSw.Stop();
        LastDecodeMs = decodeSw.Elapsed.TotalMilliseconds;
        return decoded;
    }

    private byte[] StreamStepGpuCore(byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
        float strength, int resizeMode, int seed, bool isBgra,
        StreamScheduler scheduler, bool reset, float[] embedding)
    {
        long[] latentShape = { 1, 4, height / 8, width / 8 };

        var inputRaw = EncodeImageGpu(rgbaInput, inputWidth, inputHeight, width, height, resizeMode, isBgra);
        GpuTensor inputLatents;
        using (inputRaw)
        {
            inputLatents = _gpuLatents!.Scale(inputRaw, VaeScalingFactor);
        }

        // Residual noise strength scales the cyclic timestep window (mirrors the CPU path)
        var windowScale = Math.Clamp(strength, 0.05f, 1f);
        var cyclic = scheduler.GetCyclicTimestep(_streamFrameIndex);
        var timestep = Math.Clamp(
            (int)(StreamScheduler.StreamWindowLow + (cyclic - StreamScheduler.StreamWindowLow) * windowScale),
            1, scheduler.TrainTimestepCount - 1);
        var alpha = scheduler.AlphaCumprodAt(timestep);
        var sqrtAlpha = MathF.Sqrt(alpha);
        var sqrtOneMinusAlpha = MathF.Sqrt(1f - alpha);

        var length = (int)(latentShape[0] * latentShape[1] * latentShape[2] * latentShape[3]);
        using var noise = GpuTensor.Cpu(
            CreateGaussianLatents(length, seed >= 0 ? seed + _streamFrameIndex : -1), latentShape);

        GpuTensor x;
        if (reset)
        {
            x = _gpuLatents!.Combine(inputLatents, noise, null, sqrtAlpha, sqrtOneMinusAlpha, 0f);
            _streamGpuLatents?.Dispose();
            _streamGpuLatents = null;
        }
        else
        {
            // x = √a·prev + √a·√(1-a)·input + (1-a)·noise  (expanded CPU blend)
            x = _gpuLatents!.Combine(_streamGpuLatents!, inputLatents, noise,
                sqrtAlpha, sqrtAlpha * sqrtOneMinusAlpha, 1f - alpha);
        }
        inputLatents.Dispose();

        var eps = RunUnetGpu(x, timestep, embedding, latentShape);
        var prevTimestep = Math.Max(0, timestep - 100);
        scheduler.GetEulerScales(timestep, prevTimestep, out var xScale, out var epsScale);
        var next = _gpuLatents.Combine(x, eps, null, xScale, epsScale, 0f);
        eps.Dispose();
        x.Dispose();

        _streamGpuLatents?.Dispose();
        _streamGpuLatents = next;
        _streamFrameIndex++;

        return DecodeToRgbaGpu(next, width, height);
    }

    #endregion

    /// <summary>
    /// Compiles DirectML kernels and grows allocator pools for every session with a tiny 64x64
    /// pass at init, so the first real generation doesn't pay the cold-start hitch (observed
    /// ~4x on the first VAE encode). Failures are non-fatal — just a slower first generation.
    /// </summary>
    private void WarmupSessions()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var embedding = RunTextEncoder(string.Empty);
            RunUnet(new float[4 * 8 * 8], 500, embedding, 8, 8);
            DecodeToRgba(new float[4 * 8 * 8], 64, 64);
            if (_vaeEncoder != null)
            {
                EncodeImage(new byte[64 * 64 * 4], 64, 64, 64, 64, 0, isBgra: false, out _);
            }

            Log.Info($"[StreamDiffusion] Warmup completed in {sw.ElapsedMilliseconds}ms", this);
        }
        catch (Exception ex)
        {
            Log.Warning($"[StreamDiffusion] Warmup skipped: {ex.Message}", this);
        }
    }

    /// <summary>
    /// A single-input/single-output session (VAE encoder/decoder) with persistent IO binding:
    /// input and output live in reused pinned buffers across calls — same pattern as the UNet
    /// binding — avoiding a fresh multi-MB tensor allocation per generation. Falls back to a
    /// plain <c>Session.Run</c> once (and stays there) if the execution provider rejects binding.
    /// </summary>
    private sealed class BoundFloatSession : IDisposable
    {
        public BoundFloatSession(OnnxModelSession session, int inputRank, string? preferredInputFragment)
        {
            _session = session;
            _inputName = session.FindInput(inputRank, preferredInputFragment) ?? session.PrimaryInputName;
            _outputName = session.PrimaryOutputName;

            // Prefer the model's declared output shape over caller guesses — a pre-allocated
            // bound output OrtValue whose shape mismatches the actual graph output makes
            // RunWithBinding fail. (Note: an NRE here was once blamed on shape mismatches, but
            // the real cause was RunBound passing a null RunOptions — fixed in OnnxModelSession.)
            if (session.Session.OutputMetadata.TryGetValue(_outputName, out var outputMeta)
                && outputMeta.Dimensions is { Length: > 0 } dims)
            {
                var allFixed = true;
                foreach (var d in dims)
                {
                    if (d <= 0)
                    {
                        allFixed = false;
                        break;
                    }
                }

                if (allFixed)
                    _modelOutputShape = Array.ConvertAll(dims, v => (int)v);
            }
        }

        /// <summary>Reusable output buffer filled by <see cref="Run"/> (valid until next Prepare/Run).</summary>
        public ReadOnlyMemory<float> Output => _output;

        public ReadOnlySpan<float> OutputSpan => _output;

        // Exposed for the GPU-resident flow, which binds the same prepared
        // input buffer / output buffer through its own device-oriented bindings.
        public string InputName => _inputName;
        public string OutputName => _outputName;
        public OrtValue? InputValue => _inputValue;
        public OrtValue? OutputValue => _outputValue;
        public float[] InputBuffer => Input;

        /// <summary>
        /// Ensures bound buffers exist for the given input/output shapes and returns the input
        /// span for the caller to fill before calling <see cref="Run"/>.
        /// </summary>
        public Span<float> Prepare(int[] inputShape, int[] outputShape)
        {
            // A fully-fixed output shape declared by the model wins over the caller's guess.
            if (_modelOutputShape != null && ShapeLength(_modelOutputShape) > 0)
                outputShape = _modelOutputShape;

            if (_binding == null || !_inputShape.SequenceEqual(inputShape) || !_outputShape.SequenceEqual(outputShape))
            {
                DisposeBinding();

                var inputLength = ShapeLength(inputShape);
                var outputLength = ShapeLength(outputShape);
                Input = new float[inputLength];
                _output = new float[outputLength];
                _inputShape = (int[])inputShape.Clone();
                _outputShape = (int[])outputShape.Clone();
                _inputShapeLong = Array.ConvertAll(inputShape, v => (long)v);
                _outputShapeLong = Array.ConvertAll(outputShape, v => (long)v);

                if (!_bindingFailed)
                {
                    try
                    {
                        _inputValue = OrtValue.CreateTensorValueFromMemory(Input, _inputShapeLong);
                        _outputValue = OrtValue.CreateTensorValueFromMemory(_output, _outputShapeLong);
                        _binding = _session.CreateIoBinder();
                    }
                    catch (Exception ex)
                    {
                        _bindingFailed = true;
                        DisposeBinding();
                        Log.Warning($"[StreamDiffusion] {_session.Name}: IO binding unavailable, using plain Run: {ex.Message}");
                    }
                }
            }

            return Input;
        }

        public void Run()
        {
            if (_binding != null)
            {
                try
                {
                    _binding.ClearBoundInputs();
                    _binding.ClearBoundOutputs();
                    _binding.BindInput(_inputName, _inputValue!);
                    _binding.BindOutput(_outputName, _outputValue!);
                    _session.RunBound(_binding);
                    return;
                }
                catch (Exception ex)
                {
                    _bindingFailed = true;
                    DisposeBinding();
                    Log.Warning($"[StreamDiffusion] {_session.Name}: bound run failed, falling back to plain Run: {ex}\n" +
                                $"  input '{_inputName}' shape=[{string.Join(",", _inputShape)}], " +
                                $"output '{_outputName}' bound shape=[{string.Join(",", _outputShape)}]", _session.Session);
                }
            }

            var inputTensor = new DenseTensor<float>(Input, _inputShape);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };
            using var outputs = _session.Session.Run(inputs, new[] { _outputName });
            foreach (var output in outputs)
            {
                if (output.AsTensor<float>() is DenseTensor<float> dense)
                {
                    dense.Buffer.Span.CopyTo(_output);
                    return;
                }
            }

            throw new InvalidOperationException($"{_session.Name}: output '{_outputName}' is not a float tensor");
        }

        public void Dispose()
        {
            DisposeBinding();
            Input = Array.Empty<float>();
            _output = Array.Empty<float>();
            _inputShape = Array.Empty<int>();
            _outputShape = Array.Empty<int>();
        }

        private void DisposeBinding()
        {
            _binding?.Dispose();
            _inputValue?.Dispose();
            _outputValue?.Dispose();
            _binding = null;
            _inputValue = null;
            _outputValue = null;
        }

        private static int ShapeLength(int[] shape)
        {
            var length = 1;
            foreach (var d in shape)
                length *= d;
            return length;
        }

        public float[] Input { get; private set; } = Array.Empty<float>();
        private float[] _output = Array.Empty<float>();
        private int[] _inputShape = Array.Empty<int>();
        private int[] _outputShape = Array.Empty<int>();
        private long[] _inputShapeLong = Array.Empty<long>();
        private long[] _outputShapeLong = Array.Empty<long>();
        private readonly OnnxModelSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly int[]? _modelOutputShape;
        private OrtIoBinding? _binding;
        private OrtValue? _inputValue;
        private OrtValue? _outputValue;
        private bool _bindingFailed;
    }

    private void DisposeSessions()
    {
        DisposeGpuFlow();
        DisposeUnetBinding();
        _unetSampleName = null;
        _unetEmbeddingName = null;
        _unetTimestepName = null;
        _unetBindingFailed = false;
        _cachedPromptEmbedding = null;
        _cachedUncondEmbedding = null;
        _cachedPrompt = null;
        _cachedNegativePrompt = null;
        _streamLatents = null;
        _streamPrompt = null;
        _streamEmbedding = null;
        _streamFrameIndex = 0;
        _scheduler = null;
        _epsScratch = null;
        _loggedNaNWarning = false;
        _textEncoder?.Dispose();
        _unet?.Dispose();
        _vaeDecoder?.Dispose();
        _vaeEncoder?.Dispose();
        _textEncoder = null;
        _unet = null;
        _vaeDecoder = null;
        _vaeEncoder = null;
        _boundVaeDecoder?.Dispose();
        _boundVaeEncoder?.Dispose();
        _boundVaeDecoder = null;
        _boundVaeEncoder = null;
        _tokenizer = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeSessions();
        }
    }
}
