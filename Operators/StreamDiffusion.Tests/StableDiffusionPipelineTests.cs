using System.Reflection;
using t3.streamdiffusion.Onnx;
using Xunit;

namespace StreamDiffusion.Tests;

public class StableDiffusionPipelineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sd-pipeline-tests-" + Guid.NewGuid().ToString("N"));

    public StableDiffusionPipelineTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void Touch(params string[] files)
    {
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "placeholder");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateModelDirectory_EmptyPath_ReportsMissing(string? path)
    {
        var error = StableDiffusionPipeline.ValidateModelDirectory(path);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void ValidateModelDirectory_NonexistentDirectory_ReportsNotFound()
    {
        var error = StableDiffusionPipeline.ValidateModelDirectory(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Contains("not found", error);
    }

    [Fact]
    public void ValidateModelDirectory_EmptyDirectory_ListsAllMissingFiles()
    {
        var error = StableDiffusionPipeline.ValidateModelDirectory(_tempDir);

        Assert.Contains("unet.onnx", error);
        Assert.Contains("text_encoder.onnx", error);
        Assert.Contains("vae_decoder.onnx", error);
        Assert.Contains("tokenizer", error);
    }

    [Fact]
    public void ValidateModelDirectory_WithModelsButNoTokenizer_ReportsTokenizer()
    {
        Touch("unet.onnx", "text_encoder.onnx", "vae_decoder.onnx");

        var error = StableDiffusionPipeline.ValidateModelDirectory(_tempDir);
        Assert.Contains("tokenizer", error);
        Assert.DoesNotContain("unet.onnx", error);
        Assert.DoesNotContain("text_encoder.onnx", error);
        Assert.DoesNotContain("vae_decoder.onnx", error);
    }

    [Fact]
    public void ValidateModelDirectory_CompleteDirectory_Passes()
    {
        Touch("unet.onnx", "text_encoder.onnx", "vae_decoder.onnx", "vocab.json", "merges.txt");

        Assert.Equal(string.Empty, StableDiffusionPipeline.ValidateModelDirectory(_tempDir));
    }

    [Fact]
    public void ValidateModelDirectory_TokenizerJsonSuffices()
    {
        Touch("unet.onnx", "text_encoder.onnx", "vae_decoder.onnx", "tokenizer.json");

        Assert.Equal(string.Empty, StableDiffusionPipeline.ValidateModelDirectory(_tempDir));
    }

    [Fact]
    public void ValidateModelDirectory_OptionalVaeEncoderNotRequired()
    {
        Touch("unet.onnx", "text_encoder.onnx", "vae_decoder.onnx", "tokenizer.json");

        // Complete without vae_encoder.onnx; its presence changes nothing
        Assert.Equal(string.Empty, StableDiffusionPipeline.ValidateModelDirectory(_tempDir));
        Touch("vae_encoder.onnx");
        Assert.Equal(string.Empty, StableDiffusionPipeline.ValidateModelDirectory(_tempDir));
    }

    [Fact]
    public void Initialize_IncompleteDirectory_FailsGracefully()
    {
        Touch("unet.onnx"); // everything else missing

        using var pipeline = new StableDiffusionPipeline();
        var result = pipeline.Initialize(_tempDir, 0);

        Assert.False(result);
        Assert.False(pipeline.IsInitialized);
        Assert.False(pipeline.SupportsImg2Img);
    }

    [Fact]
    public void Txt2Img_WithoutInitialization_ReturnsError()
    {
        using var pipeline = new StableDiffusionPipeline();

        var result = pipeline.Txt2Img("test", null, 512, 512, 1, 1.0f, 0, out var error);

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("not initialized", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Img2Img_WithoutInitialization_ReturnsError()
    {
        using var pipeline = new StableDiffusionPipeline();

        var result = pipeline.Img2Img("test", null, new byte[64 * 64 * 4], 64, 64, 512, 512, 1, 1.0f, 0.8f, 0, 0, 0.0f, out var error);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void StreamScheduler_AddNoiseAt_MatchesExpectedFormula()
    {
        var scheduler = new StreamScheduler(100);
        scheduler.SetTimesteps(10);

        var latents = new float[] { 2.0f, -3.0f, 0.5f, 1.0f };
        var noise = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var timestep = scheduler.Timesteps[0];

        scheduler.AddNoiseAt(latents, noise, timestep);

        var sqrtAlpha = MathF.Sqrt(scheduler.AlphaCumprodAt(timestep));
        var sqrtOneMinus = MathF.Sqrt(1f - scheduler.AlphaCumprodAt(timestep));

        Assert.Equal(sqrtAlpha * 2.0f + sqrtOneMinus * 0.1f, latents[0], 5);
        Assert.Equal(sqrtAlpha * (-3.0f) + sqrtOneMinus * (-0.2f), latents[1], 5);
        Assert.Equal(sqrtAlpha * 0.5f + sqrtOneMinus * 0.3f, latents[2], 5);
        Assert.Equal(sqrtAlpha * 1.0f + sqrtOneMinus * (-0.4f), latents[3], 5);
    }

    [Fact]
    public void StreamScheduler_AddNoiseAt_InvalidTimestep_Throws()
    {
        var scheduler = new StreamScheduler(100);
        scheduler.SetTimesteps(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.AddNoiseAt(new float[4], new float[4], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.AddNoiseAt(new float[4], new float[4], 1000));
    }

    [Fact]
    public void StreamScheduler_AddNoiseAt_LengthMismatch_Throws()
    {
        var scheduler = new StreamScheduler(100);
        scheduler.SetTimesteps(10);

        Assert.Throws<ArgumentException>(() => scheduler.AddNoiseAt(new float[4], new float[2], scheduler.Timesteps[0]));
    }

    [Fact]
    public void StreamScheduler_GetImg2ImgStartStepIndex_ClampsCorrectly()
    {
        var scheduler = new StreamScheduler(100);

        Assert.Equal(9, scheduler.GetImg2ImgStartStepIndex(0f, 10));
        Assert.Equal(5, scheduler.GetImg2ImgStartStepIndex(0.5f, 10));
        Assert.Equal(0, scheduler.GetImg2ImgStartStepIndex(1f, 10));
        Assert.Equal(9, scheduler.GetImg2ImgStartStepIndex(-0.5f, 10));
        Assert.Equal(0, scheduler.GetImg2ImgStartStepIndex(1.5f, 10));
    }

    [Fact]
    public void CreateGaussianLatents_HasCorrectLengthAndRange()
    {
        var method = typeof(StableDiffusionPipeline).GetMethod("CreateGaussianLatents", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var latents = (float[])method.Invoke(null, new object[] { 1000, 42 })!;
        Assert.Equal(1000, latents.Length);

        var sum = 0.0;
        var sumSq = 0.0;
        foreach (var v in latents)
        {
            sum += v;
            sumSq += v * v;
        }

        var mean = sum / latents.Length;
        var variance = sumSq / latents.Length - mean * mean;

        Assert.InRange(mean, -0.2, 0.2);
        Assert.InRange(variance, 0.5, 2.5);
    }

    [Fact]
    public void CreateGaussianLatents_SameSeed_ProducesSameValues()
    {
        var method = typeof(StableDiffusionPipeline).GetMethod("CreateGaussianLatents", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var a = (float[])method.Invoke(null, new object[] { 100, 123 })!;
        var b = (float[])method.Invoke(null, new object[] { 100, 123 })!;

        Assert.Equal(a, b);
    }

    [Fact]
    public void ResizeRgbaStretch_OneToOne_PreservesPixels()
    {
        var method = typeof(StableDiffusionPipeline).GetMethod("ResizeRgbaStretch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = new byte[4 * 4 * 4];
        for (var i = 0; i < input.Length; i++)
            input[i] = (byte)(i % 256);

        var result = (byte[])method.Invoke(null, new object[] { input, 4, 4, 4, 4 })!;
        Assert.Equal(input, result);
    }

    [Fact]
    public void ResizeRgbaCenterCrop_OneToOne_PreservesPixels()
    {
        var method = typeof(StableDiffusionPipeline).GetMethod("ResizeRgbaCenterCrop", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = new byte[4 * 4 * 4];
        for (var i = 0; i < input.Length; i++)
            input[i] = (byte)(i % 256);

        var result = (byte[])method.Invoke(null, new object[] { input, 4, 4, 4, 4 })!;
        Assert.Equal(input, result);
    }

    [Fact]
    public void ResizeRgbaPad_OneToOne_PreservesPixels()
    {
        var method = typeof(StableDiffusionPipeline).GetMethod("ResizeRgbaPad", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = new byte[4 * 4 * 4];
        for (var i = 0; i < input.Length; i++)
            input[i] = (byte)(i % 256);

        var result = (byte[])method.Invoke(null, new object[] { input, 4, 4, 4, 4 })!;
        Assert.Equal(input, result);
    }
}
