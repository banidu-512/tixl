using t3.streamdiffusion.Onnx;

// Headless generation test: mirrors what the StreamDiffusion operator does,
// so pipeline-level CUDA failures can be told apart from operator UI/gating issues.

Console.WriteLine("== StreamDiffusion headless harness ==");

var modelDir = args.Length > 0 ? args[0] : @"C:/Users/Artonurban/AITEST/tixl-repo/exported_sd15-fp16";
var outSize = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 512;
var pipeline = new StableDiffusionPipeline();

Console.WriteLine($"Initializing {modelDir} (SDTurbo, CUDA, device 0)...");
var ok = pipeline.Initialize(modelDir, 0, ModelType.SDTurbo, ExecutionProvider.Cuda);
Console.WriteLine($"Initialize: {ok}  img2img: {pipeline.SupportsImg2Img}");
if (!ok)
{
    Console.WriteLine("INIT FAILED - aborting");
    return 1;
}

// 1) Txt2Img, 1 step (SD Turbo)
var sw = System.Diagnostics.Stopwatch.StartNew();
var img = pipeline.Txt2Img("a beautiful landscape", null, outSize, outSize, 1, 1.0f, -1, out var err);
sw.Stop();
Console.WriteLine($"Txt2Img: {(img == null ? $"FAILED: {err}" : $"{img.Length} bytes")} in {sw.ElapsedMilliseconds} ms");

// 2) Img2Img with a synthetic gradient RGBA buffer (what the operator feeds after readback)
var w = outSize;
var h = outSize;
var rgba = new byte[w * h * 4];
for (var y = 0; y < h; y++)
{
    for (var x = 0; x < w; x++)
    {
        var i = (y * w + x) * 4;
        rgba[i] = (byte)(x * 255 / w);
        rgba[i + 1] = (byte)(y * 255 / h);
        rgba[i + 2] = 128;
        rgba[i + 3] = 255;
    }
}

sw.Restart();
var img2 = pipeline.Img2Img("a beautiful landscape", null, rgba, w, h, w, h, 1, 1.0f, 0.8f, -1,
                            resizeMode: 0, preserveDetails: 0f, isBgra: false, error: out var err2);
sw.Stop();
Console.WriteLine($"Img2Img: {(img2 == null ? $"FAILED: {err2}" : $"{img2.Length} bytes")} in {sw.ElapsedMilliseconds} ms");

// 3) Steady-state loop: the number that decides realtime usability
const int frames = 20;
var latencies = new long[frames];
for (var i = 0; i < frames; i++)
{
    sw.Restart();
    var imgN = pipeline.Img2Img("a beautiful landscape", null, rgba, w, h, w, h, 1, 1.0f, 0.8f, -1,
                                resizeMode: 0, preserveDetails: 0f, isBgra: false, error: out var errN);
    latencies[i] = sw.ElapsedMilliseconds;
    if (imgN == null)
    {
        Console.WriteLine($"Img2Img frame {i}: FAILED: {errN}");
        return 1;
    }
}
Console.WriteLine($"Steady-state over {frames} img2img frames @{outSize}²: " +
                  $"avg {latencies.Average():F0} ms ({1000.0 / latencies.Average():F1} fps), " +
                  $"min {latencies.Min()} ms, max {latencies.Max()} ms");
Console.WriteLine($"Stage breakdown of last frame: encode {pipeline.LastEncodeMs:F1} ms, " +
                  $"denoise {pipeline.LastDenoiseMs:F1} ms, decode {pipeline.LastDecodeMs:F1} ms");
Console.WriteLine("Per-frame: " + string.Join(", ", latencies));

pipeline.Dispose();
Console.WriteLine("== done ==");
return 0;
