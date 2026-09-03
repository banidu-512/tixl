using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core.Logging;
using T3.Core.Resource;
using T3.Core.Resource.Assets;
using T3.Core.Operator.Interfaces;

#pragma warning disable CS8981 // Disable warning about type name 'depthanything' being lowercase only

namespace Lib.io.video.depthanything;

internal sealed class DepthEstimationRequest
{
    public byte[]? PixelData;
    public int Width;
    public int Height;
    public bool IsBgra;
}

internal sealed class DepthEstimationResult
{
    public float[]? DepthData;
    public byte[]? ColorData;
    public int Width;
    public int Height;
    public float Min;
    public float Max;
    public DepthAnything.DepthOutputFormat ColorFormat;
    public bool ColorEnhance;
    public bool ColorInvert;
    public string? Error;
}

[Guid("c8f7d6e5-4b3a-2c1d-9f8e-7a6b5c4d3e2f")]
[ExportDependencies("Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "DirectML.dll")]
public class DepthAnything : Instance<DepthAnything>, ICustomDropdownHolder
{
    #region Outputs

    [Output(Guid = "d9e8f7a6-5c4b-3d2e-0f9e-8b7a6c5d4e3f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> OutputTexture = new();

    [Output(Guid = "e0f9e8b7-6d5c-4e3f-1a0f-9c8b7a6d5e4f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> DepthTexture = new();

    [Output(Guid = "f1a0f9c8-7e6d-5f4e-2b1a-0d9c8b7a6e5f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> NormalizedDepthTexture = new();

    [Output(Guid = "a2b1c0d9-8f7e-6d5c-3c2b-1e0d9c8b7f6a", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    [Output(Guid = "b3c2d1e0-0f9f-8e7d-4d3c-2f1e0d9c8a7b", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> MinDepth = new();

    [Output(Guid = "c4d3e2f1-1a0b-9f8e-5e4d-3f2e1d0b9a8c", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> MaxDepth = new();

    [Output(Guid = "d5e6f7a2-1b2c-0d9e-8f7a-6b5c4d3e2f1a", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> Status = new();

    #endregion

    // Everything a worker generation touches exclusively; isolates an exiting
    // worker from its replacement during model/provider switches
    private sealed class WorkerContext
    {
        public InferenceSession? Session;
        public string InputName = "pixel_values";
        public string OutputName = "predicted_depth";
        public int DeclaredSize;
        public float[]? PreprocessBuffer;
        public bool LoggedFirstInference;
    }

    public DepthAnything()
    {
        OutputTexture.UpdateAction = Update;
        DepthTexture.UpdateAction = Update;
        NormalizedDepthTexture.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
        MinDepth.UpdateAction = Update;
        MaxDepth.UpdateAction = Update;
        Status.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var pendingStatus = _pendingStatus;
        if (pendingStatus != null)
        {
            Status.Value = pendingStatus;
            _pendingStatus = null;
        }

        var inputDirty = InputTexture.DirtyFlag.IsDirty;
        var inputTexture = InputTexture.GetValue(context);
        var enabled = Enabled.GetValue(context);
        var debug = Debug.GetValue(context);
        var modelSize = (ModelSize)ModelSizeParam.GetValue(context);
        var provider = (ExecutionProvider)ExecutionProviderParam.GetValue(context);
        _requestedInputSize = ((Resolution)InputResolution.GetValue(context)) switch
        {
            Resolution.R196 => 196,
            Resolution.R280 => 280,
            Resolution.R392 => 392,
            Resolution.R518 => 518,
            _ => 0
        };
        var outputFormat = (DepthOutputFormat)OutputFormat.GetValue(context);
        var enhanceContrast = EnhanceContrast.GetValue(context);
        var invertDepth = InvertDepth.GetValue(context);
        var matchRes = MatchInputResolution.GetValue(context);
        _matchInputResolution = matchRes;
        _workerOutputFormat = outputFormat;
        _workerEnhanceContrast = enhanceContrast;
        _workerInvertDepth = invertDepth;

        // Store values for UI dropdowns
        _uiOutputFormat = (int)outputFormat;
        _uiModelSize = (int)modelSize;
        _uiInputResolution = (int)InputResolution.GetValue(context);
        _uiExecutionProvider = (int)provider;

        if (!enabled || inputTexture == null || inputTexture.IsDisposed)
        {
            OutputTexture.Value = inputTexture;
            Status.Value = "Disabled";
            PauseProcessing();
            ClearOutputs();
            return;
        }

        OutputTexture.Value = inputTexture;

        if (!_initFailed && (_processingTask == null || _processingTask.IsCompleted || modelSize != _activeModelSize || provider != _activeProvider))
        {
            InitializeWorker(debug, modelSize, provider);
        }

        if (_onnxSession == null)
        {
            if (!_isInitializing)
            {
                Status.Value = "Failed to initialize ONNX Runtime";
                ClearOutputs();
            }

            return;
        }

        // Only infer when the input texture actually changed; changes that arrive
        // while the worker is busy are latched and picked up when it frees
        if (inputDirty || !ReferenceEquals(inputTexture, _lastInputTexture))
        {
            _hasPendingInput = true;
        }

        if (_hasPendingInput && !_requestInFlight && _inputQueue.Count == 0 && !_readbackPending)
        {
            BeginReadback(inputTexture);
        }

        if (_readbackPending)
        {
            var request = CompleteReadback();
            if (request != null)
            {
                _requestInFlight = true;
                _hasPendingInput = false;
                _lastInputTexture = inputTexture;
                _inputQueue.Enqueue(request);
                _workerWake.Set();
            }
        }

        while (_outputQueue.TryDequeue(out var result))
        {
            if (result == null)
                continue;

            if (result.Error != null)
            {
                Status.Value = "Error: " + result.Error;
                ReturnResultBuffers(result);
            }
            else if (result.DepthData != null)
            {
                ReturnResultBuffers(_currentResult);
                _currentResult = result;
                _updateCount++;
                Status.Value = $"Processing {_updateCount}";
            }
            else
            {
                ReturnResultBuffers(result);
            }
        }

        if (_currentResult != null && _currentResult.DepthData != null)
        {
            // Regenerating the textures is a full-res upload -
            // skip it while neither the data nor the formatting inputs changed
            if (_currentResult != _appliedResult || outputFormat != _appliedFormat || enhanceContrast != _appliedEnhance || invertDepth != _appliedInvert || matchRes != _appliedMatchRes)
            {
                if (_currentResult == _appliedResult && debug)
                {
                    Log.Debug($"[DepthAnything] Reapplying outputs - format={outputFormat}, enhance={enhanceContrast}, invert={invertDepth}", this);
                }

                ApplyResult(_currentResult, outputFormat, enhanceContrast, invertDepth);
                _appliedResult = _currentResult;
                _appliedFormat = outputFormat;
                _appliedEnhance = enhanceContrast;
                _appliedInvert = invertDepth;
                _appliedMatchRes = matchRes;
            }

            UpdateCount.Value = _updateCount;
        }
        else
        {
            ClearOutputs();
        }
    }

    private void ClearOutputs()
    {
        ReturnResultBuffers(_currentResult);
        _currentResult = null;
        _appliedResult = null;
        DepthTexture.Value = null;
        NormalizedDepthTexture.Value = null;
        MinDepth.Value = 0.0f;
        MaxDepth.Value = 0.0f;
        if (string.IsNullOrEmpty(Status.Value))
            Status.Value = "No data";
    }

    private void ReturnResultBuffers(DepthEstimationResult? result)
    {
        if (result == null)
            return;

        ReturnDepthBuffer(result.DepthData);
        ReturnBufferToPool(result.ColorData);
        result.DepthData = null;
        result.ColorData = null;
    }

    #region ONNX Runtime Integration

    // Dynamic-input ViT models require a size divisible by the patch size (14)
    private const int DefaultInputSize = 518;
    private const int ReadbackTimeoutFrames = 60;

    // Liveness signal for the render thread; the owning worker creates and disposes it
    private InferenceSession? _onnxSession;
    private readonly object _onnxLock = new object();

    private Task? _processingTask;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentQueue<DepthEstimationRequest> _inputQueue = new();
    private readonly ConcurrentQueue<DepthEstimationResult?> _outputQueue = new();
    private DepthEstimationResult? _currentResult;
    private readonly object _workerLock = new object();
    private ModelSize _activeModelSize;
    private int _updateCount;

    private volatile bool _isInitializing;
    private volatile bool _initFailed;
    private volatile bool _requestInFlight;
    private bool _usingDirectMl;

    // Render-thread only
    private bool _hasPendingInput;
    private Texture2D? _lastInputTexture;
    private ExecutionProvider _activeProvider;
    private bool _appliedMatchRes;

    // Volatile settings the worker reads when producing a result
    private volatile bool _matchInputResolution;
    private volatile DepthOutputFormat _workerOutputFormat;
    private volatile bool _workerEnhanceContrast;
    private volatile bool _workerInvertDepth;

    // Requested inference size from the Resolution input, 0 = auto
    private volatile int _requestedInputSize;

    // Wakes the worker instead of polling; never disposed - an exiting worker may still wait on it
    private readonly AutoResetEvent _workerWake = new(false);
    private int _workerGeneration;
    private volatile string? _pendingStatus;

    // Deferred GPU readback - the map only runs after the copy query signaled,
    // so the render thread never waits for the GPU
    private Texture2D? _readbackTexture;
    private Query? _readbackQuery;
    private bool _readbackPending;
    private bool _readbackIsBgra;
    private int _readbackWaitFrames;
    private bool _loggedUnsupportedFormat;

    // Owned by the render thread - outputs only regenerate when these change
    private DepthEstimationResult? _appliedResult;
    private DepthOutputFormat _appliedFormat;
    private bool _appliedEnhance;
    private bool _appliedInvert;
    private byte[]? _normalizedBuffer;

    private readonly ConcurrentDictionary<(int width, int height, Format format), Texture2D> _cachedStagingTextures = new();
    private readonly object _textureCacheLock = new object();

    private SizeClassBufferPool? _bufferPool;

    // Stored values for UI dropdowns (updated each frame, read by UI thread)
    private int _uiOutputFormat;
    private int _uiModelSize;
    private int _uiInputResolution;
    private int _uiExecutionProvider;

    private sealed class SizeClassBufferPool
{
    private readonly int[] _sizeClasses = { 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
    private readonly ConcurrentDictionary<int, ConcurrentBag<float[]>> _floatPools = new();
    private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _bytePools = new();
    private readonly object _lock = new();
    private const int MaxPoolPerClass = 8;

    private int GetSizeClass(int size)
    {
        foreach (var cls in _sizeClasses)
        {
            if (size <= cls) return cls;
        }
        return size;
    }

    public float[] RentFloat(int size)
    {
        int cls = GetSizeClass(size);
        if (_floatPools.TryGetValue(cls, out var bag) && bag.TryTake(out var arr))
            return arr;
        return new float[size];
    }

    public void ReturnFloat(float[] arr)
    {
        if (arr == null) return;
        int cls = GetSizeClass(arr.Length);
        if (!_floatPools.TryGetValue(cls, out var bag))
        {
            lock (_lock)
            {
                if (!_floatPools.TryGetValue(cls, out bag))
                {
                    bag = new ConcurrentBag<float[]>();
                    _floatPools[cls] = bag;
                }
            }
        }
        if (bag.Count < MaxPoolPerClass)
            bag.Add(arr);
    }

    public byte[] RentByte(int size)
    {
        int cls = GetSizeClass(size);
        if (_bytePools.TryGetValue(cls, out var bag) && bag.TryTake(out var arr))
            return arr;
        return new byte[size];
    }

    public void ReturnByte(byte[] arr)
    {
        if (arr == null) return;
        int cls = GetSizeClass(arr.Length);
        if (!_bytePools.TryGetValue(cls, out var bag))
        {
            lock (_lock)
            {
                if (!_bytePools.TryGetValue(cls, out bag))
                {
                    bag = new ConcurrentBag<byte[]>();
                    _bytePools[cls] = bag;
                }
            }
        }
        if (bag.Count < MaxPoolPerClass)
            bag.Add(arr);
    }
}

    private void InitializeWorker(bool debug, ModelSize modelSize, ExecutionProvider provider)
    {
        StopWorker(debug);
        _activeModelSize = modelSize;
        _activeProvider = provider;
        _initFailed = false;
        _requestInFlight = false;
        _hasPendingInput = true;

        string? modelPath = ResolveModelPath(modelSize, debug);
        if (modelPath == null)
        {
            _initFailed = true;
            return;
        }

        _isInitializing = true;
        Status.Value = "Loading model...";

        var generation = Interlocked.Increment(ref _workerGeneration);
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _bufferPool = new SizeClassBufferPool();
        WarmUpStagingTextures();
        _processingTask = Task.Run(() => WorkerLoop(modelPath, modelSize, provider, token, generation, debug), token);
    }

    private void PublishStatus(string status)
    {
        _pendingStatus = status;
    }

    private void WarmUpStagingTextures()
    {
        // Pre-create staging textures for common input sizes
        var commonSizes = new[] { (1920, 1080), (1280, 720), (512, 512), (256, 256) };
        foreach (var (w, h) in commonSizes)
        {
            GetOrCreateStagingTexture(w, h, Format.R8G8B8A8_UNorm);
        }
    }

    private string? ResolveModelPath(ModelSize modelSize, bool debug)
    {
        string modelName = modelSize switch
        {
            ModelSize.Small => "depth-anything-v2-small-fp16.onnx",
            ModelSize.SmallFp32 => "depth-anything-v2-small-fp32.onnx",
            ModelSize.SmallInt8 => "depth-anything-v2-small-int8.onnx",
            ModelSize.Base => "depth-anything-v2-base-fp16.onnx",
            ModelSize.Large => "depth-anything-v2-large-fp16.onnx",
            _ => "depth-anything-v2-small-fp16.onnx"
        };

        // Try AssetRegistry first
        try
        {
            if (AssetRegistry.TryResolveAddress($"DepthAnything:{modelName}", this, out var resolvedPath, out _, logWarnings: false))
            {
                return resolvedPath;
            }
        }
        catch { }

        var asmDir = System.IO.Path.GetDirectoryName(typeof(DepthAnything).Assembly.Location) ?? "";
        var possiblePaths = new[]
        {
            System.IO.Path.Combine(asmDir, "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "..", "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, modelName),
            $"Assets/{modelName}",
            $"./Assets/{modelName}",
            $"../Assets/{modelName}"
        };

        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                if (debug) Log.Debug($"[DepthAnything] Found model at: {fullPath}", this);
                return fullPath;
            }
        }

        var status = $"Model not found: {modelName}";
        if (debug) Log.Error($"[DepthAnything] {status}. Place the model in Assets/ folder.", this);
        Status.Value = status;
        return null;
    }

    private static string? FirstMetadataKey(IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        foreach (var key in metadata.Keys)
        {
            return key;
        }

        return null;
    }

    private static int GetEffectiveInputSize(int declaredSize, int requestedSize, ModelSize modelSize)
    {
        // Fixed-size models only accept their declared resolution
        if (declaredSize > 0)
        {
            return declaredSize;
        }

        if (requestedSize > 0)
        {
            return requestedSize;
        }

        return modelSize is ModelSize.Small or ModelSize.SmallFp32 or ModelSize.SmallInt8 ? 392 : DefaultInputSize;
    }

    private void WorkerLoop(string modelPath, ModelSize modelSize, ExecutionProvider provider, CancellationToken token, int generation, bool debug)
    {
        if (debug) Log.Debug("[DepthAnything] Worker loop started", this);

        var workerContext = new WorkerContext();
        var failedToInit = false;
        try
        {
            var session = InitializeSession(workerContext, modelPath, provider, token, debug);
            if (session == null)
            {
                failedToInit = true;
            }
            else
            {
                WarmUpSession(workerContext, modelSize, token, debug);
                lock (_onnxLock)
                {
                    // Published only after warm-up so request acceptance coincides with a usable session
                    _onnxSession = session;
                }

                PublishStatus($"Ready ({(_usingDirectMl ? "DirectML" : "CPU")})");
                Log.Info($"[DepthAnything] Ready ({(_usingDirectMl ? "DirectML" : "CPU")}) - " +
                                $"input '{workerContext.InputName}' {session.InputMetadata[workerContext.InputName]}, " +
                                $"output '{workerContext.OutputName}' {session.OutputMetadata[workerContext.OutputName]}", this);
            }

            while (workerContext.Session != null && !token.IsCancellationRequested)
            {
                if (_inputQueue.TryDequeue(out var request))
                {
                    if (request != null && request.PixelData != null && workerContext.Session != null)
                    {
                        var result = ProcessFrame(workerContext, request, modelSize, debug);
                        if (token.IsCancellationRequested)
                        {
                            // A cancelled generation must not feed results into its successor
                            ReturnResultBuffers(result);
                        }
                        else
                        {
                            _outputQueue.Enqueue(result);
                        }
                    }

                    ReturnBufferToPool(request?.PixelData);
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
                if (debug) Log.Error($"[DepthAnything] Worker loop error: {ex.Message}", this);
                _outputQueue.Enqueue(new DepthEstimationResult { Error = ex.Message });
            }

            _requestInFlight = false;
        }
        finally
        {
            // Owns its session - dispose unconditionally; a newer worker may have replaced the field
            workerContext.Session?.Dispose();
            lock (_onnxLock)
            {
                if (ReferenceEquals(_onnxSession, workerContext.Session))
                    _onnxSession = null;
            }

            // Stale generations must not clobber their successor's init state
            if (generation == Volatile.Read(ref _workerGeneration))
            {
                _isInitializing = false;
                if (failedToInit)
                    _initFailed = true;
            }

            if (debug) Log.Debug("[DepthAnything] Worker loop stopped", this);
        }
    }

    private InferenceSession? InitializeSession(WorkerContext workerContext, string modelPath, ExecutionProvider provider, CancellationToken token, bool debug)
    {
        InferenceSession session;
        try
        {
            var modelBytes = System.IO.File.ReadAllBytes(modelPath);
            if (token.IsCancellationRequested)
            {
                return null;
            }

            session = CreateSession(modelBytes, provider);
        }
        catch (Exception ex)
        {
            PublishStatus($"Initialization error: {ex.Message}");
            if (debug) Log.Error($"[DepthAnything] Initialization error: {ex.Message}", this);
            return null;
        }

        workerContext.Session = session;
        workerContext.InputName = FirstMetadataKey(session.InputMetadata) ?? "pixel_values";
        workerContext.OutputName = FirstMetadataKey(session.OutputMetadata) ?? "predicted_depth";

        workerContext.DeclaredSize = 0;
        var dimensions = session.InputMetadata[workerContext.InputName].Dimensions;
        if (dimensions is { Length: >= 3 } && dimensions[^1] > 0)
        {
            workerContext.DeclaredSize = dimensions[^1];
        }

        return session;
    }

    private void WarmUpSession(WorkerContext workerContext, ModelSize modelSize, CancellationToken token, bool debug)
    {
        var session = workerContext.Session;
        if (session == null || token.IsCancellationRequested)
            return;

        try
        {
            // First DML inference compiles kernels; paying that on a dummy frame keeps it off a real one
            int size = GetEffectiveInputSize(workerContext.DeclaredSize, _requestedInputSize, modelSize);
            var inputTensor = new DenseTensor<float>(new float[3 * size * size], new[] { 1, 3, size, size });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(workerContext.InputName, inputTensor) };

            var stopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(inputs, new[] { workerContext.OutputName });
            stopwatch.Stop();

            Log.Debug($"[DepthAnything] Warm-up inference ({size}x{size}) took {stopwatch.ElapsedMilliseconds}ms", this);
        }
        catch (Exception ex)
        {
            // Best-effort - a failure here resurfaces on the first real frame
            if (debug) Log.Debug($"[DepthAnything] Warm-up skipped: {ex.Message}", this);
        }
    }

    private static void LogGpuAdapters()
    {
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            foreach (var adapter in factory.Adapters)
            {
                Log.Info($"[DepthAnything] GPU adapter: {adapter.Description.Description}");
                adapter.Dispose();
            }
        }
        catch
        {
            // Adapter enumeration is diagnostic only
        }
    }

    private InferenceSession CreateSession(byte[] modelBytes, ExecutionProvider provider)
    {
        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        // Without this the CPU provider saturates all cores during inference
        // and starves the render thread
        int intraOpThreads = Math.Min(4, Math.Max(1, Environment.ProcessorCount));
sessionOptions.IntraOpNumThreads = intraOpThreads;

        _usingDirectMl = false;
        if (provider == ExecutionProvider.DirectMl)
        {
            LogGpuAdapters();

            try
            {
                sessionOptions.AppendExecutionProvider_DML(0);
                _usingDirectMl = true;
            }
            catch (Exception ex)
            {
                // The CPU execution provider is always available as fallback
                Log.Info($"[DepthAnything] DirectML unavailable, using CPU: {ex.Message}", this);
            }
        }

        try
        {
            return new InferenceSession(modelBytes, sessionOptions);
        }
        catch (Exception ex) when (_usingDirectMl)
        {
            Log.Info($"[DepthAnything] DirectML session failed, retrying on CPU: {ex.Message}", this);
            _usingDirectMl = false;
            var cpuOptions = new SessionOptions();
            cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            cpuOptions.IntraOpNumThreads = 2;
            return new InferenceSession(modelBytes, cpuOptions);
        }
    }

    private DepthEstimationResult ProcessFrame(WorkerContext workerContext, DepthEstimationRequest request, ModelSize modelSize, bool debug)
    {
        // Rented buffers handed to the returned result; the catch path returns anything not attached
        float[]? resultDepth = null;
        byte[]? colorData = null;
        try
        {
            if (workerContext.Session == null || request.PixelData == null)
            {
                return ErrorResult(request, "No session or pixel data");
            }

            int inputSize = GetEffectiveInputSize(workerContext.DeclaredSize, _requestedInputSize, modelSize);

            // Preprocess with ImageNet normalization
            var (preprocessedData, success) = PreprocessImage(workerContext, request.PixelData, request.IsBgra, request.Width, request.Height, inputSize,
                                                              out int tensorWidth, out int tensorHeight);
            if (!success)
            {
                return ErrorResult(request, "Preprocessing failed");
            }

            // Run inference
            var depthData = RunInference(workerContext, preprocessedData, tensorWidth, tensorHeight, out int outputWidth, out int outputHeight, debug);
            if (depthData == null)
            {
                return ErrorResult(request, "Inference returned null");
            }

            // At model resolution the GPU sampler upscales when the texture is
            // used downstream, which skips all full-resolution CPU work per result
            int resultWidth;
            int resultHeight;
            if (_matchInputResolution)
            {
                resultDepth = ResizeDepthData(depthData, outputWidth, outputHeight, request.Width, request.Height);
                ReturnDepthBuffer(depthData);
                resultWidth = request.Width;
                resultHeight = request.Height;
            }
            else
            {
                resultDepth = depthData;
                resultWidth = outputWidth;
                resultHeight = outputHeight;
            }

            int count = resultWidth * resultHeight;
            ComputeMinMax(resultDepth, count, out float min, out float max);

            var outputFormat = _workerOutputFormat;
            var enhanceContrast = _workerEnhanceContrast;
            var invertDepth = _workerInvertDepth;
            colorData = GetBuffer(count * 4);
            ColorizeDepth(resultDepth, count, colorData, min, max, outputFormat, enhanceContrast, invertDepth);

            return new DepthEstimationResult
            {
                DepthData = resultDepth,
                ColorData = colorData,
                Width = resultWidth,
                Height = resultHeight,
                Min = min,
                Max = max,
                ColorFormat = outputFormat,
                ColorEnhance = enhanceContrast,
                ColorInvert = invertDepth
            };
        }
        catch (Exception ex)
        {
            if (debug) Log.Error($"[DepthAnything] ProcessFrame error: {ex.Message}", this);
            ReturnDepthBuffer(resultDepth);
            ReturnBufferToPool(colorData);
            return ErrorResult(request, ex.Message);
        }
    }

    private static DepthEstimationResult ErrorResult(DepthEstimationRequest request, string message)
    {
        return new DepthEstimationResult
        {
            Width = request.Width,
            Height = request.Height,
            Error = message
        };
    }

    // ImageNet normalization values for pretrained models
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    private static (float[] data, bool success) PreprocessImage(WorkerContext workerContext, byte[] rgbaData, bool isBgra,
                                                                int originalWidth, int originalHeight, int targetSize,
                                                                out int newWidth, out int newHeight)
    {
        newWidth = 0;
        newHeight = 0;
        try
        {
            // Calculate aspect-ratio-preserving dimensions (multiples of 14 for ViT)
            float aspectRatio = (float)originalWidth / originalHeight;

            if (aspectRatio > 1.0f)
            {
                // Wider than tall
                newWidth = targetSize;
                newHeight = (int)(targetSize / aspectRatio);
                newHeight = (newHeight / 14) * 14; // Round down to multiple of 14
                if (newHeight < 14) newHeight = 14;
            }
            else
            {
                // Taller than wide
                newHeight = targetSize;
                newWidth = (int)(targetSize * aspectRatio);
                newWidth = (newWidth / 14) * 14; // Round down to multiple of 14
                if (newWidth < 14) newWidth = 14;
            }

            int dataLength = newWidth * newHeight * 3;
            if (workerContext.PreprocessBuffer == null || workerContext.PreprocessBuffer.Length < dataLength)
            {
                workerContext.PreprocessBuffer = new float[dataLength];
            }

            var processedData = workerContext.PreprocessBuffer;
            int rOffset = isBgra ? 2 : 0;
            int bOffset = isBgra ? 0 : 2;
            int scaledWidth = newWidth;
            int scaledHeight = newHeight;
            float scaleX = (float)originalWidth / scaledWidth;
            float scaleY = (float)originalHeight / scaledHeight;
            int planeSize = scaledWidth * scaledHeight;

            Parallel.For(0, scaledHeight, y =>
            {
                for (int x = 0; x < scaledWidth; x++)
                {
                    int srcX = Math.Min((int)(x * scaleX), originalWidth - 1);
                    int srcY = Math.Min((int)(y * scaleY), originalHeight - 1);

                    int srcIdx = (srcY * originalWidth + srcX) * 4;
                    int dstIdx = y * scaledWidth + x;

                    processedData[dstIdx] = (rgbaData[srcIdx + rOffset] / 255.0f - ImageNetMean[0]) / ImageNetStd[0];
                    processedData[planeSize + dstIdx] = (rgbaData[srcIdx + 1] / 255.0f - ImageNetMean[1]) / ImageNetStd[1];
                    processedData[2 * planeSize + dstIdx] = (rgbaData[srcIdx + bOffset] / 255.0f - ImageNetMean[2]) / ImageNetStd[2];
                }
            });

            return (processedData, true);
        }
        catch (Exception ex)
        {
            Log.Debug($"[DepthAnything] PreprocessImage error: {ex.Message}");
            return (Array.Empty<float>(), false);
        }
    }

    private float[]? RunInference(WorkerContext workerContext, float[] preprocessedData, int tensorWidth, int tensorHeight,
                                  out int outputWidth, out int outputHeight, bool debug)
    {
        outputWidth = 0;
        outputHeight = 0;

        var session = workerContext.Session;
        if (session == null)
        {
            return null;
        }

        try
        {
            // Use actual processed dimensions (aspect-ratio preserving)
            var inputTensor = new DenseTensor<float>(preprocessedData, new[] { 1, 3, tensorHeight, tensorWidth });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(workerContext.InputName, inputTensor) };

            var stopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(inputs, new[] { workerContext.OutputName });
            stopwatch.Stop();

            DenseTensor<float>? denseOutput = null;
            foreach (var output in outputs)
            {
                denseOutput = output.AsTensor<float>() as DenseTensor<float>;
                break;
            }

            if (denseOutput == null || denseOutput.Length == 0)
            {
                if (debug) Log.Error("[DepthAnything] Inference did not produce a float tensor", this);
                return null;
            }

            // Copy out before the result collection is disposed. Pooled arrays may exceed w*h
            // (size-class rounding) - downstream code must use explicit pixel counts
            var depthData = TakeDepthBuffer((int)denseOutput.Length);
            denseOutput.Buffer.Span.CopyTo(depthData);

            var dimensions = denseOutput.Dimensions;
            outputHeight = dimensions[dimensions.Length - 2];
            outputWidth = dimensions[dimensions.Length - 1];

            if (!workerContext.LoggedFirstInference)
            {
                workerContext.LoggedFirstInference = true;
                Log.Debug($"[DepthAnything] First inference ok: {outputWidth}x{outputHeight}, {stopwatch.ElapsedMilliseconds}ms", this);
            }

            return depthData;
        }
        catch (Exception ex)
        {
            if (debug) Log.Error($"[DepthAnything] RunInference error: {ex.Message}", this);
            return null;
        }
    }

    private float[] ResizeDepthData(float[] sourceDepth, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var resized = TakeDepthBuffer(targetWidth * targetHeight);

        float scaleX = (float)sourceWidth / targetWidth;
        float scaleY = (float)sourceHeight / targetHeight;

        Parallel.For(0, targetHeight, y =>
        {
            int srcY = Math.Min((int)(y * scaleY), sourceHeight - 1);
            int rowOffset = srcY * sourceWidth;
            int dstRow = y * targetWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = Math.Min((int)(x * scaleX), sourceWidth - 1);
                resized[dstRow + x] = sourceDepth[rowOffset + srcX];
            }
        });

        return resized;
    }

    private static void ComputeMinMax(float[] depthData, int count, out float minDepth, out float maxDepth)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float depth = depthData[i];
            if (!float.IsNaN(depth) && !float.IsInfinity(depth))
            {
                if (depth < min)
                    min = depth;
                if (depth > max)
                    max = depth;
            }
        }

        if (min == float.MaxValue)
        {
            min = 0.0f;
            max = 1.0f;
        }

        minDepth = min;
        maxDepth = max;
    }

    private void StopWorker(bool debug)
    {
        lock (_workerLock)
        {
            _cancellationTokenSource?.Cancel();
            try
            {
                // Never wait for a running inference on the render thread - the worker
                // disposes its own session when it notices the cancellation
                _processingTask?.Wait(0);
            }
            catch (Exception ex)
            {
                if (debug) Log.Error($"[DepthAnything] Error waiting for worker task: {ex.Message}", this);
            }

            _workerWake.Set();
            _readbackPending = false;

            while (_inputQueue.TryDequeue(out var req))
            {
                ReturnBufferToPool(req?.PixelData);
            }

            while (_outputQueue.TryDequeue(out var stale))
            {
                ReturnResultBuffers(stale);
            }

            lock (_onnxLock)
            {
                // Pauses request submission during re-init; the exiting worker disposes its own session
                _onnxSession = null;
            }

            _requestInFlight = false;
        }
    }

    private void PauseProcessing()
    {
        // Disabling must not tear down the worker - re-enabling would reload the model
        if (_processingTask == null)
            return;

        _hasPendingInput = false;
        _readbackPending = false;

        while (_inputQueue.TryDequeue(out var request))
        {
            ReturnBufferToPool(request?.PixelData);
        }

        // Also drops results an in-flight request produced after it was paused
        while (_outputQueue.TryDequeue(out var stale))
        {
            ReturnResultBuffers(stale);
        }
    }

    #endregion

    #region Memory Management

    private Texture2D GetOrCreateStagingTexture(int width, int height, Format format)
    {
        var key = (width, height, format);

        if (_cachedStagingTextures.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }

        lock (_textureCacheLock)
        {
            if (_cachedStagingTextures.TryGetValue(key, out cachedTexture))
            {
                return cachedTexture;
            }

            var newTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(ResourceManager.Device, new Texture2DDescription
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

            _cachedStagingTextures[key] = newTexture;
            return newTexture;
        }
    }

    private byte[] GetBuffer(int size)
    {
        return _bufferPool?.RentByte(size) ?? new byte[size];
    }

    private void ReturnBufferToPool(byte[]? b)
    {
        if (b != null)
            _bufferPool?.ReturnByte(b);
    }

    private float[] TakeDepthBuffer(int length)
    {
        return _bufferPool?.RentFloat(length) ?? new float[length];
    }

    private void ReturnDepthBuffer(float[]? buffer)
    {
        if (buffer != null)
            _bufferPool?.ReturnFloat(buffer);
    }

    private void BeginReadback(Texture2D texture)
    {
        var desc = texture.Description;

        switch (desc.Format)
        {
            case Format.R8G8B8A8_UNorm:
            case Format.R8G8B8A8_UNorm_SRgb:
                _readbackIsBgra = false;
                break;

            case Format.B8G8R8A8_UNorm:
            case Format.B8G8R8A8_UNorm_SRgb:
                _readbackIsBgra = true;
                break;

            default:
                // CopyResource is a raw bit copy, so only 4-byte channel orders can be read here
                if (!_loggedUnsupportedFormat)
                {
                    _loggedUnsupportedFormat = true;
                    Log.Warning($"[DepthAnything] Unsupported input format {desc.Format} - skipping frames", this);
                }
                _hasPendingInput = false;
                return;
        }

        var device = ResourceManager.Device;

        _readbackTexture = GetOrCreateStagingTexture(desc.Width, desc.Height, desc.Format);
        device.ImmediateContext.CopyResource(texture, _readbackTexture);

        _readbackQuery ??= new Query(device, new QueryDescription { Type = QueryType.Event });
        device.ImmediateContext.End(_readbackQuery);
        _readbackPending = true;
        _readbackWaitFrames = 0;
    }

    private DepthEstimationRequest? CompleteReadback()
    {
        var context = ResourceManager.Device.ImmediateContext;

        // GetData only reports success once the GPU reached the End call,
        // so the map below never stalls the render thread. DoNotFlush keeps the
        // poll from flushing the GPU command queue every frame
        if (!context.GetData(_readbackQuery, AsynchronousFlags.DoNotFlush, out int done) || done == 0)
        {
            _readbackWaitFrames++;
            if (_readbackWaitFrames > ReadbackTimeoutFrames)
            {
                // A device reset can leave the event query unsignalled forever
                Log.Warning("[DepthAnything] Readback query never signaled - restarting readback", this);
                _readbackPending = false;
                _hasPendingInput = true;
            }

            return null;
        }

        _readbackPending = false;

        var stagingTexture = _readbackTexture;
        if (stagingTexture == null)
        {
            return null;
        }

        var desc = stagingTexture.Description;
        int width = desc.Width;
        int height = desc.Height;

        var dataBox = context.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);

        if (dataBox.DataPointer == IntPtr.Zero)
        {
            context.UnmapSubresource(stagingTexture, 0);
            return null;
        }

        try
        {
            byte[] buffer = GetBuffer(width * height * 4);
            unsafe
            {
                IntPtr srcPtr = dataBox.DataPointer;
                int rowPitch = dataBox.RowPitch;
                int rowBytes = width * 4;

                // Raw copy - the channel layout is resolved during preprocessing on the worker
                fixed (byte* dst = buffer)
                {
                    for (int y = 0; y < height; y++)
                    {
                        System.Buffer.MemoryCopy((byte*)srcPtr + (long)y * rowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }

            return new DepthEstimationRequest
            {
                PixelData = buffer,
                Width = width,
                Height = height,
                IsBgra = _readbackIsBgra
            };
        }
        finally
        {
            context.UnmapSubresource(stagingTexture, 0);
        }
    }

    #endregion

    #region Output Updates

    private void ApplyResult(DepthEstimationResult result, DepthOutputFormat outputFormat, bool enhanceContrast, bool invertDepth)
    {
        var depthData = result.DepthData;
        int width = result.Width;
        int height = result.Height;

        // The pool guarantees >= w*h - a shortfall is a logic bug, not data to patch up
        if (depthData == null || depthData.Length < width * height)
        {
            Log.Warning($"[DepthAnything] Discarding malformed depth result ({depthData?.Length ?? 0} floats < {width * height})", this);
            ClearOutputs();
            return;
        }

        MinDepth.Value = result.Min;
        MaxDepth.Value = result.Max;

        // Update depth texture (R32_Float)
        UploadDepthTexture(depthData, width, height);

        // Update normalized depth texture (R8G8B8A8_UNorm)
        var colorData = result.ColorData;
        if (colorData != null && colorData.Length >= width * height * 4
            && result.ColorFormat == outputFormat && result.ColorEnhance == enhanceContrast && result.ColorInvert == invertDepth)
        {
            UploadRgbaTexture(colorData, width, height);
        }
        else
        {
            // The worker colorized with different settings - colorize on the spot
            int count = width * height;
            if (_normalizedBuffer == null || _normalizedBuffer.Length < count * 4)
            {
                _normalizedBuffer = new byte[count * 4];
            }

            ColorizeDepth(depthData, count, _normalizedBuffer, result.Min, result.Max, outputFormat, enhanceContrast, invertDepth);
            UploadRgbaTexture(_normalizedBuffer, width, height);
        }
    }

    private void UploadDepthTexture(float[] depthData, int width, int height)
    {
        if (_depthTexture == null || _depthTexture.Description.Width != width || _depthTexture.Description.Height != height)
        {
            _depthTexture?.Dispose();
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.None
            };
            _depthTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(ResourceManager.Device, desc));
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(depthData, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var dataBox = new DataBox(handle.AddrOfPinnedObject(), width * 4, 0);
            ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _depthTexture);
        }
        finally
        {
            handle.Free();
        }

        DepthTexture.Value = _depthTexture;
    }

    private void UploadRgbaTexture(byte[] rgbaData, int width, int height)
    {
        if (_normalizedDepthTexture == null || _normalizedDepthTexture.Description.Width != width || _normalizedDepthTexture.Description.Height != height)
        {
            _normalizedDepthTexture?.Dispose();
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
            _normalizedDepthTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(ResourceManager.Device, desc));
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgbaData, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var dataBox = new DataBox(handle.AddrOfPinnedObject(), width * 4, 0);
            ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _normalizedDepthTexture);
        }
        finally
        {
            handle.Free();
        }

        NormalizedDepthTexture.Value = _normalizedDepthTexture;
    }

    private static void ColorizeDepth(float[] depthData, int pixelCount, byte[] rgbaData, float minDepth, float maxDepth,
                                      DepthOutputFormat outputFormat, bool enhanceContrast, bool invertDepth)
    {
        float range = maxDepth - minDepth;
        if (range < 0.0001f) range = 1.0f;

        for (int i = 0; i < pixelCount; i++)
        {
            float depth = depthData[i];
            if (float.IsNaN(depth) || float.IsInfinity(depth))
                depth = minDepth;

            float normalizedDepth = (depth - minDepth) / range;

            // Invert if requested
            if (invertDepth)
                normalizedDepth = 1.0f - normalizedDepth;

            // Enhance contrast
            if (enhanceContrast)
            {
                normalizedDepth = (normalizedDepth - 0.5f) * 2.0f;
                normalizedDepth = Math.Clamp(normalizedDepth, 0.0f, 1.0f);
            }

            byte depthByte = (byte)(normalizedDepth * 255);

            int rgbaIdx = i * 4;

            // Apply color format
            switch (outputFormat)
            {
                case DepthOutputFormat.Grayscale:
                    rgbaData[rgbaIdx + 0] = depthByte;
                    rgbaData[rgbaIdx + 1] = depthByte;
                    rgbaData[rgbaIdx + 2] = depthByte;
                    rgbaData[rgbaIdx + 3] = 255;
                    break;

                case DepthOutputFormat.Color:
                    // Mid-range pixels glow green, extremes drift toward red/blue.
                    // The green term goes negative near 0, so clamp before the cast.
                    int green = 255 - Math.Abs(depthByte - 128) * 2;
                    rgbaData[rgbaIdx + 0] = depthByte;
                    rgbaData[rgbaIdx + 1] = (byte)Math.Clamp(green, 0, 255);
                    rgbaData[rgbaIdx + 2] = (byte)(255 - depthByte);
                    rgbaData[rgbaIdx + 3] = 255;
                    break;

                case DepthOutputFormat.Rainbow:
                    float hue = normalizedDepth * 360;
                    var rgb = HsvToRgb(hue / 360, 1.0f, 1.0f);
                    rgbaData[rgbaIdx + 0] = (byte)(rgb.X * 255);
                    rgbaData[rgbaIdx + 1] = (byte)(rgb.Y * 255);
                    rgbaData[rgbaIdx + 2] = (byte)(rgb.Z * 255);
                    rgbaData[rgbaIdx + 3] = 255;
                    break;

                default:
                    rgbaData[rgbaIdx + 0] = depthByte;
                    rgbaData[rgbaIdx + 1] = depthByte;
                    rgbaData[rgbaIdx + 2] = depthByte;
                    rgbaData[rgbaIdx + 3] = 255;
                    break;
            }
        }
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        if (s == 0)
            return new Vector3(v, v, v);

        float i = (int)(h * 6);
        float f = h * 6 - i;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);

        return i switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q)
        };
    }

    private Texture2D? _depthTexture;
    private Texture2D? _normalizedDepthTexture;

    #endregion

    #region Input Parameters

    [Input(Guid = "12345678-1234-1234-1234-123456789012")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "23456789-2345-2345-2345-234567890123")]
    public readonly InputSlot<bool> Enabled = new(true);

    [Input(Guid = "34567890-3456-3456-3456-345678901234", MappedType = typeof(ModelSize))]
    public readonly InputSlot<int> ModelSizeParam = new();

    [Input(Guid = "90123456-9012-9012-9012-901234567890", MappedType = typeof(Resolution))]
    public readonly InputSlot<int> InputResolution = new((int)Resolution.Auto);

    [Input(Guid = "012345ab-cdef-0123-4567-89abcdef0123", MappedType = typeof(ExecutionProvider))]
    public readonly InputSlot<int> ExecutionProviderParam = new((int)ExecutionProvider.DirectMl);

    [Input(Guid = "234567cd-8912-9012-3456-6789012345ab")]
    public readonly InputSlot<bool> MatchInputResolution = new(false);

    [Input(Guid = "45678901-4567-4567-4567-456789012345", MappedType = typeof(DepthOutputFormat))]
    public readonly InputSlot<int> OutputFormat = new();

    [Input(Guid = "56789012-5678-5678-5678-567890123456")]
    public readonly InputSlot<bool> EnhanceContrast = new(false);

    [Input(Guid = "67890123-6789-6789-6789-678901234567")]
    public readonly InputSlot<bool> InvertDepth = new(false);

    [Input(Guid = "78901234-7890-7890-7890-789012345678")]
    public readonly InputSlot<bool> Debug = new(false);

    #endregion

    #region Enums

    public enum ModelSize
    {
        Small,
        Base,
        Large,
        SmallFp32,
        SmallInt8
    }

    /// <summary>
    /// Inference input resolution. Auto uses the model's native size; smaller
    /// sizes trade depth detail for speed. Values are multiples of the ViT patch size.
    /// </summary>
    public enum Resolution
    {
        Auto,
        R196,
        R280,
        R392,
        R518
    }

    public enum ExecutionProvider
    {
        Cpu,
        DirectMl
    }

    public enum DepthOutputFormat
    {
        Grayscale,
        Color,
        Rainbow
    }

    #endregion

    #region ICustomDropdownHolder

    private static readonly string[] OutputFormatNames = { "Grayscale", "Color", "Rainbow" };
    private static readonly string[] ModelSizeNames = { "Small", "Base", "Large", "Small Fp32", "Small Int8" };
    private static readonly string[] ResolutionNames = { "Auto", "196", "280", "392", "518" };
    private static readonly string[] ExecutionProviderNames = { "CPU", "DirectML" };

    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == OutputFormat.Id)
        {
            var val = _uiOutputFormat;
            return val >= 0 && val < OutputFormatNames.Length ? OutputFormatNames[val] : "Grayscale";
        }

        if (inputId == ModelSizeParam.Id)
        {
            var val = _uiModelSize;
            return val >= 0 && val < ModelSizeNames.Length ? ModelSizeNames[val] : "Small";
        }

        if (inputId == InputResolution.Id)
        {
            var val = _uiInputResolution;
            return val >= 0 && val < ResolutionNames.Length ? ResolutionNames[val] : "Auto";
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var val = _uiExecutionProvider;
            return val >= 0 && val < ExecutionProviderNames.Length ? ExecutionProviderNames[val] : "CPU";
        }

        return "";
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == OutputFormat.Id)
        {
            foreach (var name in OutputFormatNames)
                yield return name;
            yield break;
        }

        if (inputId == ModelSizeParam.Id)
        {
            foreach (var name in ModelSizeNames)
                yield return name;
            yield break;
        }

        if (inputId == InputResolution.Id)
        {
            foreach (var name in ResolutionNames)
                yield return name;
            yield break;
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            foreach (var name in ExecutionProviderNames)
                yield return name;
            yield break;
        }

        yield return "undefined";
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected))
            return;

        if (inputId == OutputFormat.Id)
        {
            var index = Array.IndexOf(OutputFormatNames, selected);
            if (index >= 0)
                OutputFormat.SetTypedInputValue(index);
            return;
        }

        if (inputId == ModelSizeParam.Id)
        {
            var index = Array.IndexOf(ModelSizeNames, selected);
            if (index >= 0)
                ModelSizeParam.SetTypedInputValue(index);
            return;
        }

        if (inputId == InputResolution.Id)
        {
            var index = Array.IndexOf(ResolutionNames, selected);
            if (index >= 0)
                InputResolution.SetTypedInputValue(index);
            return;
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var index = Array.IndexOf(ExecutionProviderNames, selected);
            if (index >= 0)
                ExecutionProviderParam.SetTypedInputValue(index);
            return;
        }
    }

    #endregion

    #region Cleanup

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;

        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch { }

        _workerWake.Set();
        // The session is disposed by the worker's own exit path - disposing it here
        // could race an in-flight Run on the worker thread

        _depthTexture?.Dispose();
        _depthTexture = null;

        _normalizedDepthTexture?.Dispose();
        _normalizedDepthTexture = null;

        _readbackQuery?.Dispose();
        _readbackQuery = null;

        lock (_textureCacheLock)
        {
            foreach (var cachedTexture in _cachedStagingTextures.Values)
            {
                cachedTexture?.Dispose();
            }
            _cachedStagingTextures.Clear();
        }

        base.Dispose(isDisposing);
    }

    #endregion
}
