# DepthAnything Operator - Integration Guide

## Quick Start

### 1. Add to Solution

Add the project reference to `t3.sln`:

```xml
<ProjectReference Include="Operators\DepthAnything\DepthAnything.csproj" />
```

### 2. Download Models

Place the ONNX model files in `Operators/DepthAnything/Assets/`:

```bash
# Create Assets directory
mkdir -p Assets/

# Download models from Hugging Face or convert from official repo
# depth-anything-v2-small-fp16.onnx (required)
# depth-anything-v2-base-fp16.onnx (optional)
# depth-anything-v2-large-fp16.onnx (optional)
```

### 3. Build and Run

```bash
# Build the solution
dotnet build t3.sln

# Or build from Visual Studio
```

## Operator Location in Editor

After building, operators will appear in the node browser under:

```
Lib > io > video > depthanything >
├── DepthAnything          (Main depth estimation)
├── DepthToNormal          (Depth → Normal conversion)
├── DepthFilter            (Bilateral filtering)
└── DepthThreshold         (Depth-based masking)
```

## Typical Workflow

```
Texture2D Input
    │
    ├──> [DepthAnything] ──> DepthTexture (R32_Float)
    │                           │
    │                           ├──> [DepthFilter] ──> Filtered Depth
    │                           │
    │                           ├──> [DepthToNormal] ──> Normal Map
    │                           │
    │                           └──> [DepthThreshold] ──> Binary Mask
    │
    └──> Original (passthrough)
```

## Performance Tips

1. **Use Small model** for real-time applications (256x256 input)
2. **Enable EnhanceContrast** for better depth separation
3. **Use bilateral filter** to reduce noise before normal generation
4. **Cache results** if input doesn't change every frame

## Troubleshooting

### Model not found error
- Ensure `.onnx` files are in `Assets/` folder
- Check file names match exactly (case-sensitive)
- Verify files are not corrupted

### Low depth quality
- Try larger model size (Base or Large)
- Enhance contrast for better separation
- Use depth filter to reduce noise

### Performance issues
- Reduce input resolution before depth estimation
- Use Small model instead of Base/Large
- Disable debug mode in production

## Dependencies

All dependencies are managed via NuGet:
- Microsoft.ML.OnnxRuntime (1.20.1)
- SharpDX (via TiXL Core)

## License

- This code: MIT
- Depth Anything V2 model: Apache 2.0
- ONNX Runtime: MIT
