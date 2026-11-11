#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using T3.Core.Logging;
using T3.Core.Settings;
using T3.Core.Utils;

namespace T3.Editor.Gui.AiModels;

internal enum AiCheckResult { Ok, Fixable, Missing, Info }

/** One row of the setup check shown under Settings > AI Models. */
internal sealed class AiSetupCheckItem
{
    public required string Title;
    public AiCheckResult Result;
    public string Detail = "";

    /** Set when the one-click setup can resolve the item by downloading a model. */
    public ModelCatalogEntry? EntryToInstall;

    public string ResultText => Result switch
        {
            AiCheckResult.Ok => "OK",
            AiCheckResult.Fixable => "Setup available",
            AiCheckResult.Missing => "Missing",
            AiCheckResult.Info => "Optional",
            _ => "",
        };
}

/// <summary>
/// Checks everything the AI operators need to run: the engine runtimes (CUDA/TensorRT/DirectML),
/// a writable model store, and the models of each family. <see cref="FixAllAsync"/> then downloads
/// the fastest default models of every family that has nothing usable, so the operators work
/// without the user having to hunt for files.
/// </summary>
internal static class AiSetupChecker
{
    public static readonly List<AiSetupCheckItem> LastResults = new();

    public static DateTime LastCheckAt = DateTime.MinValue;

    /** While true a fix-up download is in progress; the settings page polls until it ends. */
    public static bool IsFixing;

    public static string FixProgressText = "";

    private static readonly string[] SdRequiredFiles =
    [
        "unet.onnx", "text_encoder.onnx", "vae_decoder.onnx",
    ];

    private static readonly string[] V2RequiredFiles =
    [
        "dit_step.onnx", "dit_manifest.json", "text_encoder.onnx", "vae_decoder.onnx",
    ];

    public static List<AiSetupCheckItem> Run()
    {
        var items = new List<AiSetupCheckItem>();

        CheckEngineRuntimes(items);
        CheckModelStore(items);
        CheckFamily(AiModelFamily.DepthAnything, items);
        CheckFamily(AiModelFamily.MediaPipe, items);
        CheckFamily(AiModelFamily.StreamDiffusion, items);

        LastResults.Clear();
        LastResults.AddRange(items);
        LastCheckAt = DateTime.Now;
        return items;
    }

    private static void CheckEngineRuntimes(List<AiSetupCheckItem> items)
    {
        if (OperatingSystem.IsMacOS())
        {
            items.Add(new AiSetupCheckItem
                          {
                              Title = "ML acceleration (CoreML)",
                              Result = AiCheckResult.Info,
                              Detail = "macOS uses CoreML through the CPU ONNX Runtime build; no CUDA setup is needed.",
                          });
            return;
        }

        // CUDA: the primary GPU path of the ONNX operators. Probing loads the runtime, exactly like the operators do.
        string cudaReason;
        var cudaOk = OperatingSystem.IsWindows()
                         ? WindowsCudaRuntime.TryPrepare(out cudaReason)
                         : LinuxCudaRuntime.TryPrepare(out cudaReason);
        items.Add(new AiSetupCheckItem
                      {
                          Title = "CUDA 12 runtime (cuDNN 9)",
                          Result = cudaOk ? AiCheckResult.Ok : AiCheckResult.Info,
                          Detail = cudaOk
                                       ? "GPU acceleration for ONNX models is available."
                                       : $"CUDA runtime not found. {cudaReason}",
                      });

        // TensorRT: optional but the fastest provider for diffusion
        string trtReason;
        string trtDirectory;
        var trtOk = OperatingSystem.IsWindows()
                        ? WindowsCudaRuntime.TryPrepareTensorRt(out trtReason, out trtDirectory)
                        : LinuxCudaRuntime.TryPrepareTensorRt(out trtReason, out trtDirectory);
        items.Add(new AiSetupCheckItem
                      {
                          Title = "TensorRT 10 runtime",
                          Result = trtOk ? AiCheckResult.Ok : AiCheckResult.Info,
                          Detail = trtOk
                                       ? $"Found in {trtDirectory}. TensorRT engines are compiled on first use and cached."
                                       : $"TensorRT not found ({trtReason}). Optional: speeds up diffusion several-fold once installed.",
                      });

        if (OperatingSystem.IsWindows())
        {
            var directMlAvailable = IsDirectMlShipped();
            items.Add(new AiSetupCheckItem
                          {
                              Title = "DirectML runtime",
                              Result = directMlAvailable ? AiCheckResult.Ok : AiCheckResult.Info,
                              Detail = directMlAvailable
                                           ? "DirectML build is shipped; an operator selecting DirectML needs an editor restart to switch builds."
                                           : "DirectML build is not shipped in this installation; CUDA is the GPU path.",
                          });
        }
    }

    private static bool IsDirectMlShipped()
    {
        var configured = AiModelSettings.Config.DirectMlDirectory;
        if (!string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "DirectML.dll")))
            return true;

        var baseDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "runtimes", "win-x64-directml", "native", "onnxruntime.dll"));
    }

    private static void CheckModelStore(List<AiSetupCheckItem> items)
    {
        var root = AiModelSettings.ModelsRoot;
        items.Add(new AiSetupCheckItem
                      {
                          Title = "Model store",
                          Result = AiCheckResult.Ok,
                          Detail = root,
                      });
    }

    private static void CheckFamily(AiModelFamily family, List<AiSetupCheckItem> items)
    {
        var entries = AiModelCatalog.ForFamily(family).ToList();
        var downloadable = entries.Where(e => !e.RequiresLocalExport).ToList();
        var installed = downloadable.Where(e => e.IsInstalled()).ToList();
        var available = downloadable.Where(e => e.IsAvailable()).ToList();
        var defaultEntry = downloadable.FirstOrDefault(e => e.IsDefault) ?? downloadable.FirstOrDefault();

        switch (family)
        {
            case AiModelFamily.DepthAnything:
            {
                if (installed.Count > 0)
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "Depth models",
                                      Result = AiCheckResult.Ok,
                                      Detail = string.Join(", ", installed.Select(e => e.DisplayName)),
                                  });
                }
                else if (available.Count > 0 || HasAnyDepthModelInStore() || HasShippedDepthModel())
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "Depth models",
                                      Result = AiCheckResult.Info,
                                      Detail = "A bundled V2 model is used. The DA3 models are faster and more accurate - download one below.",
                                      EntryToInstall = defaultEntry,
                                  });
                }
                else
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "Depth models",
                                      Result = AiCheckResult.Fixable,
                                      Detail = "No depth model installed. [DepthAnything] shows an error until one is downloaded.",
                                      EntryToInstall = defaultEntry,
                                  });
                }

                break;
            }

            case AiModelFamily.MediaPipe:
            {
                var missing = downloadable.Where(e => !e.IsAvailable()).ToList();
                if (missing.Count == 0)
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "MediaPipe models",
                                      Result = AiCheckResult.Ok,
                                      Detail = "All tracking models are available (bundled or in the model store).",
                                  });
                }
                else
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "MediaPipe models",
                                      Result = AiCheckResult.Fixable,
                                      Detail = $"{missing.Count} model(s) missing: {string.Join(", ", missing.Select(e => e.DisplayName))}. " +
                                               "The affected operators report \"Model not found\" until they are installed.",
                                      EntryToInstall = missing.First(),
                                  });
                }

                break;
            }

            case AiModelFamily.StreamDiffusion:
            {
                var usableExport = FindUsableExport();
                if (usableExport != null)
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "Diffusion models",
                                      Result = AiCheckResult.Ok,
                                      Detail = $"Usable export: {Path.GetFileName(usableExport!.TrimEnd(Path.DirectorySeparatorChar))}",
                                  });
                }
                else
                {
                    items.Add(new AiSetupCheckItem
                                  {
                                      Title = "Diffusion models",
                                      Result = AiCheckResult.Fixable,
                                      Detail = "No usable ONNX export found. [StreamDiffusion] cannot start without one. " +
                                               "Recommended: a StreamDiffusion V2 export (see Operators/StreamDiffusion/tools). " +
                                               "Quick start: SD-Turbo, about 2.5 GB, via Download defaults.",
                                      EntryToInstall = defaultEntry,
                                  });
                }

                break;
            }
        }
    }

    /** The one-click fix: download every fixable family's default model, then re-check. */
    public static async Task FixAllAsync()
    {
        if (IsFixing)
            return;

        IsFixing = true;
        FixProgressText = "Checking…";
        try
        {
            var pending = LastResults.Where(i => i.EntryToInstall != null
                                                 && i.Result is AiCheckResult.Fixable
                                                                   or AiCheckResult.Missing)
                                     .Select(i => i.EntryToInstall!)
                                     .ToList();

            // MediaPipe can miss several entries at once; queue them all
            var pipeMissing = LastResults.Where(i => i.Title == "MediaPipe models" && i.Result == AiCheckResult.Fixable)
                                         .SelectMany(_ => AiModelCatalog.ForFamily(AiModelFamily.MediaPipe)
                                                                        .Where(e => !e.RequiresLocalExport && !e.IsAvailable()));
            pending = pending.Union(pipeMissing).ToList();

            foreach (var entry in pending)
            {
                FixProgressText = $"Downloading {entry.DisplayName}…";
                Log.Info($"[AI Models] One-click setup: installing '{entry.DisplayName}'");
                await AiModelDownloadService.GetOrStartTask(entry);
            }

            FixProgressText = "Re-checking…";
            Run();
            FixProgressText = "";
            Log.Info("[AI Models] One-click setup finished");
        }
        finally
        {
            IsFixing = false;
        }
    }

    private static bool HasAnyDepthModelInStore()
    {
        var folder = AiModelSettings.FamilyDirectory(AiModelSettings.DepthAnythingFamilyFolder);
        if (!Directory.Exists(folder))
            return false;

        return Directory.EnumerateFiles(folder, "*.onnx").Any();
    }

    /** The DepthAnything package can ship a small model; the operators fall back to it. */
    private static bool HasShippedDepthModel()
    {
        foreach (var shippedName in new[] { "depth-anything-v2-small-fp16.onnx", "da3-small-fp16.onnx", "da3-small.onnx" })
        {
            if (ModelCatalogEntry.TryResolveShippedFile(AiModelFamily.DepthAnything, shippedName, out _))
                return true;
        }

        return false;
    }

    /** Any folder in the store that satisfies the SD or the V2 file layout. */
    private static string? FindUsableExport()
    {
        var familyDirectory = AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder);
        if (!Directory.Exists(familyDirectory))
            return null;

        foreach (var directory in Directory.GetDirectories(familyDirectory))
        {
            if (V2RequiredFiles.All(f => File.Exists(Path.Combine(directory, f))))
                return directory;

            if (SdRequiredFiles.All(f => File.Exists(Path.Combine(directory, f)))
                && (File.Exists(Path.Combine(directory, "tokenizer.json"))
                    || (File.Exists(Path.Combine(directory, "vocab.json")) && File.Exists(Path.Combine(directory, "merges.txt")))))
                return directory;
        }

        return null;
    }
}
