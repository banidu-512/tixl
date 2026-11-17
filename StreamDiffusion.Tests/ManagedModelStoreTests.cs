using t3.streamdiffusion.Onnx;
using T3.Core.Settings;
using Xunit;

namespace StreamDiffusion.Tests;

/// <summary>
/// Pins the contract between the model store (Settings > AI Models) and the operator: exports install
/// into one subfolder of <c>StreamDiffusion/</c> per model, and the operator's empty-EnginePath scan
/// finds the first of them that validates.
/// </summary>
public class ManagedModelStoreTests : IDisposable
{
    private readonly string _tempStore = Path.Combine(Path.GetTempPath(), "sd-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousModelsDirectory;

    public ManagedModelStoreTests()
    {
        _previousModelsDirectory = AiModelSettings.Config.ModelsDirectory;
        AiModelSettings.Config.ModelsDirectory = _tempStore;
    }

    public void Dispose()
    {
        AiModelSettings.Config.ModelsDirectory = _previousModelsDirectory ?? "";
        try { Directory.Delete(_tempStore, true); } catch { }
    }

    [Fact]
    public void FamilyDirectory_RespectsConfiguredStoreRoot()
    {
        Assert.Equal(Path.Combine(_tempStore, "StreamDiffusion"), AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder));
    }

    [Fact]
    public void StoredFile_IsFoundInTheFamilyFolder()
    {
        var familyDirectory = AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder);
        Directory.CreateDirectory(familyDirectory);
        File.WriteAllText(Path.Combine(familyDirectory, "probe.onnx"), "placeholder");

        Assert.True(AiModelSettings.TryGetStoredFile(AiModelSettings.StreamDiffusionFamilyFolder, "probe.onnx", out var path));
        Assert.Equal(Path.Combine(familyDirectory, "probe.onnx"), path);
    }

    [Fact]
    public void ExportSubfolder_LayoutIsFoundByTheOperatorScan()
    {
        // The layout the model browser produces: one folder per export inside the family folder
        var exportFolder = Path.Combine(AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder), "sd-turbo-onnx");
        Directory.CreateDirectory(exportFolder);
        foreach (var file in new[] { "unet.onnx", "text_encoder.onnx", "vae_decoder.onnx", "vocab.json", "merges.txt" })
            File.WriteAllText(Path.Combine(exportFolder, file), "placeholder");

        var found = ScanForValidExport();
        Assert.Equal(exportFolder, found);
    }

    [Fact]
    public void SiblingFolderWithMissingFiles_IsSkippedByTheOperatorScan()
    {
        var familyDirectory = AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder);
        Directory.CreateDirectory(Path.Combine(familyDirectory, "broken-export"));
        File.WriteAllText(Path.Combine(familyDirectory, "broken-export", "unet.onnx"), "placeholder");

        Directory.CreateDirectory(Path.Combine(familyDirectory, "sd-turbo-onnx"));
        foreach (var file in new[] { "unet.onnx", "text_encoder.onnx", "vae_decoder.onnx", "vocab.json", "merges.txt" })
            File.WriteAllText(Path.Combine(familyDirectory, "sd-turbo-onnx", file), "placeholder");

        Assert.Equal(Path.Combine(familyDirectory, "sd-turbo-onnx"), ScanForValidExport());
    }

    [Fact]
    public void EmptyStore_ScanFindsNothing()
    {
        Assert.Null(ScanForValidExport());
    }

    /** Mirrors the operator's empty-EnginePath fallback (StreamDiffusion.FindManagedModelFolder). */
    private static string? ScanForValidExport()
    {
        var familyDirectory = AiModelSettings.FamilyDirectory(AiModelSettings.StreamDiffusionFamilyFolder);
        if (!Directory.Exists(familyDirectory))
            return null;

        foreach (var directory in Directory.GetDirectories(familyDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (StableDiffusionPipeline.ValidateModelDirectory(directory).Length == 0)
                return directory;
        }

        return null;
    }
}
