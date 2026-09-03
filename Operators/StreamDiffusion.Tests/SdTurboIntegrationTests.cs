using t3.streamdiffusion.Onnx;
using Xunit;

namespace StreamDiffusion.Tests;

/// <summary>
/// End-to-end tests against the real SD-Turbo ONNX models in exported_sd15/.
/// These no-op (pass silently) on machines without the models downloaded, so
/// they stay safe for CI. See .meta/StreamDiffusion.md for how to obtain them.
/// </summary>
public class SdTurboIntegrationTests : IDisposable
{
    private static readonly string ModelDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "exported_sd15"));

    /// <summary>The realtime model dir: fp16 + TAESD. Skipped when not exported on this machine.</summary>
    private static readonly string TinyVaeModelDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "exported_sd15-fp16"));

    private static bool TinyVaeAvailable =>
        File.Exists(Path.Combine(TinyVaeModelDir, "taesd_decoder.onnx")) &&
        File.Exists(Path.Combine(TinyVaeModelDir, "text_encoder", "model.onnx"));

    private readonly StableDiffusionPipeline _pipeline = new();

    public void Dispose() => _pipeline.Dispose();

    private bool ModelsAvailable =>
        File.Exists(Path.Combine(ModelDir, "unet", "model.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "text_encoder", "model.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "vae_decoder", "model.onnx"));

    [Fact]
    public void ModelDirectory_IsValid()
    {
        if (!Directory.Exists(ModelDir))
            return; // models not downloaded on this machine

        Assert.Equal(string.Empty, StableDiffusionPipeline.ValidateModelDirectory(ModelDir));
    }

    [Fact]
    public void Tokenizer_EncodesRealPrompts()
    {
        var vocabPath = Path.Combine(ModelDir, "tokenizer", "vocab.json");
        if (!File.Exists(vocabPath))
            return;

        var tokenizer = ClipTokenizer.FromModelDirectory(ModelDir);
        Assert.NotNull(tokenizer);

        var ids = tokenizer.Encode("a beautiful landscape");
        Assert.Equal(ClipTokenizer.MaxLength, ids.Length);
        Assert.Equal(ClipTokenizer.BosTokenId, ids[0]);
        Assert.Equal(ClipTokenizer.EosTokenId, ids[76]);

        // "a beautiful landscape" must map to real vocab entries, not all padding
        var contentIds = ids[1..76];
        Assert.Contains(contentIds, id => id != ClipTokenizer.PadTokenId);
    }

    [Fact]
    public void Pipeline_Initializes_And_GeneratesImage()
    {
        if (!ModelsAvailable)
            return; // models not downloaded on this machine

        Assert.True(_pipeline.Initialize(ModelDir, 0), "pipeline should initialize with valid models");
        Assert.True(_pipeline.IsInitialized);
        Assert.True(_pipeline.SupportsImg2Img, "vae_encoder.onnx is present, img2img must be supported");

        var result = _pipeline.Txt2Img(
            prompt: "a photograph of a cat",
            negativePrompt: null,
            width: 512,
            height: 512,
            steps: 1,
            guidance: 1.0f, // SD-Turbo fast path: no CFG, single UNet pass
            seed: 42,
            out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(512 * 512 * 4, result!.Length);

        // Deterministic: same seed produces identical pixels
        var rerun = _pipeline.Txt2Img("a photograph of a cat", null, 512, 512, 1, 1.0f, 42, out _);
        Assert.Equal(result, rerun);

        // The image is not uniformly black or white - a real generation
        var distinctValues = new HashSet<byte>(result.Where((_, i) => (i & 3) != 3).Select(b => (byte)(b >> 4)));
        Assert.True(distinctValues.Count > 1, "generated image should contain pixel variation");
    }

    [Fact]
    public void Pipeline_Img2Img_TransformsInput()
    {
        if (!ModelsAvailable || !_pipeline.Initialize(ModelDir, 0))
            return;

        // 64x64 gray input, fully denoising transform
        var input = new byte[64 * 64 * 4];
        for (var i = 0; i < input.Length; i += 4)
        {
            input[i] = 128;
            input[i + 1] = 128;
            input[i + 2] = 128;
            input[i + 3] = 255;
        }

        var result = _pipeline.Img2Img(
            prompt: "an oil painting",
            negativePrompt: null,
            rgbaInput: input,
            inputWidth: 64,
            inputHeight: 64,
            width: 512,
            height: 512,
            steps: 1,
            guidance: 1.0f,
            strength: 1.0f,
            seed: 7,
            resizeMode: 0,
            preserveDetails: 0.0f,
            out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(512 * 512 * 4, result!.Length);
    }

    [Fact]
    public void TinyVae_UsedWhenAvailable_AndGeneratesRealtimeFrames()
    {
        if (!TinyVaeAvailable)
            return; // TAESD not exported on this machine (tools/export-taesd-onnx.py)

        Assert.True(_pipeline.Initialize(TinyVaeModelDir, 0, ModelType.SDTurbo),
            "pipeline should initialize with the fp16 + TAESD model dir");
        Assert.True(_pipeline.UsingTinyVae, "TAESD files present but pipeline did not pick them up");
        Assert.False(_pipeline.UseGpuLatentFlow,
            "GPU-resident latent flow must be opt-in (TIXL_SD_GPU_LATENTS=1), not default");

        var input = new byte[64 * 64 * 4];
        for (var i = 0; i < input.Length; i += 4)
        {
            input[i] = 200;
            input[i + 1] = 100;
            input[i + 2] = 50;
            input[i + 3] = 255;
        }

        var result = _pipeline.Img2Img(
            prompt: "an oil painting", negativePrompt: null,
            rgbaInput: input, inputWidth: 64, inputHeight: 64,
            width: 512, height: 512, steps: 1, guidance: 1.0f, strength: 0.8f,
            seed: 7, resizeMode: 0, preserveDetails: 0.0f, out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(512 * 512 * 4, result!.Length);

        // Output must be actual image content, not black/blank: mean above a floor
        var mean = 0f;
        for (var i = 0; i < result.Length; i += 4)
            mean += result[i] + result[i + 1] + result[i + 2];
        mean /= 512 * 512 * 3;
        Assert.True(mean > 5f, $"TAESD decode produced near-black output (mean {mean:F1})");
    }
}
