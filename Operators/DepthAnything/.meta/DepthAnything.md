# DepthAnything Operator

## Overview
Estimates depth from RGB images using the Depth Anything V2 model (Small, Base, or Large) with ONNX Runtime.

## Inputs
- **Texture2D Input** - Source image/video texture
- **bool Enabled** - Toggle processing on/off
- **ModelSize** - Model size (Small=256px, Base=384px, Large=518px)
- **DepthOutputFormat** - Visualization mode (Grayscale/Color/Rainbow)
- **bool EnhanceContrast** - Increase depth contrast
- **bool InvertDepth** - Flip depth values (near↔far)
- **bool Debug** - Enable debug logging

## Outputs
- **Texture2D Output** - Original input (passthrough)
- **Texture2D DepthTexture** - Raw depth (R32_Float format)
- **Texture2D NormalizedDepthTexture** - Visualized depth (RGBA)
- **int UpdateCount** - Number of processed frames
- **float MinDepth** - Minimum depth value this frame
- **float MaxDepth** - Maximum depth value this frame

## Model Files Required
Place in `Assets/`:
- `depth-anything-v2-small-fp16.onnx` (default)
- `depth-anything-v2-base-fp16.onnx` (optional)
- `depth-anything-v2-large-fp16.onnx` (optional)

## Performance Notes
- Small model: ~15-30ms per frame (CPU)
- Uses async worker thread for processing
- Staging textures are cached for performance
- Buffer pooling reduces GC pressure

## Use Cases
- Depth-based video effects
- Background separation
- 3D reconstruction preprocessing
- Point cloud generation
