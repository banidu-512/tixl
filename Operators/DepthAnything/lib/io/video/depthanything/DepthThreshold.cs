using System;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using T3.Core.Resource;

#pragma warning disable CS8981

namespace Lib.io.video.depthanything;

/// <summary>
/// Creates a binary mask from depth data based on threshold ranges
/// Useful for foreground/background segmentation based on depth
/// </summary>
[Guid("f4a5b6c7-d8e9-f0a1-b2c3-d4e5f6a7b8c9")]
public class DepthThreshold : Instance<DepthThreshold>
{
    #region Outputs

    [Output(Guid = "a5b6c7d8-e9f0-a1b2-c3d4-e5f6a7b8c9d0", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> MaskTexture = new();

    [Output(Guid = "b6c7d8e9-f0a1-b2c3-d4e5-f6a7b8c9d0e1", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> PixelRatio = new();

    #endregion

    public DepthThreshold()
    {
        MaskTexture.UpdateAction = Update;
        PixelRatio.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var depthTexture = DepthTexture.GetValue(context);
        var minThreshold = MinThreshold.GetValue(context);
        var maxThreshold = MaxThreshold.GetValue(context);
        var invert = Invert.GetValue(context);
        var useNormalized = UseNormalized.GetValue(context);

        if (depthTexture == null || depthTexture.IsDisposed)
        {
            MaskTexture.Value = null;
            PixelRatio.Value = 0;
            return;
        }

        var device = ResourceManager.Device;
        var desc = depthTexture.Description;

        // Create or update mask texture
        if (_maskTexture == null || _maskTexture.Description.Width != desc.Width || _maskTexture.Description.Height != desc.Height)
        {
            _maskTexture?.Dispose();
            var maskDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.None
            };
            _maskTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(device, maskDesc));
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

            // Calculate actual min/max if using normalized thresholds
            float actualMin = minThreshold;
            float actualMax = maxThreshold;

            if (useNormalized)
            {
                float dataMin = float.MaxValue;
                float dataMax = float.MinValue;

                foreach (var d in depthData)
                {
                    if (!float.IsNaN(d) && !float.IsInfinity(d))
                    {
                        if (d < dataMin) dataMin = d;
                        if (d > dataMax) dataMax = d;
                    }
                }

                float range = dataMax - dataMin;
                if (range > 0.0001f)
                {
                    actualMin = dataMin + minThreshold * range;
                    actualMax = dataMin + maxThreshold * range;
                }
                else
                {
                    actualMin = dataMin;
                    actualMax = dataMax;
                }
            }

            // Create binary mask
            var maskData = new byte[width * height];
            int passCount = 0;

            Parallel.For(0, height * height, i =>
            {
                float depth = depthData[i];
                bool inRange = depth >= actualMin && depth <= actualMax &&
                              !float.IsNaN(depth) && !float.IsInfinity(depth);

                if (invert) inRange = !inRange;

                maskData[i] = inRange ? (byte)255 : (byte)0;

                if (inRange) System.Threading.Interlocked.Increment(ref passCount);
            });

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(maskData, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var dataOut = new DataBox(handle.AddrOfPinnedObject(), width, 0);
                device.ImmediateContext.UpdateSubresource(dataOut, _maskTexture);
            }
            finally
            {
                handle.Free();
            }

            MaskTexture.Value = _maskTexture;
            PixelRatio.Value = (float)passCount / (width * height);
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
        }
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

        _maskTexture?.Dispose();
        _maskTexture = null;

        lock (_textureCacheLock)
        {
            foreach (var texture in _cachedStagingTextures.Values)
                texture?.Dispose();
            _cachedStagingTextures.Clear();
        }

        base.Dispose(isDisposing);
    }

    #region Inputs

    [Input(Guid = "a3b4c5d6-e7f8-a9b0-c1d2-e3f4a5b6c7d8")]
    public readonly InputSlot<Texture2D> DepthTexture = new();

    [Input(Guid = "b4c5d6e7-f8a9-b0c1-d2e3-f4a5b6c7d8e9")]
    public readonly InputSlot<float> MinThreshold = new(0.0f);

    [Input(Guid = "c5d6e7f8-a9b0-c1d2-e3f4-a5b6c7d8e9f0")]
    public readonly InputSlot<float> MaxThreshold = new(0.5f);

    [Input(Guid = "d6e7f8a9-b0c1-d2e3-f4a5-b6c7d8e9f0a1")]
    public readonly InputSlot<bool> Invert = new(false);

    [Input(Guid = "e7f8a9b0-c1d2-e3f4-a5b6-c7d8e9f0a1b2")]
    public readonly InputSlot<bool> UseNormalized = new(true);

    #endregion

    #region Private Fields

    private Texture2D? _maskTexture;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, Format), Texture2D> _cachedStagingTextures = new();
    private readonly object _textureCacheLock = new object();

    #endregion
}
