# Depth Anything Operator Package for TiXL

Real-time monocular depth estimation for TiXL with **Depth Anything 3** (preferred) and Depth Anything V2, plus GPU operators that turn the depth into normals, positions and parallax.

## Operators

| Operator | Purpose |
|----------|---------|
| `DepthAnything` | Runs the model on a worker thread; outputs depth, visualized depth, confidence, sky, field of view, timing and the estimated camera |
| `DepthFilter` | Edge-aware bilateral smoothing of depth (GPU) |
| `DepthToNormal` | Normal map from depth with a Sobel gradient (GPU) |
| `DepthToPosition` | Camera-space position texture from depth and field of view (GPU) |
| `DepthToPoints` | Camera-space point buffer from depth and color, for live splats with `DrawPoints` (GPU) |
| `DepthParallax` | Shifts an image by its depth for 2.5D camera moves (GPU) |
| `DepthThreshold` | Binary depth mask (still reads depth back on the CPU) |

## Models

Put the ONNX files in `Assets/`, or run `Scripts/download_models.py` (`Scripts/download_models.ps1` on Windows). Pass file names to download only those, e.g. `python download_models.py da3-small.onnx`.

| Selection | Files tried in order | License |
|-----------|----------------------|---------|
| V3 Small | `da3-small-fp16.onnx`, `da3-small.onnx`, `da3-small-int8.onnx` | Apache-2.0 |
| V3 Base | `da3-base-fp16.onnx`, `da3-base.onnx` | Apache-2.0 |
| V3 Mono Large | `da3-mono-large-fp16.onnx`, `da3-mono-large-int8.onnx` | Apache-2.0 |
| V3 Metric Large | `da3-metric-large-fp16.onnx` | Apache-2.0 |
| V2 Small/Base/Large | `depth-anything-v2-{small,base,large}-fp16.onnx` (also `-small-fp32`, `-small-int8`) | Apache-2.0 (Small), CC-BY-NC (Base, Large) |

A V3 selection without an installed V3 file falls back to the V2 model of similar size.

The V3 files are community ONNX exports of the official weights. The operator reads the input and output layout from the model and accepts float32 NCHW input (ImageNet normalization is done on the GPU) as well as uint8 NHWC input from exports that normalize inside the graph, such as `da3-small.onnx`. Other exports with these layouts work too: the depth output may be called `depth` or `predicted_depth`, and `depth_conf`, `sky`, `intrinsics` and `extrinsics` outputs are used when present. The V3 Base export includes extrinsics, so `DepthAnything.CamReference` can publish the estimated camera; the Small export is depth-only. The DA3 Large and Giant any-view checkpoints are CC-BY-NC 4.0 and are not offered by the download scripts.

Fixed-size exports (for example 504x504 or 504x378) dictate their input size; `InputResolution` only affects models with a dynamic input.

## Latency

- The bundled `da3-small.onnx` is an fp32 export. Convert it once with `Scripts/convert_to_fp16.py` (or use the fp16 file already in `Assets/`): the operator tries `da3-small-fp16.onnx` first. fp16 halves the weights and uses the GPU's fp16 tensor cores. In the same run on a thermally throttled RTX 3080 Ti Laptop, model-only latency for a 518x518 frame fell from 163 ms (fp32) to 56 ms (fp16), with depth correlation 0.999997 and a mean normalized error of 0.0007 against fp32; at the GPU's normal clock the fp16 model takes 28 ms.
- CUDA is the fastest provider for the uint8 NHWC DA3 Small export. TensorRT's parser rejects the graph's uint8 `Transpose` input and assigns that node to CUDA, so plain CUDA is the better choice for this file. TensorRT is useful for float-NCHW exports such as DA3 Base; the operator still lets CUDA run the nodes TensorRT does not claim.
- Resampling to the model size, ImageNet normalization and the planar layout are one compute pass on the GPU. Only the model-sized input is read back, whatever the resolution of the source, and any texture format is accepted.
- One frame is in flight at a time and a new one is captured as soon as the last result arrived, so results never queue behind stale input. `LatencyMs` reports capture to result, `InferenceMs` the model alone.
- The session is created from the file path with the provider chain DirectML, CUDA, TensorRT or CoreML, then CPU. DirectML runs sequentially without memory-pattern planning, as that provider requires. TensorRT builds a cached engine under `%LOCALAPPDATA%\TiXL\trt_cache\<model>` — the first load of a model takes a minute or two while the engine builds, after which it starts instantly.
- Measured on an RTX 3080 Ti Laptop, V3 Small at 518x518, single frame: **TensorRT 9 ms, CUDA 24 ms, DirectML 47 ms, CPU ~970 ms**. TensorRT wins; CUDA is the good compromise when you want no engine-build wait. All four were verified end to end, and a provider that is unavailable falls through to the next one in the chain with a log line naming the reason.
- Windows can only load **one** ONNX Runtime native build per process: the GPU build (CUDA/TensorRT) or the DirectML build. Choosing DirectML and later switching to CUDA in the same session needs a restart — the operator logs a warning when that happens. CUDA and TensorRT switch freely within a session.
- `Smoothing` blends consecutive results and settles the depth range. DA3 predicts depth up to an unknown scale per frame, so this is what keeps a live visualization from pulsing.
- int8 files are small and meant for the CPU. Use fp16 or fp32 files with CUDA, DirectML or TensorRT. TiXL discovers the TensorRT 10 runtime in the standard NVIDIA install, `TENSORRT_ROOT`, or a `TensorRTRuntime` folder next to the editor and preloads it; no `PATH` change is needed.

## Depth Semantics

V3 models predict depth (far is larger), V2 models predict disparity (near is larger). `NormalizedDepthTexture` is near bright for both. `DepthToPosition`, `DepthToPoints` and `DepthParallax` have a `DepthIsDisparity` switch for V2. The Metric model returns meters when `FieldOfView` matches the source camera.

## Live Depth Splats

`DepthToPoints` turns the depth map into a point buffer and lets TiXL's existing `DrawPoints` render it as camera-facing splats:

```
DepthAnything.DepthTexture      → DepthToPoints.DepthTexture
DepthAnything.OutputTexture     → DepthToPoints.ColorTexture
DepthAnything.VerticalFov       → DepthToPoints.FieldOfView
DepthAnything.ConfidenceTexture → DepthToPoints.ConfidenceTexture   (optional)
DepthToPoints.Points            → DrawPoints.GPoints
```

On `DrawPoints` enable `UsePointsScale`, set `PointSize` to about 5, and turn `EnableZWrite` off. Each point is billboarded in camera space; its `Scale` is the world-space size that covers `SplatSizePixels` screen pixels at its depth, so near and far points look evenly dense. The result is a live 2.5D splat of the input that can be orbited, transformed and composited like any other 3D object.

> **Run splats on the Vulkan backend.** The D3D11 backend does not draw point buffers written by a compute shader, so on D3D11 the chain is silently black although `PointCount` reports tens of thousands of points. Start the editor with `TIXL_BACKEND=vulkan` (or pick Vulkan in the settings) for this chain; D3D11 is fine for every other depth operator. Vulkan needs a Slang SPIR-V build with `spirv-opt` reachable from `slangc`; where the installed release has no `spirv-opt`, put it in Slang's `bin` folder and set `TIXL_SLANG_IN_PROCESS=0` so the compiler can find it.

- `PixelStride` and `MaxPoints` bound the cost. The default 2 and 262,144 points turn a 518x518 model output into ~67k points; the stride grows automatically when the cap would be exceeded.
- Invalid depths and points below `ConfidenceThreshold` are written as zero-scale transparent points, so no compaction pass or CPU readback is needed.
- `VerticalFov` is 0 when the model doesn't predict intrinsics; `DepthToPoints` falls back to 50° so the view stays usable.
- `TemporalSmoothing` blends each point with the previous frame's buffer. 0 is raw and most responsive; 0.7-0.9 calms a still webcam, lower it when the camera or subject moves. It keeps a second point buffer, so the point memory doubles.
- With the V3 Base confidence map, `ConfidenceThreshold` around 1.0 drops the flying-pixel streaks that cause long spikes; the useful value is model-dependent.
- For spatial denoising, put `DepthFilter` between the depth output and `DepthToPoints`; `DepthAnything.Smoothing` blends the depth and its range before unprojection.
- When the source aspect differs from the model's square input, scale the point object's X axis to the source aspect (or uncheck `MatchInputResolution` and feed a depth map with the source aspect).
- For true Gaussian splatting (learned scale, rotation, opacity and SH), DA3's Gaussian head lives only in the Giant/nested checkpoints, which are CC-BY-NC 4.0; `DepthToPoints` is an Apache-2.0-friendly approximation built from depth and color.

See `Examples/DepthSplat.example.md` for the full node settings and two captures (front and rotated 35°).

## Camera Reference

The V3 Base export predicts camera extrinsics and intrinsics. `DepthAnything.CamReference` publishes them as an `ICamera`: the position comes from the extrinsics, +Z is forward, the image-up axis comes from the camera, and the vertical FOV comes from the intrinsics. Connect it to camera-reference inputs such as `ApplyCamTransform.CamReference` or `SortPoints.CameraReference`.

The pose follows DA3's image frame. The depth splat is built in a y-up, +Z-forward frame, so a render camera driven directly by `CamReference` needs a Y flip (or use a `Camera` operator for the view). Models without an extrinsics output fall back to the reference pose at the origin; the Small export is depth-only, the Base export has the pose.

## Examples

The Examples package ships three example compositions (symbol browser → Examples → Lib/io/video/depthanything):

- `DepthAnythingExample` — LoadImage → DepthAnything, showing the colorized normalized depth.
- `DepthAnythingExampleNormalSurface` — the depth feeds DepthToNormal and DepthToPosition, with both textures as outputs.
- `DepthAnythingExampleParallaxMove` — LoadImage and depth feed DepthParallax for a 2.5D camera move; animate `Offset` for the wobble.

They default to the Depth Anything 3 Small model, so they run as soon as `da3-small.onnx` is installed.

## Credits

- [Depth Anything 3](https://github.com/ByteDance-Seed/Depth-Anything-3) by ByteDance Seed
- [Depth Anything V2](https://github.com/DepthAnything/Depth-Anything-V2) by Lihe Yang et al.
- ONNX Runtime by Microsoft
