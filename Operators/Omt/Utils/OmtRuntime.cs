#nullable enable
using System.Runtime.InteropServices;

namespace Lib.Utils;

/// <summary>
/// Locates the native VMX codec (<c>libvmx</c>) that libomtnet compresses video with, and tells the operators
/// whether they can run. The managed OMT protocol library ships with this package on every platform; the codec
/// behind it exists as an official Windows and Apple Silicon binary, while other platforms have to build it from
/// the libvmx sources — so it is probed at runtime instead of assumed.
/// </summary>
internal static class OmtRuntime
{
    /// <summary>True where the native codec loads. Probed once; the result does not change while running.</summary>
    public static bool IsAvailable => _isAvailable ??= Probe();

    /// <summary>What an operator reports while the codec is missing.</summary>
    public static string MissingMessage { get; } = BuildMissingMessage();

    private static bool Probe()
    {
        try
        {
            foreach (var candidate in Candidates())
            {
                if (!File.Exists(candidate))
                    continue;

                if (NativeLibrary.TryLoad(candidate, out _))
                    return true;
            }

            // An installation outside the package (or a system-wide libvmx) is still fine.
            if (NativeLibrary.TryLoad(FileName, out _))
                return true;
        }
        catch (Exception e)
        {
            Log.Warning($"Probing the OMT video codec failed: {e.Message}");
        }

        Log.Warning(MissingMessage);
        return false;
    }

    /// <summary>
    /// The places the load context looks: beside the package assembly, then the NuGet runtimes layout. Checked
    /// here as well so an operator can say what is missing instead of failing on the first frame.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        foreach (var root in SearchRoots())
        {
            yield return Path.Combine(root, FileName);

            var nativeDirectory = Path.Combine(root, "runtimes", RuntimeIdentifier, "native");
            yield return Path.Combine(nativeDirectory, FileName);
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(OmtRuntime).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return assemblyDirectory;

        if (!string.IsNullOrEmpty(AppContext.BaseDirectory) && AppContext.BaseDirectory != assemblyDirectory)
            yield return AppContext.BaseDirectory;
    }

    private static string BuildMissingMessage()
    {
        if (OperatingSystem.IsWindows())
            return $"OMT video codec not found. {FileName} should sit in the Omt package's runtimes folder; reinstalling TiXL restores it.";

        if (OperatingSystem.IsMacOS())
            return $"OMT video codec not found. {FileName} ships for Apple Silicon only; on an Intel Mac build libvmx from github.com/openmediatransport/libvmx and place it next to the Omt package.";

        return $"OMT video codec not found. Build libvmx from github.com/openmediatransport/libvmx and place {FileName} next to the Omt package (or install it system-wide).";
    }

    private static string FileName =>
        OperatingSystem.IsWindows() ? "libvmx.dll"
            : OperatingSystem.IsMacOS() ? "libvmx.dylib"
                : "libvmx.so";

    private static string RuntimeIdentifier
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
                                   {
                                       Architecture.Arm64 => "arm64",
                                       Architecture.X86   => "x86",
                                       _                  => "x64",
                                   };

            return OperatingSystem.IsWindows() ? $"win-{architecture}"
                       : OperatingSystem.IsMacOS() ? $"osx-{architecture}"
                           : $"linux-{architecture}";
        }
    }

    private static bool? _isAvailable;
}
