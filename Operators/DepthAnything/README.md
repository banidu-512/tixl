# Depth Anything Operator Package for TiXL

Real-time monocular depth estimation for TiXL with **Depth Anything 3** (preferred) and Depth Anything V2, plus GPU operators that turn the depth into normals, positions and parallax.

## Operators

| Operator | Purpose |
|----------|---------|
| `DepthAnything` | Runs the model on a worker thread; outputs depth, visualized depth, confidence, sky, field of view and timing |
| `DepthFilter` | Edge-aware bilateral smoothing of depth (GPU) |
| `DepthToNormal` | Normal map from depth with a Sobel gradient (GPU) |
| `DepthToPosition` | Camera-space position texture from depth and field of view (GPU) |
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

The V3 files are community ONNX exports of the official weights. The operator reads the input and output layout from the model and accepts float32 NCHW input (ImageNet normalization is done on the GPU) as well as uint8 NHWC input from exports that normalize inside the graph, such as `da3-small.onnx`. Other exports with these layouts work too: the depth output may be called `depth` or `predicted_depth`, and `depth_conf`, `sky` and `intrinsics` outputs are used when present. The DA3 Large and Giant any-view checkpoints are CC-BY-NC 4.0 and are not offered by the download scripts.

Fixed-size exports (for example 504x504 or 504x378) dictate their input size; `InputResolution` only affects models with a dynamic input.

## Latency

- Resampling to the model size, ImageNet normalization and the planar layout are one compute pass on the GPU. Only the model-sized input is read back, whatever the resolution of the source, and any texture format is accepted.
- One frame is in flight at a time and a new one is captured as soon as the last result arrived, so results never queue behind stale input. `LatencyMs` reports capture to result, `InferenceMs` the model alone.
- The session is created from the file path with the provider chain DirectML or CUDA, then CPU. DirectML runs sequentially without memory-pattern planning, as that provider requires.
- `Smoothing` blends consecutive results and settles the depth range. DA3 predicts depth up to an unknown scale per frame, so this is what keeps a live visualization from pulsing.
- int8 files are small and meant for the CPU. Use fp16 or fp32 files with CUDA or DirectML.

## Depth Semantics

V3 models predict depth (far is larger), V2 models predict disparity (near is larger). `NormalizedDepthTexture` is near bright for both. `DepthToPosition` and `DepthParallax` have a `DepthIsDisparity` switch for V2. The Metric model returns meters when `FieldOfView` matches the source camera.

## Credits

- [Depth Anything 3](https://github.com/ByteDance-Seed/Depth-Anything-3) by ByteDance Seed
- [Depth Anything V2](https://github.com/DepthAnything/Depth-Anything-V2) by Lihe Yang et al.
- ONNX Runtime by Microsoft
