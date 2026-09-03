using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using T3.Core.Logging;

namespace t3.streamdiffusion.Onnx;

public enum ExecutionProvider
{
    Cpu,
    DirectMl,
    Cuda,
    TensorRt
}

/// <summary>
/// Wraps an InferenceSession with GPU execution (CUDA/TensorRT, DirectML fallthrough,
/// CPU fallback) and input/output name discovery tolerant of differing export conventions.
/// </summary>
public sealed class OnnxModelSession : IDisposable
{
    public InferenceSession Session { get; }
    public string Name { get; }
    public string ProviderLabel { get; }

    // ORT's RunWithBinding(RunOptions, OrtIoBinding) dereferences runOptions.Handle without a
    // null check — passing null throws NullReferenceException from managed interop (v1.20.1).
    private readonly RunOptions _runOptions = new();

    private static readonly object PreloadLock = new();
    private static bool _preloadAttempted;
    private static readonly List<IntPtr> PinnedCudaModules = new();

    // The CUDA/cuDNN runtime DLLs the provider DLLs statically import. Loading them
    // explicitly (absolute path) before the provider resolves: the Windows loader
    // then satisfies the provider's imports from these already-loaded modules.
    private static readonly string[] CudaRuntimeDlls =
    {
        "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "curand64_10.dll",
        "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_graph64_9.dll",
        "cudnn_heuristic64_9.dll", "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll", "cudnn_ext64_9.dll", "cudnn64_9.dll",
        "nvinfer.dll", "nvonnxparser.dll"
    };

    private OnnxModelSession(InferenceSession session, string name, string providerLabel)
    {
        Session = session;
        Name = name;
        ProviderLabel = providerLabel;
    }

    public static OnnxModelSession? Create(string modelPath, int deviceId, string name,
        ExecutionProvider provider = ExecutionProvider.Cuda)
    {
        if (!File.Exists(modelPath))
        {
            Log.Error($"[StreamDiffusion] {name}: model file not found: {modelPath}");
            return null;
        }

        // The GPU ORT build has no DirectML, and CUDA/TensorRT need NVIDIA runtime
        // DLLs - walk the chain and keep the first provider that initializes; CPU
        // always works and is always last
        var chain = provider switch
        {
            ExecutionProvider.Cpu => new[] { ExecutionProvider.Cpu },
            ExecutionProvider.DirectMl => new[] { ExecutionProvider.DirectMl, ExecutionProvider.Cuda, ExecutionProvider.Cpu },
            ExecutionProvider.Cuda => new[] { ExecutionProvider.Cuda, ExecutionProvider.Cpu },
            ExecutionProvider.TensorRt => new[] { ExecutionProvider.TensorRt, ExecutionProvider.Cuda, ExecutionProvider.Cpu },
            _ => new[] { ExecutionProvider.Cpu }
        };

        PreloadCudaRuntimes(modelPath);

        foreach (var candidate in chain)
        {
            SessionOptions? options = null;
            try
            {
                options = CreateSessionOptions(candidate, deviceId, modelPath);
                var session = new InferenceSession(modelPath, options);
                Log.Info($"[StreamDiffusion] {name}: ready on {EpLabel(candidate)}");
                return new OnnxModelSession(session, name, EpLabel(candidate));
            }
            catch (Exception ex) when (candidate != ExecutionProvider.Cpu)
            {
                Log.Info($"[StreamDiffusion] {name}: {EpLabel(candidate)} unavailable ({FirstLine(ex.Message)}), trying next provider");
                options?.Dispose();
            }
            catch
            {
                options?.Dispose();
                throw;
            }
        }

        Log.Error($"[StreamDiffusion] {name}: failed to create session");
        return null;
    }

    /// <summary>
    /// GPU sessions keep a small CPU pool (only pre/post-processing nodes run there),
    /// while CPU-fallback sessions get the full core count — the previous fixed 2 threads
    /// made a CPU-only UNet 4-8x slower than necessary.
    /// </summary>
    private static SessionOptions CreateSessionOptions(ExecutionProvider provider, int deviceId, string modelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = provider == ExecutionProvider.Cpu ? Math.Max(4, Environment.ProcessorCount) : 2,
        };

        switch (provider)
        {
            case ExecutionProvider.DirectMl:
                options.AppendExecutionProvider_DML(deviceId);
                break;

            case ExecutionProvider.Cuda:
                options.AppendExecutionProvider_CUDA(deviceId);
                break;

            case ExecutionProvider.TensorRt:
                // Engine building is slow (minutes per input shape) - persist engines
                // next to the models so only the first use of a resolution stalls
                var trtCache = Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "trt_cache");
                Directory.CreateDirectory(trtCache);
                using (var trtOptions = new OrtTensorRTProviderOptions())
                {
                    trtOptions.UpdateOptions(new Dictionary<string, string>
                    {
                        ["device_id"] = deviceId.ToString(),
                        ["trt_max_workspace_size"] = "2147483648",
                        ["trt_engine_cache_enable"] = "1",
                        ["trt_engine_cache_path"] = trtCache,
                        ["trt_timing_cache_enable"] = "1",
                        ["trt_timing_cache_path"] = trtCache
                    });
                    options.AppendExecutionProvider_Tensorrt(trtOptions);
                }
                break;
        }

        return options;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    private static void PreloadCudaRuntimes(string modelPath)
    {
        lock (PreloadLock)
        {
            if (_preloadAttempted)
                return; // already loaded (or attempted) - also covers concurrent sessions initializing

            _preloadAttempted = true;

            var candidateDirs = new List<string>();

            void AddDir(string? d)
            {
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && !candidateDirs.Contains(d))
                    candidateDirs.Add(d);
            }

            AddDir(Path.GetDirectoryName(Environment.ProcessPath));
            AddDir(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName));
            AddDir(AppContext.BaseDirectory);
            AddDir(Path.Combine(AppContext.BaseDirectory, "CudaRuntime"));
            AddDir(Path.GetDirectoryName(modelPath));
            if (!string.IsNullOrEmpty(modelPath))
                AddDir(Path.Combine(Path.GetDirectoryName(modelPath)!, "CudaRuntime"));

            var asmLocation = typeof(OnnxModelSession).Assembly.Location;
            if (!string.IsNullOrEmpty(asmLocation))
            {
                var asmDir = Path.GetDirectoryName(asmLocation);
                AddDir(asmDir);
                if (!string.IsNullOrEmpty(asmDir))
                    AddDir(Path.Combine(asmDir, "CudaRuntime"));
            }

            // Walk up directory tree to search for repo root CudaRuntime or Editor bin folders
            var dirInfo = new DirectoryInfo(AppContext.BaseDirectory);
            while (dirInfo != null)
            {
                var dirPath = dirInfo.FullName;
                AddDir(Path.Combine(dirPath, "Operators", "DepthAnything", "CudaRuntime"));
                AddDir(Path.Combine(dirPath, "Operators", "StreamDiffusion", "CudaRuntime"));
                AddDir(Path.Combine(dirPath, "Editor", "bin", "Debug", "net10.0-windows"));
                AddDir(Path.Combine(dirPath, "Editor", "bin", "Release", "net10.0-windows"));
                AddDir(Path.Combine(dirPath, "CudaRuntime"));
                dirInfo = dirInfo.Parent;
            }

            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
                AddDir(Path.Combine(cudaPath, "bin"));

            string? primaryCudaDir = null;
            foreach (var dir in candidateDirs)
            {
                if (File.Exists(Path.Combine(dir, "cublas64_12.dll")))
                {
                    primaryCudaDir = dir;
                    break;
                }
            }

            if (primaryCudaDir != null)
            {
                try
                {
                    SetDllDirectory(primaryCudaDir);
                }
                catch { }
            }

            foreach (var dll in CudaRuntimeDlls)
            {
                foreach (var dir in candidateDirs)
                {
                    var candidate = Path.Combine(dir, dll);
                    if (!File.Exists(candidate))
                        continue;

                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        PinnedCudaModules.Add(handle);
                    }
                    break;
                }
            }

            if (PinnedCudaModules.Count > 0)
                Log.Info($"[StreamDiffusion] Preloaded {PinnedCudaModules.Count} CUDA/cuDNN runtime DLLs from {primaryCudaDir ?? "system"}");
        }
    }

    private static string FirstLine(string message)
    {
        return message.Split('\n')[0].Trim();
    }

    private static string EpLabel(ExecutionProvider provider)
    {
        return provider switch
        {
            ExecutionProvider.DirectMl => "DirectML",
            ExecutionProvider.Cuda => "CUDA",
            ExecutionProvider.TensorRt => "TensorRT",
            _ => "CPU"
        };
    }

    public string PrimaryInputName => FirstKey(Session.InputMetadata);
    public string PrimaryOutputName => FirstKey(Session.OutputMetadata);

    public IReadOnlyDictionary<string, NodeMetadata> Inputs => Session.InputMetadata;
    public IReadOnlyDictionary<string, NodeMetadata> Outputs => Session.OutputMetadata;

    /// <summary>
    /// Finds a float input by tensor rank, optionally preferring a name fragment.
    /// Used to locate the UNet sample/timestep/embeddings across export variants.
    /// </summary>
    public string? FindInput(int rank, string? preferredNameFragment = null)
    {
        if (preferredNameFragment != null)
        {
            foreach (var (name, metadata) in Session.InputMetadata)
            {
                if (name.Contains(preferredNameFragment, StringComparison.OrdinalIgnoreCase)
                    && metadata.Dimensions is { Length: var inputRank } && inputRank == rank)
                {
                    return name;
                }
            }
        }

        foreach (var (name, metadata) in Session.InputMetadata)
        {
            if (metadata.Dimensions is { Length: var fallbackRank } && fallbackRank == rank)
            {
                return name;
            }
        }

        return null;
    }

    public DenseTensor<float> RunFloat(DenseTensor<float> input, string inputName, string outputName)
    {
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var outputs = Session.Run(inputs, new[] { outputName });

        foreach (var output in outputs)
        {
            if (output.AsTensor<float>() is DenseTensor<float> dense)
            {
                var copy = new DenseTensor<float>(dense.Dimensions);
                dense.Buffer.Span.CopyTo(copy.Buffer.Span);
                return copy;
            }
        }

        throw new InvalidOperationException($"{Name}: output '{outputName}' is not a float tensor");
    }

    /// <summary>Creates an IO binding for zero-copy/persistent-buffer inference.</summary>
    public OrtIoBinding CreateIoBinder()
    {
        return Session.CreateIoBinding();
    }

    /// <summary>Runs the session with a pre-bound IO binding (inputs/outputs must be bound).</summary>
    public void RunBound(OrtIoBinding binding)
    {
        Session.RunWithBinding(_runOptions, binding);
    }

    private static string FirstKey(IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        foreach (var key in metadata.Keys)
        {
            return key;
        }

        return string.Empty;
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        Session.Dispose();
    }
}
