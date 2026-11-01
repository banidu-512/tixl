# Depth Anything V2 Operator Package for TiXL

This operator package integrates **Depth Anything V2** (Small model) with TiXL for real-time depth estimation from Texture2D inputs.

## Features

- **Real-time depth estimation** from video/texture inputs
- **Multiple model sizes**: Small (256x256), Base (384x384), Large (518x518)
- **FP16 quantization** for improved performance
- **Multiple output formats**: Grayscale, Color, Rainbow visualization
- **Adjustable parameters**: Contrast enhancement, depth inversion
- **Async processing** with worker thread pattern (similar to MediaPipe operators)

## Model Requirements

You need to download the Depth Anything V2 ONNX models and place them in the `Assets/` folder:

### Required Model Files

1. **depth-anything-v2-small-fp16.onnx** (Default, ~24MB)
   - Download: [Depth Anything V2 Small](https://github.com/DepthAnything/Depth-Anything-V2)
   - Input: 256x256
   - Best for: Real-time applications

2. **depth-anything-v2-base-fp16.onnx** (Optional, ~97MB)
   - Download: [Depth Anything V2 Base](https://github.com/DepthAnything/Depth-Anything-V2)
   - Input: 384x384
   - Best for: Balance between quality and speed

3. **depth-anything-v2-large-fp16.onnx** (Optional, ~345MB)
   - Download: [Depth Anything V2 Large](https://github.com/DepthAnything/Depth-Anything-V2)
   - Input: 518x518
   - Best for: Highest quality depth estimation

### How to Get FP16 Models

The official repository provides FP32 models. To get FP16 models:

1. Download the ONNX models from the official repo
2. Convert to FP16 using ONNX Runtime tools:
   ```bash
   python -m onnxruntime.quantization.preprocess \
     --input_model depth_anything_v2_small.onnx \
     --output_model depth-anything-v2-small-fp16.onnx \
     --per_channel \
     --reduce_range
   ```

## Installation

1. Place this folder in `Operators/DepthAnything/`
2. Add project reference to your TiXL solution
3. Download required model files to `Assets/`
4. Build the solution

## Usage

### Inputs

| Name | Type | Description |
|------|------|-------------|
| InputTexture | Texture2D | Input video/image texture |
| Enabled | bool | Enable/disable processing |
| ModelSize | enum | Small/Base/Large model selection |
| OutputFormat | enum | Grayscale/Color/Rainbow visualization |
| EnhanceContrast | bool | Enhance depth contrast |
| InvertDepth | bool | Invert depth values |
| Debug | bool | Enable debug logging |

### Outputs

| Name | Type | Description |
|------|------|-------------|
| OutputTexture | Texture2D | Passthrough of input texture |
| DepthTexture | Texture2D | Raw depth (R32_Float) |
| NormalizedDepthTexture | Texture2D | Visualized depth (RGBA) |
| UpdateCount | int | Frame counter |
| MinDepth | float | Minimum depth value in frame |
| MaxDepth | float | Maximum depth value in frame |

## Technical Details

- **Framework**: ONNX Runtime 1.20.1
- **Processing**: Async worker thread with input/output queues
- **Memory**: Staging texture caching and buffer pooling
- **GPU Support**: Compatible with DirectX11/SharpDX

## Dependencies

- Microsoft.ML.OnnxRuntime (1.20.1)
- SharpDX (via TiXL Core)
- OpenCvSharp (optional, for advanced use cases)

## License

Depth Anything V2 is licensed under Apache 2.0. See the official repository for details.

## Credits

- [Depth Anything V2](https://github.com/DepthAnything/Depth-Anything-V2) by Lihe Yang et al.
- ONNX Runtime by Microsoft
