#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace T3.Editor.Gui.AiModels;

/** The operator families the browser manages models for. */
internal enum AiModelFamily
{
    DepthAnything,
    StreamDiffusion,
    MediaPipe,
}

internal static class AiModelFamilyExtension
{
    public static string StoreFolder(this AiModelFamily family) => family switch
        {
            AiModelFamily.DepthAnything => T3.Core.Settings.AiModelSettings.DepthAnythingFamilyFolder,
            AiModelFamily.StreamDiffusion => T3.Core.Settings.AiModelSettings.StreamDiffusionFamilyFolder,
            AiModelFamily.MediaPipe => T3.Core.Settings.AiModelSettings.MediapipeFamilyFolder,
            _ => family.ToString(),
        };

    public static string Title(this AiModelFamily family) => family switch
        {
            AiModelFamily.DepthAnything => "Depth Anything",
            AiModelFamily.StreamDiffusion => "Stream Diffusion",
            AiModelFamily.MediaPipe => "MediaPipe",
            _ => family.ToString(),
        };

    public static string Description(this AiModelFamily family) => family switch
        {
            AiModelFamily.DepthAnything => "Monocular depth estimation ([DepthAnything] operators)",
            AiModelFamily.StreamDiffusion => "Text/image to image diffusion ([StreamDiffusion] operator)",
            AiModelFamily.MediaPipe => "Face, hand, pose tracking and segmentation ([Face…], [Hand…], [Pose…] operators)",
            _ => string.Empty,
        };
}

/// <summary>One file of a catalog entry, downloaded to a fixed name inside the family's store folder.</summary>
internal sealed record CatalogFile(string Url, string TargetFileName, long ExpectedSizeBytes);

/// <summary>
/// A model the model browser can install (or an export that has to be produced locally). Downloaded
/// files land in the model store, keeping multi-gigabyte weights out of the git repository.
/// </summary>
internal sealed class ModelCatalogEntry
{
    public required string Id { get; init; }
    public required AiModelFamily Family { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";

    /** Empty for entries that are only listed (installed with local export tooling). */
    public IReadOnlyList<CatalogFile> Files { get; init; } = [];

    /// <summary>0 for entries that are only listed (installed with the export tooling, not downloadable).</summary>
    public long TotalSizeBytes { get; init; }

    /** Installed as the family's default by the one-click setup. */
    public bool IsDefault { get; init; }

    /** Not a download: the row shows how the export is produced locally. */
    public bool RequiresLocalExport { get; init; }

    /** When true, presence is checked against the packaged assets too (MediaPipe models ship with TiXL). */
    public bool ShippedWithTiXl { get; init; }

    /**
     * Subfolder of the family directory the files install into. Families of single model files
     * (DepthAnything, MediaPipe) use none; StreamDiffusion installs one folder per export, which is
     * what the operator points its EnginePath at.
     */
    public string InstallSubFolder => Family == AiModelFamily.StreamDiffusion ? Id : string.Empty;

    public string TargetPath(string fileName)
    {
        var subFolder = InstallSubFolder;
        return string.IsNullOrEmpty(subFolder)
                   ? Path.Combine(T3.Core.Settings.AiModelSettings.FamilyDirectory(Family.StoreFolder()), fileName)
                   : Path.Combine(T3.Core.Settings.AiModelSettings.FamilyDirectory(Family.StoreFolder()), subFolder, fileName);
    }

    /** A local-export entry lives in its own folder under the family directory. */
    public string ExportFolder => Path.Combine(T3.Core.Settings.AiModelSettings.FamilyDirectory(Family.StoreFolder()), Id);

    /** The folder the entry's files live in (family folder, or one subfolder per export). */
    public string StoreDirectory
        => RequiresLocalExport || Files.Count == 0 ? ExportFolder : Path.GetDirectoryName(TargetPath(Files[0].TargetFileName)) ?? ExportFolder;

    public bool IsInstalled()
    {
        if (RequiresLocalExport)
            return Directory.Exists(ExportFolder);

        foreach (var file in Files)
        {
            if (!File.Exists(TargetPath(file.TargetFileName)))
                return false;
        }

        return true;
    }

    /** True when the files are found anywhere (store, package assets). Only used for shipped models. */
    public bool IsAvailable()
    {
        if (IsInstalled())
            return true;

        if (!ShippedWithTiXl)
            return false;

        return TryResolveShippedFile(Family, Files[0].TargetFileName, out _);
    }

    /** Checks the packaged assets through the asset registry, as the operators do. */
    public static bool TryResolveShippedFile(AiModelFamily family, string fileName, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            if (T3.Core.Resource.Assets.AssetRegistry.TryResolveAddress($"{family.StoreFolder()}:{fileName}", null, out var resolved, out _, logWarnings: false))
            {
                fullPath = resolved;
                return true;
            }
        }
        catch
        {
            // The registry may not be populated yet; absence then simply reads as "not available"
        }

        return false;
    }
}

/// <summary>
/// The models known to the settings window's browser. URLs are checked against the download script
/// shipped with the DepthAnything package and Google's MediaPipe model bucket; every entry must be
/// reachable without authentication.
/// </summary>
internal static class AiModelCatalog
{
    private const string HuggingFace = "https://huggingface.co";
    private const string MediaPipeModels = "https://storage.googleapis.com/mediapipe-models";
    private const string MediaPipeAssets = "https://storage.googleapis.com/mediapipe-assets";

    public static readonly IReadOnlyList<ModelCatalogEntry> Entries =
    [
        // ---------------------------------------------------------------- DepthAnything
        new()
        {
            Id = "da3-small",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything 3 – Small",
            Description = "Fastest DA3 model. Recommended default for interactive depth.",
            TotalSizeBytes = 96 * 1024 * 1024,
            IsDefault = true,
            Files =
            [
                new($"{HuggingFace}/zerochocobo/DepthAnything3_ONNX/resolve/main/da3_small.onnx", "da3-small.onnx", 96 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "da3-small-int8",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything 3 – Small (int8)",
            Description = "Quantized small model; lower quality, a quarter of the download.",
            TotalSizeBytes = 37 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/ohaiibuzzle/DA3-ONNX/resolve/main/da3_s_504x378_int8.onnx", "da3-small-int8.onnx", 37 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "da3-base-fp16",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything 3 – Base (fp16)",
            Description = "More detail than Small, still realtime on most GPUs.",
            TotalSizeBytes = 198 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/Heliosoph/da3-base-onnx/resolve/main/model_fp16.onnx", "da3-base-fp16.onnx", 198 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "da3-mono-large-int8",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything 3 – Mono Large (int8)",
            Description = "Large single-image model, quantized.",
            TotalSizeBytes = 431 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/ohaiibuzzle/DA3-ONNX/resolve/main/da3mono_l_504x378_int8.onnx", "da3-mono-large-int8.onnx", 431 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "da3-metric-large-fp16",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything 3 – Metric Large (fp16)",
            Description = "Metric depth in meters; largest and slowest DA3 model.",
            TotalSizeBytes = 670 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/Heliosoph/da3metric-large-onnx/resolve/main/model_fp16.onnx", "da3-metric-large-fp16.onnx", 670 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "v2-small-fp16",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything V2 – Small (fp16)",
            Description = "The classic V2 small export, kept for older compositions.",
            TotalSizeBytes = 50 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/onnx-community/depth-anything-v2-small/resolve/main/onnx/model_fp16.onnx", "depth-anything-v2-small-fp16.onnx", 50 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "v2-base-fp16",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything V2 – Base (fp16)",
            Description = "The classic V2 base export, kept for older compositions.",
            TotalSizeBytes = 194 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/onnx-community/depth-anything-v2-base/resolve/main/onnx/model_fp16.onnx", "depth-anything-v2-base-fp16.onnx", 194 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "v2-large-fp16",
            Family = AiModelFamily.DepthAnything,
            DisplayName = "Depth Anything V2 – Large (fp16)",
            Description = "The classic V2 large export, kept for older compositions.",
            TotalSizeBytes = 672 * 1024 * 1024,
            Files =
            [
                new($"{HuggingFace}/onnx-community/depth-anything-v2-large/resolve/main/onnx/model_fp16.onnx", "depth-anything-v2-large-fp16.onnx", 672 * 1024 * 1024),
            ],
        },

        // ---------------------------------------------------------------- StreamDiffusion
        new()
        {
            Id = "streamdiffusion-v2",
            Family = AiModelFamily.StreamDiffusion,
            DisplayName = "StreamDiffusion V2 (recommended)",
            Description = "Causal Wan2.1 1.3B video model - the latest and best variant. Produces its export with the scripts in Operators/StreamDiffusion/tools/streamdiffusionv2.",
            RequiresLocalExport = true,
        },
        new()
        {
            Id = "sd-turbo-onnx",
            Family = AiModelFamily.StreamDiffusion,
            DisplayName = "SD-Turbo (ONNX, fp16)",
            Description = "Official ONNX Runtime export, per-component. 1 step, realtime capable.",
            TotalSizeBytes = 2_462L * 1024 * 1024,
            IsDefault = true,
            Files =
            [
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/unet/model.onnx", "unet.onnx", 1_652 * 1024 * 1024),
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/text_encoder/model.onnx", "text_encoder.onnx", 649 * 1024 * 1024),
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/vae_decoder/model.onnx", "vae_decoder.onnx", 95 * 1024 * 1024),
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/vae_encoder/model.onnx", "vae_encoder.onnx", 65 * 1024 * 1024),
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/tokenizer/vocab.json", "vocab.json", 1 * 1024 * 1024),
                new($"{HuggingFace}/onnxruntime/sd-turbo/resolve/main/tokenizer/merges.txt", "merges.txt", 512 * 1024),
            ],
        },

        // Exports that have to be produced locally; listed so the browser answers "what works".
        new()
        {
            Id = "sd15-onnx",
            Family = AiModelFamily.StreamDiffusion,
            DisplayName = "SD 1.5 (own export)",
            Description = "Export with optimum: optimum-cli export onnx --model stable-diffusion-v1-5 --task stable-diffusion <folder>. Multi-step, use Guidance 7.5+.",
            RequiresLocalExport = true,
        },
        new()
        {
            Id = "sdxl-turbo-onnx",
            Family = AiModelFamily.StreamDiffusion,
            DisplayName = "SDXL-Turbo (not usable yet)",
            Description = "ONNX exports exist, but the pipeline loads a single text encoder, so SDXL's second encoder is not wired up. Shown for completeness; no download.",
            RequiresLocalExport = true,
        },

        // ---------------------------------------------------------------- MediaPipe
        new()
        {
            Id = "face-landmarker",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Face Landmarker",
            Description = "Used by [FaceLandmarkDetection].",
            TotalSizeBytes = 4 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/face_landmarker/face_landmarker/float16/1/face_landmarker.task", "face_landmarker.task", 4 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "hand-landmarker",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Hand Landmarker",
            Description = "Used by [HandLandmarkDetection] and [GestureRecognition].",
            TotalSizeBytes = 8 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task", "hand_landmarker.task", 8 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "pose-landmarker",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Pose Landmarker",
            Description = "Used by [PoseLandmarkDetection].",
            TotalSizeBytes = 9 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task", "pose_landmarker.task", 9 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "gesture-recognizer",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Gesture Recognizer",
            Description = "Used by [GestureRecognition].",
            TotalSizeBytes = 8 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/gesture_recognizer/gesture_recognizer/float16/1/gesture_recognizer.task", "gesture_recognizer.task", 8 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "face-detection-short",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Face Detector (short range)",
            Description = "Used by [FaceDetection].",
            TotalSizeBytes = 230 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeAssets}/face_detection_short_range.tflite", "blaze_face_short_range.tflite", 230 * 1024),
            ],
        },
        new()
        {
            Id = "face-detection-full",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Face Detector (full range)",
            Description = "Full-range face detection; works at larger distances than the short-range model.",
            TotalSizeBytes = 1084 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeAssets}/face_detection_full_range.tflite", "face_detection_full_range.tflite", 1084 * 1024),
            ],
        },
        new()
        {
            Id = "selfie-segmenter",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Selfie Segmenter",
            Description = "Used by [ImageSegmentation].",
            TotalSizeBytes = 256 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/image_segmenter/selfie_segmenter/float16/latest/selfie_segmenter.tflite", "selfie_segmenter.tflite", 256 * 1024),
            ],
        },
        new()
        {
            Id = "selfie-segmenter-landscape",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Selfie Segmenter (landscape)",
            Description = "Used by [ImageSegmentation] in landscape mode.",
            TotalSizeBytes = 256 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/image_segmenter/selfie_segmenter_landscape/float16/latest/selfie_segmenter_landscape.tflite", "selfie_segmenter_landscape.tflite", 256 * 1024),
            ],
        },
        new()
        {
            Id = "selfie-multiclass",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Selfie Multiclass Segmenter",
            Description = "Used by [ImageSegmentation]; segments hair, skin, clothes and background.",
            TotalSizeBytes = 16 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite", "selfie_multiclass_256x256.tflite", 16 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "efficientdet-lite0",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Object Detector (EfficientDet Lite0)",
            Description = "Used by [ObjectDetection].",
            TotalSizeBytes = 8 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/object_detector/efficientdet_lite0/float32/latest/efficientdet_lite0.tflite", "efficientdet_lite0.tflite", 8 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "efficientdet-lite2",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Object Detector (EfficientDet Lite2)",
            Description = "Used by [ObjectDetection]; more accuracy than Lite0, slower.",
            TotalSizeBytes = 8 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/object_detector/efficientdet_lite2/float32/latest/efficientdet_lite2.tflite", "efficientdet_lite2.tflite", 8 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "ssd-mobilenet-v2",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "Object Detector (SSD MobileNet v2)",
            Description = "Alternative object detection model for [ObjectDetection].",
            TotalSizeBytes = 7 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeModels}/object_detector/ssd_mobilenet_v2/float32/latest/ssd_mobilenet_v2.tflite", "ssd_mobilenet_v2.tflite", 7 * 1024 * 1024),
            ],
        },
        new()
        {
            Id = "deeplabv3",
            Family = AiModelFamily.MediaPipe,
            DisplayName = "DeepLab v3 Segmenter",
            Description = "Used by [ImageSegmentation]; general-purpose segmentation.",
            TotalSizeBytes = 3 * 1024 * 1024,
            ShippedWithTiXl = true,
            Files =
            [
                new($"{MediaPipeAssets}/deeplabv3.tflite", "deeplabv3.tflite", 3 * 1024 * 1024),
            ],
        },
    ];

    public static readonly AiModelFamily[] Families = (AiModelFamily[])Enum.GetValues<AiModelFamily>();

    /** Per-family index built once: the settings panel draws it every frame, so it must not iterate. */
    private static readonly Dictionary<AiModelFamily, IReadOnlyList<ModelCatalogEntry>> _entriesByFamily = BuildFamilyIndex();

    private static Dictionary<AiModelFamily, IReadOnlyList<ModelCatalogEntry>> BuildFamilyIndex()
    {
        var index = new Dictionary<AiModelFamily, IReadOnlyList<ModelCatalogEntry>>();
        foreach (var family in Families)
        {
            var list = new List<ModelCatalogEntry>();
            foreach (var entry in Entries)
            {
                if (entry.Family == family)
                    list.Add(entry);
            }
            index[family] = list;
        }
        return index;
    }

    public static IReadOnlyList<ModelCatalogEntry> ForFamily(AiModelFamily family)
        => _entriesByFamily.TryGetValue(family, out var list) ? list : [];

    /** The entries the one-click setup downloads when missing: the fastest working model per family. */
    public static IEnumerable<ModelCatalogEntry> Defaults
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.IsDefault)
                    yield return entry;
            }
        }
    }

    public static string FormatSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (double)(1024 * 1024 * 1024):0.#} GB",
            >= 1024 * 1024 => $"{Math.Round(bytes / (double)(1024 * 1024))} MB",
            >= 1024 => $"{Math.Round(bytes / 1024.0)} KB",
            _ => $"{bytes} B",
        };
}
