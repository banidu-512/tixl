using System;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using T3.Core.Resource;

#pragma warning disable CS8981

namespace Lib.io.video.depthanything;

/// <summary>
/// Filters and processes depth data with bilateral filtering for edge-aware smoothing
/// </summary>
[Guid("e2f3a4b5-c6d7-e8f9-a0b1-c2d3e4f5a6b7")]
public class DepthFilter : Instance<DepthFilter>
{
    #region Outputs

    [Output(Guid = "f3a4b5c6-d7e8-f9a0-b1c2-d3e4f5a6b7c8", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> FilteredDepth = new();

    #endregion

    public DepthFilter()
    {
        FilteredDepth.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var depthTexture = DepthTexture.GetValue(context);
        var sigmaSpace = SigmaSpace.GetValue(context);
        var sigmaColor = SigmaColor.GetValue(context);
        var enabled = Enabled.GetValue(context);

        if (!enabled || depthTexture == null || depthTexture.IsDisposed)
        {
            FilteredDepth.Value = depthTexture;
            return;
        }

        var device = ResourceManager.Device;
        var desc = depthTexture.Description;

        // Create or update output texture
        if (_outputTexture == null || _outputTexture.Description.Width != desc.Width || _outputTexture.Description.Height != desc.Height)
        {
            _outputTexture?.Dispose();
            var outputDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.None
            };
            _outputTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(device, outputDesc));
        }

        // Get depth data
        var stagingTexture = GetOrCreateStagingTexture(desc.Width, desc.Height, desc.Format);
        device.ImmediateContext.CopyResource(depthTexture, stagingTexture);
        var dataBox = device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);

        if (dataBox.DataPointer == IntPtr.Zero)
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
            return;
        }

        try
        {
            int width = desc.Width;
            int height = desc.Height;
            var depthData = new float[width * height];

            // Copy data from staging texture
            IntPtr srcPtr = dataBox.DataPointer;
            int rowPitch = dataBox.RowPitch;

            for (int y = 0; y < height; y++)
            {
                int dstOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    unsafe
                    {
                        IntPtr rowSrcPtr = srcPtr + (y * rowPitch) + (x * sizeof(float));
                        depthData[dstOffset + x] = *((float*)rowSrcPtr);
                    }
                }
            }

            // Apply bilateral filter
            var filteredData = ApplyBilateralFilter(depthData, width, height, sigmaSpace, sigmaColor);

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(filteredData, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var dataOut = new DataBox(handle.AddrOfPinnedObject(), width * 4, 0);
                device.ImmediateContext.UpdateSubresource(dataOut, _outputTexture);
            }
            finally
            {
                handle.Free();
            }

            FilteredDepth.Value = _outputTexture;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
        }
    }

    private float[] ApplyBilateralFilter(float[] depth, int width, int height, float sigmaSpace, float sigmaColor)
    {
        var filtered = new float[width * height];
        int radius = (int)(sigmaSpace * 2);
        if (radius < 1) radius = 1;
        if (radius > 5) radius = 5; // Limit kernel size for performance

        float spaceCoeff = -0.5f / (sigmaSpace * sigmaSpace);
        float colorCoeff = -0.5f / (sigmaColor * sigmaColor);

        // Compute Gaussian kernel weights
        var spaceWeights = new float[radius * 2 + 1];
        for (int i = -radius; i <= radius; i++)
        {
            spaceWeights[i + radius] = (float)System.Math.Exp(i * i * spaceCoeff);
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float centerDepth = depth[idx];

                if (float.IsNaN(centerDepth) || float.IsInfinity(centerDepth))
                {
                    filtered[idx] = centerDepth;
                    continue;
                }

                float sum = 0;
                float weightSum = 0;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            int nIdx = ny * width + nx;
                            float neighborDepth = depth[nIdx];

                            if (!float.IsNaN(neighborDepth) && !float.IsInfinity(neighborDepth))
                            {
                                float spaceWeight = spaceWeights[Math.Abs(dx) + radius] * spaceWeights[Math.Abs(dy) + radius];
                                float colorDiff = centerDepth - neighborDepth;
                                float colorWeight = (float)System.Math.Exp(colorDiff * colorDiff * colorCoeff);

                                float weight = spaceWeight * colorWeight;
                                sum += neighborDepth * weight;
                                weightSum += weight;
                            }
                        }
                    }
                }

                filtered[idx] = weightSum > 0 ? sum / weightSum : centerDepth;
            }
        });

        return filtered;
    }

    private Texture2D GetOrCreateStagingTexture(int width, int height, Format format)
    {
        var key = (width, height, format);

        if (_cachedStagingTextures.TryGetValue(key, out var cachedTexture))
            return cachedTexture;

        lock (_textureCacheLock)
        {
            if (_cachedStagingTextures.TryGetValue(key, out cachedTexture))
                return cachedTexture;

            var newTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(ResourceManager.Device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            }));

            _cachedStagingTextures[key] = newTexture;
            return newTexture;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;

        _outputTexture?.Dispose();
        _outputTexture = null;

        lock (_textureCacheLock)
        {
            foreach (var texture in _cachedStagingTextures.Values)
                texture?.Dispose();
            _cachedStagingTextures.Clear();
        }

        base.Dispose(isDisposing);
    }

    #region Inputs

    [Input(Guid = "a2b3c4d5-e6f7-a8b9-c0d1-e2f3a4b5c6d7")]
    public readonly InputSlot<Texture2D> DepthTexture = new();

    [Input(Guid = "b3c4d5e6-f7a8-b9c0-d1e2-f3a4b5c6d7e8")]
    public readonly InputSlot<float> SigmaSpace = new(1.0f);

    [Input(Guid = "c4d5e6f7-a8b9-c0d1-e2f3-a4b5c6d7e8f9")]
    public readonly InputSlot<float> SigmaColor = new(0.1f);

    [Input(Guid = "d5e6f7a8-b9c0-d1e2-f3a4-b5c6d7e8f9a0")]
    public readonly InputSlot<bool> Enabled = new(true);

    #endregion

    #region Private Fields

    private Texture2D? _outputTexture;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, Format), Texture2D> _cachedStagingTextures = new();
    private readonly object _textureCacheLock = new object();

    #endregion
}
