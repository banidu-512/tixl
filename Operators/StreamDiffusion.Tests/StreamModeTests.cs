using System.Diagnostics;
using System.Reflection;
using t3.streamdiffusion.Onnx;
using Xunit;

namespace StreamDiffusion.Tests;

/// <summary>
/// Streaming-mode tests. Model-dependent tests no-op (pass silently) on
/// machines without the SD-Turbo models in exported_sd15/, following the
/// CI-safe pattern of SdTurboIntegrationTests.
/// </summary>
public class StreamModeTests : IDisposable
{
    private static readonly string ModelDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "exported_sd15"));

    private readonly StableDiffusionPipeline _pipeline = new();

    public void Dispose() => _pipeline.Dispose();

    private bool ModelsAvailable =>
        File.Exists(Path.Combine(ModelDir, "unet", "model.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "text_encoder", "model.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "vae_decoder", "model.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "vae_encoder", "model.onnx"));

    private static byte[] MakeGrayInput(int size)
    {
        var input = new byte[size * size * 4];
        for (var i = 0; i < input.Length; i += 4)
        {
            input[i] = 128;
            input[i + 1] = 128;
            input[i + 2] = 128;
            input[i + 3] = 255;
        }

        return input;
    }

    [Fact]
    public void StreamStep_ProducesValidRgba_AndSecondCallIsWarm()
    {
        if (!ModelsAvailable || !_pipeline.Initialize(ModelDir, 0, ModelType.SDTurbo))
            return;

        var input = MakeGrayInput(64);

        var first = _pipeline.StreamStep("robotic steampunk", input, 64, 64, 512, 512, 0.6f, 0, 42, out var error1);
        Assert.Null(error1);
        Assert.NotNull(first);
        Assert.Equal(512 * 512 * 4, first!.Length);

        var sw = Stopwatch.StartNew();
        var second = _pipeline.StreamStep("robotic steampunk", input, 64, 64, 512, 512, 0.6f, 0, 42, out var error2);
        sw.Stop();

        Assert.Null(error2);
        Assert.NotNull(second);
        Assert.Equal(512 * 512 * 4, second!.Length);

        // Non-degenerate output
        var distinctValues = new HashSet<byte>(second.Where((_, i) => (i & 3) != 3).Select(b => (byte)(b >> 4)));
        Assert.True(distinctValues.Count > 1, "stream output should contain pixel variation");

        // Warm second frame should complete - generous budget because a GPU
        // shared with other compute (local LLM servers etc.) can stall a frame
        // for seconds; this is a works-at-all smoke check, not a perf gate.
        Assert.True(sw.ElapsedMilliseconds < 30000, $"warm stream step took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ResetStream_AfterPromptChange_ChangesOutput()
    {
        if (!ModelsAvailable || !_pipeline.Initialize(ModelDir, 0, ModelType.SDTurbo))
            return;

        var input = MakeGrayInput(64);

        var a = _pipeline.StreamStep("a forest", input, 64, 64, 512, 512, 0.6f, 0, 42, out _);
        var b = _pipeline.StreamStep("a forest", input, 64, 64, 512, 512, 0.6f, 0, 42, out _);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // Prompt change resets the stream state
        var c = _pipeline.StreamStep("a city at night", input, 64, 64, 512, 512, 0.6f, 0, 42, out _);
        Assert.NotNull(c);
        Assert.NotEqual(b, c);

        // Explicit reset also works and produces valid output
        _pipeline.ResetStream();
        var d = _pipeline.StreamStep("a forest", input, 64, 64, 512, 512, 0.6f, 0, 42, out var error);
        Assert.Null(error);
        Assert.NotNull(d);
    }

    [Fact]
    public void TryPrepareDenoise_CachesPromptEmbeddings_ByReference()
    {
        if (!ModelsAvailable || !_pipeline.Initialize(ModelDir, 0, ModelType.SDTurbo))
            return;

        var method = typeof(StableDiffusionPipeline).GetMethod("TryPrepareDenoise",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        object?[] Args(float guidance) =>
            new object?[] { "cache test prompt", null, 1, guidance, 0.8f, 42, null!, null!, null!, null!, null! };

        var args1 = Args(1.0f);
        Assert.True((bool)method!.Invoke(_pipeline, args1)!);
        var first = (float[])args1[6]!;

        var args2 = Args(1.0f);
        Assert.True((bool)method.Invoke(_pipeline, args2)!);
        var second = (float[])args2[6]!;

        // Same prompt string: cached embedding must be the same array instance
        Assert.Same(first, second);

        // Different prompt: new embedding
        var args3 = Args(1.0f);
        args3[0] = "different prompt";
        Assert.True((bool)method.Invoke(_pipeline, args3)!);
        var third = (float[])args3[6]!;
        Assert.NotSame(first, third);

        // Re-initialize invalidates the cache
        Assert.True(_pipeline.Initialize(ModelDir, 0, ModelType.SDTurbo));
        var args4 = Args(1.0f);
        Assert.True((bool)method.Invoke(_pipeline, args4)!);
        var fourth = (float[])args4[6]!;
        Assert.NotSame(first, fourth);
    }
}
