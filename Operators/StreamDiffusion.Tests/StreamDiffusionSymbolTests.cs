using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace StreamDiffusion.Tests;

public class StreamDiffusionSymbolTests
{
    private static readonly string OperatorRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Operators", "StreamDiffusion"));

    [Fact]
    public void SymbolFile_ExistsAndIsValidJson()
    {
        var symbolPath = Path.Combine(OperatorRoot, "lib", "io", "ai", "StreamDiffusion.t3");
        Assert.True(File.Exists(symbolPath), $"Symbol file not found at {symbolPath}");

        var json = JObject.Parse(File.ReadAllText(symbolPath));
        Assert.Equal(3, json["FormatVersion"]?.Value<int>());

        var id = json["Id"]?.Value<string>();
        Assert.Equal("9A7B3C8D-4E2F-5A6B-7C8D-9E0F1A2B3C4D", id);

        var inputs = json["Inputs"] as JArray;
        Assert.NotNull(inputs);
        Assert.NotEmpty(inputs);

        var inputIds = inputs.Select(i => i["Id"]?.Value<string>()).ToList();
        Assert.Contains("A1B2C3D4-E5F6-7890-ABCD-EF1234567890", inputIds); // EnginePath
        Assert.Contains("C3D4E5F6-A7B8-9012-CDEF-123456789012", inputIds); // Prompt
        Assert.Contains("B4C5D6E7-F8A9-0123-7890-234567890123", inputIds); // TriggerGenerate
    }

    [Fact]
    public void SymbolUiFile_ExistsAndIsValidJson()
    {
        var uiPath = Path.Combine(OperatorRoot, "lib", "io", "ai", "StreamDiffusion.t3ui");
        Assert.True(File.Exists(uiPath), $"UI file not found at {uiPath}");

        var json = JObject.Parse(File.ReadAllText(uiPath));
        Assert.Equal(3, json["FormatVersion"]?.Value<int>());

        var id = json["Id"]?.Value<string>();
        Assert.Equal("9A7B3C8D-4E2F-5A6B-7C8D-9E0F1A2B3C4D", id);

        var description = json["Description"]?.Value<string>();
        Assert.NotNull(description);
        Assert.Contains("Stable Diffusion", description);
        Assert.Contains("ONNX", description);

        var inputUis = json["InputUis"] as JArray;
        Assert.NotNull(inputUis);
        Assert.NotEmpty(inputUis);

        var outputUis = json["OutputUis"] as JArray;
        Assert.NotNull(outputUis);
        Assert.NotEmpty(outputUis);
    }

    [Fact]
    public void MetaFile_ExistsAndContainsRequiredSections()
    {
        var metaPath = Path.Combine(OperatorRoot, ".meta", "StreamDiffusion.md");
        Assert.True(File.Exists(metaPath), $"Meta file not found at {metaPath}");

        var content = File.ReadAllText(metaPath);
        Assert.Contains("# StreamDiffusion Operator", content);
        Assert.Contains("## Overview", content);
        Assert.Contains("## Inputs", content);
        Assert.Contains("## Outputs", content);
        Assert.Contains("## Model Files Required", content);
        Assert.Contains("## Prerequisites", content);
        Assert.Contains("## Recommended Settings", content);
        Assert.Contains("## Troubleshooting", content);
        Assert.Contains("## Performance Notes", content);
        Assert.Contains("## Use Cases", content);
    }

    [Fact]
    public void PipelineSourceFiles_ExistInOnnxFolder()
    {
        var onnxDir = Path.Combine(OperatorRoot, "lib", "Onnx");
        Assert.True(Directory.Exists(onnxDir), $"lib/Onnx folder not found at {onnxDir}");

        Assert.True(File.Exists(Path.Combine(onnxDir, "ClipTokenizer.cs")), "ClipTokenizer.cs missing");
        Assert.True(File.Exists(Path.Combine(onnxDir, "StreamScheduler.cs")), "StreamScheduler.cs missing");
        Assert.True(File.Exists(Path.Combine(onnxDir, "OnnxModelSession.cs")), "OnnxModelSession.cs missing");
        Assert.True(File.Exists(Path.Combine(onnxDir, "StableDiffusionPipeline.cs")), "StableDiffusionPipeline.cs missing");
    }

    [Fact]
    public void LibrediffusionFiles_AreGone()
    {
        Assert.False(Directory.Exists(Path.Combine(OperatorRoot, "lib", "Native")),
            "lib/Native folder should no longer exist");
        Assert.False(Directory.Exists(Path.Combine(OperatorRoot, "dependencies")),
            "dependencies folder should no longer exist");
    }

    [Fact]
    public void Csproj_UsesGpuPackage()
    {
        var csprojPath = Path.Combine(OperatorRoot, "StreamDiffusion.csproj");
        Assert.True(File.Exists(csprojPath));

        var csproj = File.ReadAllText(csprojPath);

        Assert.Contains("Microsoft.ML.OnnxRuntime.Gpu", csproj);
        Assert.DoesNotContain("librediffusion", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssetsFolder_ExistsForOptionalModels()
    {
        var assetsPath = Path.Combine(OperatorRoot, "Assets");
        Assert.True(Directory.Exists(assetsPath), "Assets folder should exist for optional ONNX models");
    }
}
