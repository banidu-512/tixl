using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
#nullable enable

namespace T3.Core.Utils;

/// <summary>Linux only: checks that the CUDA runtime the ONNX Runtime GPU build links against can be loaded,
/// before the CUDA execution provider is appended. Without it the provider fails inside native code
/// ("libcublasLt.so.13: cannot open shared object file") and ONNX Runtime prints the error to stderr;
/// probing first lets the operator skip CUDA with one readable line and go on to the CPU provider.
/// <para>Libraries are looked up through ld.so first, then in the usual toolkit folders, a
/// <c>CudaRuntime</c> folder next to the operator, and <c>CUDA_PATH</c>/<c>CUDA_HOME</c>. Loaded by absolute path
/// they stay resident, and the provider's own imports are then satisfied by SONAME without
/// <c>LD_LIBRARY_PATH</c> having to be set.</para></summary>
public static class LinuxCudaRuntime
{
    /** What libonnxruntime_providers_cuda.so of the 1.30 GPU package imports, plus cuDNN. CUDA 13 dropped Pascal. */
    private static readonly string[] RequiredLibraries =
    {
        "libcudart.so.13", "libcublasLt.so.13", "libcublas.so.13", "libcudnn.so.9"
    };

    /** The TensorRT 10 runtime the provider links against. Optional helpers are skipped when absent. */
    private static readonly string[] TensorRtLibraries =
    {
        "libnvinfer.so.10", "libnvinfer_dispatch.so.10", "libnvinfer_lean.so.10",
        "libnvinfer_plugin.so.10", "libnvonnxparser.so.10"
    };

    private static readonly string[] TensorRtRequiredLibraries = { "libnvinfer.so.10", "libnvonnxparser.so.10" };

    private static readonly object Lock = new();
    private static readonly List<IntPtr> LoadedHandles = new();
    private static string? _failure;
    private static bool _probed;
    private static string? _tensorRtFailure;
    private static string? _tensorRtDirectory;
    private static bool _tensorRtProbed;

    public static bool TryPrepare(out string reason)
    {
        lock (Lock)
        {
            if (!_probed)
            {
                _probed = true;
                _failure = Probe();
            }

            reason = _failure ?? string.Empty;
            return _failure == null;
        }
    }

    /// <summary>Loads the TensorRT 10 runtime the ONNX Runtime provider links against, so the provider does not
    /// depend on LD_LIBRARY_PATH having been set before the process started.</summary>
    public static bool TryPrepareTensorRt(out string reason, out string directory)
    {
        lock (Lock)
        {
            if (!_tensorRtProbed)
            {
                _tensorRtProbed = true;
                _tensorRtFailure = ProbeTensorRt();
            }

            reason = _tensorRtFailure ?? string.Empty;
            directory = _tensorRtDirectory ?? string.Empty;
            return _tensorRtFailure == null;
        }
    }

    public static bool TryPrepareTensorRt(out string reason) => TryPrepareTensorRt(out reason, out _);

    private static string? Probe()
    {
        var searchDirs = CollectSearchDirs();
        var missing = new List<string>();

        foreach (var library in RequiredLibraries)
        {
            if (!TryLoad(library, searchDirs))
                missing.Add(library);
        }

        if (missing.Count == 0)
            return null;

        return $"{string.Join(", ", missing)} not found. This ONNX Runtime's GPU build needs the CUDA 13 runtime and cuDNN 9 " +
               "(NVIDIA driver 580 or newer; CUDA 13 no longer supports Pascal GPUs). Install them, or point CUDA_PATH at the toolkit";
    }

    private static string? ProbeTensorRt()
    {
        var missing = new List<string>();
        foreach (var dir in CollectTensorRtSearchDirs())
        {
            if (!File.Exists(Path.Combine(dir, "libnvinfer.so.10")))
                continue;

            _tensorRtDirectory = dir;
            missing.Clear();
            foreach (var library in TensorRtLibraries)
            {
                var path = Path.Combine(dir, library);
                if (!File.Exists(path))
                {
                    if (TensorRtRequiredLibraries.Contains(library))
                        missing.Add(library);
                    continue;
                }

                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    LoadedHandles.Add(handle);
                }
                else if (TensorRtRequiredLibraries.Contains(library))
                {
                    missing.Add(library);
                }
            }

            if (missing.Count == 0)
                return null;

            return $"TensorRT 10 found in {dir}, but {string.Join(", ", missing)} could not be loaded";
        }

        return "TensorRT 10 runtime not found (libnvinfer.so.10). Install TensorRT 10, set TENSORRT_ROOT, " +
               "or add its lib folder to LD_LIBRARY_PATH";
    }

    private static List<string> CollectTensorRtSearchDirs()
    {
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !dirs.Contains(dir))
                dirs.Add(dir);
        }

        foreach (var variable in new[] { "TENSORRT_ROOT", "TENSORRT_PATH", "TRT_PATH", "TENSORRT_HOME" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root))
                continue;

            Add(root);
            Add(Path.Combine(root, "lib"));
            Add(Path.Combine(root, "lib64"));
        }

        foreach (var root in new[] { "/usr/local/TensorRT", "/opt/TensorRT", "/usr/src/tensorrt" })
        {
            Add(Path.Combine(root, "lib"));
            Add(Path.Combine(root, "lib64"));
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty)
                               .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(entry.Trim());
        }

        Add("/usr/lib/x86_64-linux-gnu");
        Add("/usr/lib64");
        Add("/usr/lib");
        return dirs;
    }

    private static bool TryLoad(string library, List<string> searchDirs)
    {
        if (NativeLibrary.TryLoad(library, out var handle))
        {
            LoadedHandles.Add(handle);
            return true;
        }

        foreach (var dir in searchDirs)
        {
            var path = Path.Combine(dir, library);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
            {
                LoadedHandles.Add(handle);
                return true;
            }
        }

        return false;
    }

    private static List<string> CollectSearchDirs()
    {
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !dirs.Contains(dir))
                dirs.Add(dir);
        }

        var assemblyDir = Path.GetDirectoryName(typeof(LinuxCudaRuntime).Assembly.Location);
        Add(Path.Combine(AppContext.BaseDirectory, "CudaRuntime"));
        if (!string.IsNullOrEmpty(assemblyDir))
            Add(Path.Combine(assemblyDir, "CudaRuntime"));

        foreach (var variable in new[] { "CUDA_PATH", "CUDA_HOME" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root))
                continue;

            Add(Path.Combine(root, "lib64"));
            Add(Path.Combine(root, "lib"));
            Add(Path.Combine(root, "targets", "x86_64-linux", "lib"));
        }

        // Toolkit installs: NVIDIA's runfile/deb (/usr/local/cuda-13.x), Arch (/opt/cuda), distro packages.
        // CUDA 13 comes first because that is what this ONNX Runtime's provider imports.
        var roots = new List<string> { "/usr/local/cuda", "/opt/cuda" };
        if (Directory.Exists("/usr/local"))
        {
            roots.AddRange(Directory.GetDirectories("/usr/local", "cuda-13*").OrderByDescending(d => d, StringComparer.Ordinal));
            roots.AddRange(Directory.GetDirectories("/usr/local", "cuda-12*").OrderByDescending(d => d, StringComparer.Ordinal));
        }

        foreach (var root in roots)
        {
            Add(Path.Combine(root, "lib64"));
            Add(Path.Combine(root, "targets", "x86_64-linux", "lib"));
        }

        Add("/usr/lib/x86_64-linux-gnu");
        Add("/usr/lib64");
        return dirs;
    }
}
