using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lib.io.video.depthanything.Utils;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;


if (args.Length > 2 && args[2] == "synthetic")
{
    var rnd = new Random(3);
    var truth = new Sim3(1.7f, Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.6f, -0.3f, 0.2f)), new Vector3(0.4f, -1.2f, 2.0f));
    var src = new List<Vector3>(); var dst = new List<Vector3>(); var w = new List<float>();
    for (int i = 0; i < 4000; i++)
    {
        var p = new Vector3((float)rnd.NextDouble() * 4 - 2, (float)rnd.NextDouble() * 4 - 2, (float)rnd.NextDouble() * 4 + 1);
        var q = truth.Apply(p) + new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * 0.02f;
        if (i % 5 == 0) q += new Vector3(2, -1, 3); // 20% gross outliers
        src.Add(p); dst.Add(q); w.Add(1f);
    }
    Sim3Fit.Fit(src, dst, w, out var fit);
    var dq = Quaternion.Concatenate(Quaternion.Inverse(truth.Rotation), fit.Rotation);
    Console.WriteLine($"synthetic: scale {fit.Scale:F4} (1.7), rot err {2 * Math.Acos(Math.Clamp(Math.Abs(dq.W), 0, 1)) * 180 / Math.PI:F3} deg, t err {Vector3.Distance(fit.Translation, truth.Translation):F4}");
    Sim3Fit.FitWithRotation(src, dst, w, truth.Rotation, out var fixedFit);
    Console.WriteLine($"fixed-rotation: scale {fixedFit.Scale:F4}, t err {Vector3.Distance(fixedFit.Translation, truth.Translation):F4}");
    return;
}

const int Views = 6, Size = 392;
int Stride = args.Length > 2 && int.TryParse(args[2], out var st) ? st : 3;
int Overlap = Views - Stride;
var raw = File.ReadAllBytes(args[1]);
var frameLen = 3 * Size * Size;
var frames = new float[raw.Length / 4];
Buffer.BlockCopy(raw, 0, frames, 0, raw.Length);
var frameCount = frames.Length / frameLen;
using var session = new InferenceSession(args[0]);
var chain = new DepthChunkChain();
var placed = new List<PlacedFrame>();
for (int start = 0, c = 0; start + Views <= frameCount; start += Stride, c++)
{
    var input = new float[Views * frameLen];
    Array.Copy(frames, start * frameLen, input, 0, input.Length);
    using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor("images", new DenseTensor<float>(input, new[] { 1, Views, 3, Size, Size })) });
    float[] Get(string n) => outputs.First(o => o.Name == n).AsTensor<float>().ToArray();
    var chunk = new DepthChunk(Views, Size, Get("depth"), Get("depth_conf"), Get("extrinsics"), Get("intrinsics"));
    var raws = new List<string>();
    for (int v = 0; v < Views; v++) { chunk.CameraToWorld(v, out var rr, out _); var ff = Vector3.Transform(new Vector3(0,0,1), rr); raws.Add($"{Math.Atan2(ff.X, ff.Z) * 180 / Math.PI:F1}"); }
    Console.WriteLine($"chunk {c} raw yaw per view: {string.Join(' ', raws)}");
    var result = chain.Add(chunk, Stride, true);
    if (result == null) { Console.WriteLine($"chunk {c}: overlap fit failed"); continue; }
    var s = chain.LastChunkToWorld;
    var angle = 2 * Math.Acos(Math.Clamp(Math.Abs(s.Rotation.W), 0, 1)) * 180 / Math.PI;
    Console.WriteLine($"chunk {c} frames {start}-{start + Views - 1}: pairs {chain.LastPairCount}, overlap residual {chain.LastResidualBefore:F3} -> {chain.LastResidualAfter:F3}, point-fit rotation differs by {chain.LastPointRotationDegrees:F1} deg, " +
                      $"chunk->world scale {s.Scale:F3} rot {angle:F1} deg, +{result.Count} frames");
    foreach (var f in result) placed.Add(f);
}
Console.WriteLine("frame, yaw (deg, forward azimuth), pitch, world position, depth scale");
double? last = null;
for (int i = 0; i < placed.Count; i++)
{
    var f = placed[i];
    var fwd = Vector3.Transform(new Vector3(0, 0, 1), f.Rotation);
    var yaw = Math.Atan2(fwd.X, fwd.Z) * 180 / Math.PI;
    var pitch = Math.Asin(Math.Clamp(-fwd.Y, -1, 1)) * 180 / Math.PI;
    Console.WriteLine($"{i,2} chunk {f.Chunk} view {f.View}: yaw {yaw,6:F1} step {(last.HasValue ? yaw - last.Value : 0),5:F1} pitch {pitch,5:F1} pos ({f.Position.X:F3},{f.Position.Y:F3},{f.Position.Z:F3}) scale {f.DepthScale:F3}");
    last = yaw;
}
