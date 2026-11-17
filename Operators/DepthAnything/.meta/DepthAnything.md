# DepthAnything Operator

## Overview
Estimates depth from images with Depth Anything 3 (preferred) or Depth Anything V2 through ONNX Runtime. The input is resampled and normalized on the GPU, inference runs on a worker thread, and the latest result is published when it is ready.

## Inputs
- **Texture2D InputTexture** - Source image or video texture, any format and size
- **bool Enabled** - Toggle processing on/off
- **ModelSize** - V2 Small, Base, Large, Small Fp32, Small Int8; V3 Small, Base, Mono Large, Metric Large
- **Resolution** - Inference size for models with dynamic input. Fixed-size exports ignore it
- **ExecutionProvider** - CPU, DirectML, CUDA, CoreML
- **bool MatchInputResolution** - Resize outputs to the input size on the CPU (costs latency)
- **float Smoothing** - Temporal blend of depth and depth range
- **float FieldOfView** - Horizontal field of view in degrees; used by the Metric model
- **DepthOutputFormat OutputFormat** - Grayscale/Color/Rainbow visualization
- **bool EnhanceContrast, InvertDepth, Debug**

## Outputs
- **Texture2D OutputTexture** - Original input (passthrough)
- **Texture2D DepthTexture** - Raw model output (R32_Float); V3 is depth (far larger), V2 is disparity (near larger)
- **Texture2D NormalizedDepthTexture** - Visualized depth (RGBA), near bright
- **Texture2D ConfidenceTexture** - Per-pixel confidence, when the model predicts it
- **Texture2D SkyTexture** - Sky score, when the model predicts it
- **int UpdateCount**, **float MinDepth**, **float MaxDepth**
- **float VerticalFov** - Camera field of view in degrees estimated by the model, 0 when unknown
- **float InferenceMs**, **float LatencyMs** - Model time and capture-to-result time
- **string Status**

## Model Files
Place in `Assets/`, or run `Scripts/download_models.py` (`.ps1` on Windows). The first file found wins:
- V3 Small: `da3-small-fp16.onnx`, `da3-small.onnx`, `da3-small-int8.onnx`
- V3 Base: `da3-base-fp16.onnx`, `da3-base.onnx`
- V3 Mono Large: `da3-mono-large-fp16.onnx`, `da3-mono-large-int8.onnx`
- V3 Metric Large: `da3-metric-large-fp16.onnx`
- V2: `depth-anything-v2-{small,base,large}-fp16.onnx`, `-small-fp32`, `-small-int8`

A V3 selection without an installed V3 file falls back to the V2 model of similar size.

## Low-Latency Notes
- Preprocessing (resample, ImageNet normalization, planar layout) is one compute pass; only the model-sized input crosses to the CPU.
- Only one frame is in flight; a new frame is captured as soon as the previous result arrived, so results are never queued behind stale input.
- int8 exports are meant for the CPU; use fp16 or fp32 files with CUDA or DirectML.
- Depth, filter, normal, position and parallax operators downstream stay on the GPU.
