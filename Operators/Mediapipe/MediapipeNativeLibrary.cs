using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mediapipe;

/// <summary>
/// macOS: the bundled libmediapipe_c.dylib leaves its OpenCL imports to dynamic lookup, and Apple
/// Silicon macOS ships no OpenCL, so dyld refuses the image unless the symbols already exist in the
/// process ("symbol not found in flat namespace '_clBuildProgram'"). libmediapipe_opencl_stub.dylib
/// (built next to the native by the macOS CI and Installer/macOS/build-dmg.sh) exports harmless
/// no-ops - the CPU delegate paths the operators use never call them. Loading it here runs before
/// the first mediapipe_c P/Invoke in this assembly resolves the real library. No-op elsewhere.
/// </summary>
internal static class MediapipeNativeLibrary
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var stub = Path.Combine(AppContext.BaseDirectory, "libmediapipe_opencl_stub.dylib");
        if (File.Exists(stub))
            NativeLibrary.TryLoad(stub, out _);
    }
}
