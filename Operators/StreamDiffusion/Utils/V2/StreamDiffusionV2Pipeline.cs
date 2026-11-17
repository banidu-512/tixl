using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using t3.streamdiffusion.Onnx;
using T3.Core.Logging;

namespace t3.streamdiffusion.V2;

/// <summary>
/// StreamDiffusion V2: the causal Wan2.1 video DiT, run as a pipelined stream-batch. Every call consumes one input frame
/// and advances all denoising steps at once, each batch element holding a different latent frame at a different noise level.
/// Keys/values of past frames live in a GPU-resident cache owned here; the exported DiT is stateless.
/// </summary>
internal sealed class StreamDiffusionV2Pipeline : IGenerationPipeline
{
    public const string DiTFileName = "dit_step.onnx";

    private const int EncoderWindowFrames = 8;
    private const int DecoderWindowLatents = 3;
    private const int FramesPerLatent = 4;
    private const int LatentChannels = 16;
    private const int TextLength = 512;
    private const int TextFeatureCount = 4096;
    private const int FirstStreamFrame = 2;
    private const int MaxFramePosition = 1000;

    private static readonly int[] DenoisingTimesteps = { 700, 500, 400, 200 };

    private static readonly string[] RequiredFiles =
    {
        DiTFileName, "dit_manifest.json", "cross_kv.onnx", "kv_zeros.onnx", "text_encoder.onnx",
        "vae_encoder.onnx", "vae_decoder.onnx", "tokenizer.json",
    };

    public bool IsInitialized => _dit != null && _tokenizer != null;
    public bool SupportsImg2Img => true;
    public string ProviderLabel => _dit?.ProviderLabel ?? "CPU";
    public string? InitializationError => _initializationError;

    public bool VerboseLogging
    {
        get => _verboseLogging;
        set => _verboseLogging = value;
    }

    public int RecommendedSteps => DenoisingTimesteps.Length;
    public float RecommendedGuidance => 1f;

    /// <summary>Per-stage wall time of the most recent stream step (milliseconds).</summary>
    public double LastEncodeMs { get; private set; }
    public double LastDenoiseMs { get; private set; }
    public double LastDecodeMs { get; private set; }

    public static string ValidateModelDirectory(string? modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
            return "Model path is empty. Select a folder with the StreamDiffusion V2 ONNX export.";

        if (!Directory.Exists(modelDirectory))
            return $"Model directory not found: {modelDirectory}";

        var missing = new List<string>();
        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(modelDirectory, file)))
                missing.Add(file);
        }

        return missing.Count == 0 ? string.Empty : $"Missing in '{modelDirectory}': {string.Join(", ", missing)}";
    }

    public bool TryGetFixedOutputSize(out int width, out int height)
    {
        width = _width;
        height = _height;
        return IsInitialized;
    }

    public void ConfigureStream(int steps)
    {
        lock (_lock)
        {
            var clamped = Math.Clamp(steps, 1, DenoisingTimesteps.Length);
            if (clamped == _stepCount)
                return;

            _stepCount = clamped;
            _needsPrime = true;
        }
    }

    public bool Initialize(string modelDirectory, int deviceId, ExecutionProvider provider)
    {
        lock (_lock)
        {
            _initializationError = null;
            var error = ValidateModelDirectory(modelDirectory);
            if (error.Length > 0)
            {
                _initializationError = error;
                Log.Error($"[StreamDiffusion] {error}");
                return false;
            }

            if (provider == ExecutionProvider.DirectMl)
            {
                _initializationError = "StreamDiffusion V2 does not support DirectML. Use CUDA or TensorRT.";
                Log.Error("[StreamDiffusion] V2 does not support DirectML: ONNX Runtime's DML provider fails the DiT's 5D " +
                          "reshapes with an internal error. Use CUDA or TensorRT.");
                return false;
            }

            if (provider == ExecutionProvider.Cpu)
            {
                // The DiT is far too large for CPU and the cache I/O is built around GPU memory
                Log.Warning("[StreamDiffusion] V2 is designed for CUDA or TensorRT, but CPU was selected. " +
                            "Expect very slow streaming; switch ExecutionProvider to CUDA.");
            }

            try
            {
                _modelDirectory = modelDirectory;
                _deviceId = deviceId;
                LoadManifest(Path.Combine(modelDirectory, "dit_manifest.json"));
                _tokenizer = Umt5Tokenizer.Load(Path.Combine(modelDirectory, "tokenizer.json"));

                // Explicit profile so one TensorRT engine covers every Steps value (the step count is the batch size)
                var ditProfile = CreateDitTensorRtProfile();
                _dit = OnnxModelSession.Create(Path.Combine(modelDirectory, DiTFileName), deviceId, "V2 DiT", provider, ditProfile);
                _crossKv = OnnxModelSession.Create(Path.Combine(modelDirectory, "cross_kv.onnx"), deviceId, "V2 CrossKV", provider);
                _kvZeros = OnnxModelSession.Create(Path.Combine(modelDirectory, "kv_zeros.onnx"), deviceId, "V2 KvZeros", provider);
                _vaeEncoder = OnnxModelSession.Create(Path.Combine(modelDirectory, "vae_encoder.onnx"), deviceId, "V2 VaeEncoder", provider);
                _vaeDecoder = OnnxModelSession.Create(Path.Combine(modelDirectory, "vae_decoder.onnx"), deviceId, "V2 VaeDecoder", provider);
                if (_dit == null || _crossKv == null || _kvZeros == null || _vaeEncoder == null || _vaeDecoder == null)
                {
                    DisposeSessions();
                    return false;
                }

                _device = _dit.ProviderLabel is "CUDA" or "TensorRT"
                              ? new OrtMemoryInfo("Cuda", OrtAllocatorType.ArenaAllocator, deviceId, OrtMemType.Default)
                              : OrtMemoryInfo.DefaultInstance;
                return true;
            }
            catch (Exception ex)
            {
                _initializationError = ex.Message;
                Log.Error($"[StreamDiffusion] V2 initialization failed: {ex.Message}");
                DisposeSessions();
                return false;
            }
        }
    }

    public byte[]? Txt2Img(string prompt, string? negativePrompt, int width, int height, int steps, float guidance, int seed, out string? error)
    {
        error = "StreamDiffusion V2 is a video model and needs an input image. Use the Stream mode.";
        return null;
    }

    public byte[]? Img2Img(string prompt, string? negativePrompt, byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
                           int steps, float guidance, float strength, int seed, int resizeMode, float preserveDetails, bool isBgra,
                           out string? error)
    {
        ConfigureStream(steps);
        return StreamStep(prompt, rgbaInput, inputWidth, inputHeight, width, height, strength, resizeMode, seed, isBgra, out error);
    }

    public byte[]? StreamStep(string prompt, byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
                              float strength, int resizeMode, int seed, bool isBgra, out string? error)
    {
        lock (_lock)
        {
            error = null;
            if (!IsInitialized)
            {
                error = "Pipeline not initialized";
                return null;
            }

            var stage = "resize";
            try
            {
                var resized = StableDiffusionPipeline.ResizeRgba(rgbaInput, inputWidth, inputHeight, _width, _height, resizeMode);
                var frameChange = PushFrame(resized, isBgra);

                if (!string.Equals(prompt, _preparedPrompt, StringComparison.Ordinal))
                {
                    stage = "prompt";
                    PreparePrompt(prompt);
                }

                if (seed != _lastSeed)
                {
                    _lastSeed = seed;
                    _needsPrime = true;
                }

                var initialNoiseScale = Math.Clamp(strength, 0.3f, 1f);
                if (_needsPrime || _newestFrame >= MaxFramePosition)
                {
                    stage = "prime";
                    Prime(seed, initialNoiseScale);
                }

                var (noiseScale, timestep) = MotionNoiseController.Update(frameChange, _noiseScale, initialNoiseScale);
                _noiseScale = noiseScale;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                stage = "encode";
                var clean = EncodeLatestFrame();
                LastEncodeMs = stopwatch.Elapsed.TotalMilliseconds;

                stopwatch.Restart();
                stage = "denoise";
                var finished = Step(clean, noiseScale, timestep);
                LastDenoiseMs = stopwatch.Elapsed.TotalMilliseconds;

                // Until the pipeline is full there is no denoised frame yet; the encoded input stands in so the output never goes blank
                stopwatch.Restart();
                stage = "decode";
                var rgba = DecodeToRgba(finished ?? clean, finished != null);
                LastDecodeMs = stopwatch.Elapsed.TotalMilliseconds;

                if (VerboseLogging)
                    Log.Debug($"[StreamDiffusion] V2 encode {LastEncodeMs:F0} ms, denoise {LastDenoiseMs:F0} ms, decode {LastDecodeMs:F0} ms");

                return rgba;
            }
            catch (Exception ex)
            {
                Log.Error($"[StreamDiffusion] V2 {stage} failed: {ex}");
                error = $"V2 {stage} error: {ex.Message}";
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeStreamState();
            DisposeSessions();
        }
    }

    private void LoadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        _latentHeight = root.GetProperty("latentHeight").GetInt32();
        _latentWidth = root.GetProperty("latentWidth").GetInt32();
        _tokensPerFrame = root.GetProperty("tokensPerFrame").GetInt32();
        _cacheFrames = root.GetProperty("cacheFrames").GetInt32();
        _sinkFrames = root.GetProperty("sinkFrames").GetInt32();
        _layers = root.GetProperty("layers").GetInt32();
        _heads = root.GetProperty("heads").GetInt32();
        _headDim = root.GetProperty("headDim").GetInt32();
        _width = _latentWidth * 8;
        _height = _latentHeight * 8;
        _latentLength = LatentChannels * _latentHeight * _latentWidth;
        _frameLength = 3 * _width * _height;
    }

    /// <summary>
    /// The DiT's batch axis is the step count (1-4). TensorRT would otherwise build a multi-GB engine for the exact
    /// traced batch and rebuild it whenever <c>Steps</c> changes; this profile covers the whole range with one engine.
    /// </summary>
    private OnnxModelSession.TensorRtProfile CreateDitTensorRtProfile()
    {
        var cacheTokens = _cacheFrames * _tokensPerFrame;

        string Shapes(int batch)
        {
            return $"latent:{batch}x{LatentChannels}x{_latentHeight}x{_latentWidth}," +
                   $"timestep:{batch},sigma:{batch},frame_pos:{batch}," +
                   $"k_cache:{_layers}x{batch}x{cacheTokens}x{_heads}x{_headDim}," +
                   $"v_cache:{_layers}x{batch}x{cacheTokens}x{_heads}x{_headDim}," +
                   $"cache_mask:{batch}x{cacheTokens},slot:{batch}," +
                   $"cross_k:{_layers}x1x{TextLength}x{_heads}x{_headDim}," +
                   $"cross_v:{_layers}x1x{TextLength}x{_heads}x{_headDim}";
        }

        return new OnnxModelSession.TensorRtProfile(
            Shapes(1), Shapes(Math.Min(2, DenoisingTimesteps.Length)), Shapes(DenoisingTimesteps.Length));
    }

    /// <summary>Appends a frame to the encoder window and returns the RMS change against the previous frame in [-1, 1] units.</summary>
    private float PushFrame(byte[] rgba, bool isBgra)
    {
        if (_frames[0].Length != _frameLength)
        {
            for (var index = 0; index < EncoderWindowFrames; index++)
                _frames[index] = new float[_frameLength];
        }

        var frame = _frames[_frameWriteIndex];
        var previous = _frameCount > 0 ? _frames[(_frameWriteIndex + EncoderWindowFrames - 1) % EncoderWindowFrames] : null;
        var plane = _width * _height;
        var redOffset = isBgra ? 2 : 0;
        var blueOffset = isBgra ? 0 : 2;
        for (var pixel = 0; pixel < plane; pixel++)
        {
            frame[pixel] = rgba[pixel * 4 + redOffset] / 255f;
            frame[plane + pixel] = rgba[pixel * 4 + 1] / 255f;
            frame[2 * plane + pixel] = rgba[pixel * 4 + blueOffset] / 255f;
        }

        var change = 0f;
        if (previous != null)
        {
            // A sparse sample is plenty to tell a cut or fast motion from a still scene
            var squared = 0.0;
            var samples = 0;
            for (var index = 0; index < _frameLength; index += 61)
            {
                var difference = (frame[index] - previous[index]) * 2f;
                squared += difference * difference;
                samples++;
            }

            change = (float)Math.Sqrt(squared / Math.Max(1, samples));
        }
        else
        {
            // Seed the whole window so the first encode sees a static scene instead of black frames
            for (var index = 0; index < EncoderWindowFrames; index++)
            {
                if (index != _frameWriteIndex)
                    Array.Copy(frame, _frames[index], _frameLength);
            }
        }

        _frameWriteIndex = (_frameWriteIndex + 1) % EncoderWindowFrames;
        _frameCount++;
        return change;
    }

    private float[] EncodeLatestFrame()
    {
        // After a push the write index points at the oldest frame, so reading from it yields oldest to newest
        if (_encoderInput.Length != EncoderWindowFrames * _frameLength)
            _encoderInput = new float[EncoderWindowFrames * _frameLength];

        var input = _encoderInput;
        for (var slot = 0; slot < EncoderWindowFrames; slot++)
        {
            var source = _frames[(_frameWriteIndex + slot) % EncoderWindowFrames];
            Array.Copy(source, 0, input, slot * _frameLength, _frameLength);
        }

        var tensor = new DenseTensor<float>(input, new[] { 1, EncoderWindowFrames, 3, _height, _width });
        using var outputs = _vaeEncoder!.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(_vaeEncoder.PrimaryInputName, tensor) });
        var latents = outputs.First().AsTensor<float>().ToArray();

        var clean = new float[_latentLength];
        Array.Copy(latents, latents.Length - _latentLength, clean, 0, _latentLength);
        return clean;
    }

    private byte[] DecodeToRgba(float[] latent, bool isDenoised)
    {
        int windowCount;
        if (isDenoised)
        {
            if (_decodeCount < DecoderWindowLatents)
                _decodeCount++;

            for (var index = 0; index < _decodeRing.Length - 1; index++)
                _decodeRing[index] = _decodeRing[index + 1];

            _decodeRing[^1] = latent;
            windowCount = _decodeCount;
        }
        else
        {
            windowCount = 1;
        }

        var input = new float[windowCount * _latentLength];
        for (var index = 0; index < windowCount; index++)
        {
            var source = isDenoised ? _decodeRing[_decodeRing.Length - windowCount + index] : latent;
            Array.Copy(source, 0, input, index * _latentLength, _latentLength);
        }

        var tensor = new DenseTensor<float>(input, new[] { 1, windowCount, LatentChannels, _latentHeight, _latentWidth });
        using var outputs = _vaeDecoder!.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(_vaeDecoder.PrimaryInputName, tensor) });
        var frames = outputs.First().AsTensor<float>().ToArray();

        var plane = _width * _height;
        var lastFrameOffset = (windowCount * FramesPerLatent - 1) * 3 * plane;
        var rgba = new byte[plane * 4];
        for (var pixel = 0; pixel < plane; pixel++)
        {
            rgba[pixel * 4 + 0] = ToByte(frames[lastFrameOffset + pixel]);
            rgba[pixel * 4 + 1] = ToByte(frames[lastFrameOffset + plane + pixel]);
            rgba[pixel * 4 + 2] = ToByte(frames[lastFrameOffset + 2 * plane + pixel]);
            rgba[pixel * 4 + 3] = 255;
        }

        return rgba;
    }

    private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);

    private void PreparePrompt(string prompt)
    {
        var ids = _tokenizer!.Encode(prompt, TextLength);
        var length = Math.Min(TextLength, Math.Max(16, (ids.Length + 15) / 16 * 16));

        var idTensor = new DenseTensor<long>(new[] { 1, length });
        var maskTensor = new DenseTensor<long>(new[] { 1, length });
        for (var index = 0; index < ids.Length; index++)
        {
            idTensor[0, index] = ids[index];
            maskTensor[0, index] = 1;
        }

        // The encoder is about as large as the rest of the pipeline together, so it only lives for the duration of one prompt change.
        // Optimizations stay off because constant folding would expand its bf16 weights to fp32 (twice the memory).
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
            // Prompt changes load this on the worker; leave cores for the render thread instead of saturating the CPU
            IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
        };
        using var textEncoder = new InferenceSession(Path.Combine(_modelDirectory!, "text_encoder.onnx"), options);
        using var encoded = textEncoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("ids", idTensor),
            NamedOnnxValue.CreateFromTensor("mask", maskTensor),
        });
        var embeddings = encoded.First().AsTensor<float>().ToArray();

        // Rows beyond the prompt are zero in the reference, and the DiT still attends over all 512 rows
        var padded = new float[TextLength * TextFeatureCount];
        Array.Copy(embeddings, padded, Math.Min(embeddings.Length, ids.Length * TextFeatureCount));

        using var embeddingValue = OrtValue.CreateTensorValueFromMemory(padded, new long[] { 1, TextLength, TextFeatureCount });
        using var binding = _crossKv!.CreateIoBinder();
        binding.BindInput("prompt_embeds", embeddingValue);
        binding.BindOutputToDevice("cross_k", _device!);
        binding.BindOutputToDevice("cross_v", _device!);
        _crossKv.RunBound(binding);
        binding.SynchronizeBoundOutputs();

        var previous = _crossOutputs;
        _crossOutputs = binding.GetOutputValues();
        previous?.Dispose();
        _preparedPrompt = prompt;
    }

    private void Prime(int seed, float initialNoiseScale)
    {
        DisposeStreamState();

        _batch = Math.Clamp(_stepCount, 1, DenoisingTimesteps.Length);
        _random = seed >= 0 ? new Random(seed) : new Random();
        _hidden = new float[_batch][];
        _caches = new KvCacheState[_batch];
        for (var index = 0; index < _batch; index++)
        {
            _hidden[index] = new float[_latentLength];
            _caches[index] = new KvCacheState(_cacheFrames);
        }

        _decodeRing = new float[DecoderWindowLatents][];
        _decodeCount = 0;

        var shape = new long[] { _layers, _batch, _cacheFrames * _tokensPerFrame, _heads, _headDim };
        var keys = AllocateZeroedCache(shape);
        var values = AllocateZeroedCache(shape);
        _cacheOwners.Add(keys);
        _cacheOwners.Add(values);
        _cacheKeys = keys[0];
        _cacheValues = values[0];

        float[] clean;
        try
        {
            clean = EncodeLatestFrame();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"VaeEncoder: {ex.Message}", ex);
        }

        var startTimestep = (int)(1000f * initialNoiseScale) - 100;
        var noise = new float[_latentLength];
        var current = new float[_latentLength];

        // The first frames are denoised alone with every batch element running the same step, so the caches end up identical
        // across the batch before pipelined streaming starts
        for (var frame = 0; frame < FirstStreamFrame; frame++)
        {
            FillGaussian(noise);
            for (var index = 0; index < _latentLength; index++)
                current[index] = noise[index] * initialNoiseScale + clean[index] * (1f - initialNoiseScale);

            var slot = KvSlotPolicy.SlotForFrame(frame, _cacheFrames, _sinkFrames);
            for (var step = 0; step < _batch; step++)
            {
                var timestep = step == 0 ? startTimestep : DenoisingTimesteps[step];
                var timesteps = new float[_batch];
                var sigmas = new float[_batch];
                var framePositions = new long[_batch];
                var slots = new long[_batch];
                var latents = new float[_batch * _latentLength];
                for (var element = 0; element < _batch; element++)
                {
                    timesteps[element] = timestep;
                    sigmas[element] = _schedule.SigmaAt(timestep);
                    framePositions[element] = frame;
                    slots[element] = slot;
                    Array.Copy(current, 0, latents, element * _latentLength, _latentLength);
                }

                float[] prediction;
                try
                {
                    prediction = RunDit(latents, timesteps, sigmas, framePositions, slots);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Dit: {ex.Message}", ex);
                }

                for (var element = 0; element < _batch; element++)
                    _caches[element].MarkWritten(slot, frame);

                if (step < _batch - 1)
                {
                    FillGaussian(noise);
                    _schedule.AddNoise(prediction.AsSpan(0, _latentLength), noise, DenoisingTimesteps[step + 1], current);
                }
            }
        }

        _newestFrame = FirstStreamFrame - 1;
        _noiseScale = initialNoiseScale;
        _needsPrime = false;
    }

    /// <summary>Runs one pipelined step. Returns the finished latent of the oldest batch element, or null while the pipeline fills.</summary>
    private float[]? Step(float[] clean, float noiseScale, int newestTimestep)
    {
        var noise = new float[_latentLength];
        FillGaussian(noise);
        var newest = _hidden[0];
        for (var index = 0; index < _latentLength; index++)
            newest[index] = noise[index] * noiseScale + clean[index] * (1f - noiseScale);

        _newestFrame++;

        var timesteps = new float[_batch];
        var sigmas = new float[_batch];
        var framePositions = new long[_batch];
        var slots = new long[_batch];
        var latents = new float[_batch * _latentLength];
        var active = new bool[_batch];
        for (var element = 0; element < _batch; element++)
        {
            var timestep = element == 0 ? newestTimestep : DenoisingTimesteps[element];
            var frame = _newestFrame - element;
            active[element] = frame >= FirstStreamFrame;

            timesteps[element] = timestep;
            sigmas[element] = _schedule.SigmaAt(timestep);
            framePositions[element] = Math.Max(frame, 0);

            // Idle elements write into an empty slot so they cannot disturb the primed frames
            slots[element] = active[element]
                                 ? KvSlotPolicy.SlotForFrame(frame, _cacheFrames, _sinkFrames)
                                 : Math.Max(_caches[element].FindEmptySlot(), 0);
            Array.Copy(_hidden[element], 0, latents, element * _latentLength, _latentLength);
        }

        var prediction = RunDit(latents, timesteps, sigmas, framePositions, slots);

        for (var element = 0; element < _batch; element++)
        {
            if (active[element])
                _caches[element].MarkWritten((int)slots[element], _newestFrame - element);
        }

        float[]? finished = null;
        var lastElement = _batch - 1;
        if (active[lastElement])
        {
            finished = new float[_latentLength];
            Array.Copy(prediction, lastElement * _latentLength, finished, 0, _latentLength);
        }

        for (var element = lastElement - 1; element >= 0; element--)
        {
            FillGaussian(noise);
            _schedule.AddNoise(prediction.AsSpan(element * _latentLength, _latentLength), noise,
                               DenoisingTimesteps[element + 1], _hidden[element + 1]);
        }

        return finished;
    }

    /// <summary>Runs the DiT for the whole batch, swaps in the updated caches and returns the denoised latents.</summary>
    private float[] RunDit(float[] latents, float[] timesteps, float[] sigmas, long[] framePositions, long[] slots)
    {
        var cacheTokens = _cacheFrames * _tokensPerFrame;
        var mask = new float[_batch * cacheTokens];
        for (var element = 0; element < _batch; element++)
            _caches[element].FillMask(mask.AsSpan(element * cacheTokens, cacheTokens), _tokensPerFrame, (int)slots[element]);

        var batchShape = new long[] { _batch };
        using var latentValue = OrtValue.CreateTensorValueFromMemory(latents, new long[] { _batch, LatentChannels, _latentHeight, _latentWidth });
        using var timestepValue = OrtValue.CreateTensorValueFromMemory(timesteps, batchShape);
        using var sigmaValue = OrtValue.CreateTensorValueFromMemory(sigmas, batchShape);
        using var positionValue = OrtValue.CreateTensorValueFromMemory(framePositions, batchShape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, new long[] { _batch, cacheTokens });
        using var slotValue = OrtValue.CreateTensorValueFromMemory(slots, batchShape);

        using var binding = _dit!.CreateIoBinder();
        binding.BindInput("latent", latentValue);
        binding.BindInput("timestep", timestepValue);
        binding.BindInput("sigma", sigmaValue);
        binding.BindInput("frame_pos", positionValue);
        binding.BindInput("k_cache", _cacheKeys!);
        binding.BindInput("v_cache", _cacheValues!);
        binding.BindInput("cache_mask", maskValue);
        binding.BindInput("slot", slotValue);
        binding.BindInput("cross_k", _crossOutputs![0]);
        binding.BindInput("cross_v", _crossOutputs![1]);
        binding.BindOutputToDevice("x0", OrtMemoryInfo.DefaultInstance);
        binding.BindOutputToDevice("k_cache_out", _device!);
        binding.BindOutputToDevice("v_cache_out", _device!);
        _dit.RunBound(binding);
        binding.SynchronizeBoundOutputs();

        var outputs = binding.GetOutputValues();
        var prediction = outputs[0].GetTensorDataAsSpan<float>().ToArray();

        // The cache outputs stay on the device and become the next call's inputs; everything older can go
        foreach (var owner in _cacheOwners)
            owner.Dispose();

        _cacheOwners.Clear();
        _cacheOwners.Add(outputs);
        _cacheKeys = outputs[1];
        _cacheValues = outputs[2];
        return prediction;
    }

    private IDisposableReadOnlyCollection<OrtValue> AllocateZeroedCache(long[] shape)
    {
        using var shapeValue = OrtValue.CreateTensorValueFromMemory(shape, new long[] { shape.Length });
        using var binding = _kvZeros!.CreateIoBinder();
        binding.BindInput("shape", shapeValue);
        binding.BindOutputToDevice("zeros", _device!);
        _kvZeros.RunBound(binding);
        binding.SynchronizeBoundOutputs();
        return binding.GetOutputValues();
    }

    private void FillGaussian(float[] destination)
    {
        for (var index = 0; index < destination.Length; index += 2)
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            destination[index] = (float)(radius * Math.Cos(2.0 * Math.PI * u2));
            if (index + 1 < destination.Length)
                destination[index + 1] = (float)(radius * Math.Sin(2.0 * Math.PI * u2));
        }
    }

    private void DisposeStreamState()
    {
        foreach (var owner in _cacheOwners)
            owner.Dispose();

        _cacheOwners.Clear();
        _cacheKeys = null;
        _cacheValues = null;
        _needsPrime = true;
    }

    private void DisposeSessions()
    {
        _crossOutputs?.Dispose();
        _crossOutputs = null;
        _dit?.Dispose();
        _crossKv?.Dispose();
        _kvZeros?.Dispose();
        _vaeEncoder?.Dispose();
        _vaeDecoder?.Dispose();
        _dit = null;
        _crossKv = null;
        _kvZeros = null;
        _vaeEncoder = null;
        _vaeDecoder = null;
        _preparedPrompt = null;

        // The CUDA memory info owns a native handle; DefaultInstance is process-wide and must stay alive
        if (!ReferenceEquals(_device, OrtMemoryInfo.DefaultInstance))
            (_device as IDisposable)?.Dispose();

        _device = null;
    }

    private readonly object _lock = new();
    private volatile bool _verboseLogging;
    private readonly FlowMatchSchedule _schedule = new();
    private readonly float[][] _frames = { Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(),
                                           Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>() };
    private readonly List<IDisposableReadOnlyCollection<OrtValue>> _cacheOwners = new();

    private OnnxModelSession? _dit;
    private OnnxModelSession? _crossKv;
    private OnnxModelSession? _kvZeros;
    private OnnxModelSession? _vaeEncoder;
    private OnnxModelSession? _vaeDecoder;
    private Umt5Tokenizer? _tokenizer;
    private OrtMemoryInfo? _device;
    private string? _modelDirectory;
    private int _deviceId;

    private int _latentHeight;
    private int _latentWidth;
    private int _tokensPerFrame;
    private int _cacheFrames;
    private int _sinkFrames;
    private int _layers;
    private int _heads;
    private int _headDim;
    private int _width;
    private int _height;
    private int _latentLength;
    private int _frameLength;

    private int _stepCount = DenoisingTimesteps.Length;
    private int _batch;
    private bool _needsPrime = true;
    private int _lastSeed = int.MinValue;
    private int _newestFrame;
    private float _noiseScale = MotionNoiseController.DefaultNoiseScale;
    private Random _random = new();

    private float[] _encoderInput = Array.Empty<float>();
    private int _frameWriteIndex;
    private int _frameCount;
    private float[][] _hidden = Array.Empty<float[]>();
    private KvCacheState[] _caches = Array.Empty<KvCacheState>();
    private float[][] _decodeRing = new float[DecoderWindowLatents][];
    private int _decodeCount;

    private string? _preparedPrompt;
    private string? _initializationError;
    private IDisposableReadOnlyCollection<OrtValue>? _crossOutputs;
    private OrtValue? _cacheKeys;
    private OrtValue? _cacheValues;
}
