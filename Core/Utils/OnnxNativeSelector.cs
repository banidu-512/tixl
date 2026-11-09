using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using T3.Core.Logging;

namespace T3.Core.Utils;

/// <summary>
/// Windows ships two ONNX Runtime natives with identical file names: the GPU build (CUDA, TensorRT) in
/// <c>runtimes/win-x64/native</c> and the DirectML build in <c>runtimes/win-x64-directml/native</c>. A process can bind
/// only one of them per managed ONNX Runtime assembly, so the first <see cref="Select"/> for an assembly decides, and the
/// other provider family then needs a restart. Call it before the first ONNX Runtime type is used (a native call).
/// On Linux and macOS there is nothing to choose and the call does nothing.
/// </summary>
public static class OnnxNativeSelector
{
    public enum Build
    {
        /// <summary>The GPU build: CUDA and TensorRT providers on Windows and Linux, CoreML on macOS.</summary>
        Gpu,
        DirectMl,
    }

    private const string GpuFolder = "runtimes/win-x64/native";
    private const string DirectMlFolder = "runtimes/win-x64-directml/native";

    private static readonly object Lock = new();
    private static readonly Dictionary<Assembly, Build> Selected = new();

    /// <summary>Binds the native for <paramref name="ortAssembly"/> (the Microsoft.ML.OnnxRuntime assembly of the calling
    /// operator package) on first use and returns the build in effect, which differs from
    /// <paramref name="preferred"/> when the other one was bound earlier or its files are not shipped.</summary>
    public static Build Select(Assembly ortAssembly, Build preferred)
    {
        if (!OperatingSystem.IsWindows())
            return Build.Gpu;

        lock (Lock)
        {
            if (Selected.TryGetValue(ortAssembly, out var existing))
                return existing;

            var assemblyDir = Path.GetDirectoryName(ortAssembly.Location) ?? AppContext.BaseDirectory;
            var chosen = preferred;
            if (!HasNative(assemblyDir, chosen))
            {
                chosen = chosen == Build.Gpu ? Build.DirectMl : Build.Gpu;
                if (!HasNative(assemblyDir, chosen))
                    chosen = preferred; // neither shipped here: leave the default probing alone
                else
                    Log.Info($"[ONNX] The {Describe(preferred)} build is not shipped next to {Path.GetFileName(ortAssembly.Location)}, using {Describe(chosen)}");
            }

            try
            {
                var folder = Path.Combine(assemblyDir, chosen == Build.Gpu ? GpuFolder : DirectMlFolder);
                NativeLibrary.SetDllImportResolver(ortAssembly, (name, _, _) => Resolve(name, folder));
            }
            catch (InvalidOperationException)
            {
                // A resolver is already set for this assembly: the default probing stays in charge and the
                // requested build may differ, so say so instead of pretending the choice took effect.
                Log.Warning($"[ONNX] A native resolver is already set for {Path.GetFileName(ortAssembly.Location)}; " +
                            $"the requested {(chosen == Build.Gpu ? "CUDA" : "DirectML")} build may not be the one that loads.");
            }

            Selected[ortAssembly] = chosen;
            return chosen;
        }
    }

    public static string Describe(Build build) => build == Build.Gpu ? "CUDA" : "DirectML";

    /// <summary>
    /// Whether <paramref name="build"/>'s native is shipped next to <paramref name="ortAssembly"/>. ONNX Runtime
    /// DirectML builds stopped after 1.24.4, so callers that offer DirectML as a choice need this to say
    /// "not available in this runtime" instead of "restart to switch" (which would never succeed).
    /// </summary>
    public static bool IsShipped(Assembly ortAssembly, Build build)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var assemblyDir = Path.GetDirectoryName(ortAssembly.Location) ?? AppContext.BaseDirectory;
        return HasNative(assemblyDir, build);
    }

    private static bool HasNative(string assemblyDir, Build build)
        => File.Exists(Path.Combine(assemblyDir, build == Build.Gpu ? GpuFolder : DirectMlFolder, "onnxruntime.dll"));

    private static IntPtr Resolve(string libraryName, string folder)
    {
        if (!libraryName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        // The DirectML runtime beside the build is preferred over a (possibly older) system DirectML.dll
        var directMl = Path.Combine(folder, "DirectML.dll");
        if (File.Exists(directMl))
            NativeLibrary.TryLoad(directMl, out _);

        var file = Path.Combine(folder, libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll");
        return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
    }
}
