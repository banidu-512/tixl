using System;
using System.Numerics;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core.Resource;

#pragma warning disable CS8981

namespace Lib.io.video.depthanything;

/// <summary>
/// Converts depth map to normal map using Sobel edge detection
/// </summary>
[Guid("d9e0f1a2-3b4c-5d6e-7f8a-9b0c1d2e3f4a")]
public class DepthToNormal : Instance<DepthToNormal>
{
    #region Outputs

    [Output(Guid = "e1f2a3b4-c5d6-e7f8-a9b0-c1d2e3f4a5b6", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> NormalTexture = new();

    #endregion

    public DepthToNormal()
    {
        NormalTexture.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var depthTexture = DepthTexture.GetValue(context);
        var strength = Strength.GetValue(context);
        var invert = Invert.GetValue(context);

        if (depthTexture == null || depthTexture.IsDisposed)
        {
            NormalTexture.Value = null;
            return;
        }

        var device = ResourceManager.Device;
        var desc = depthTexture.Description;

        // Create or update normal texture
        if (_normalTexture == null || _normalTexture.Description.Width != desc.Width || _normalTexture.Description.Height != desc.Height)
        {
            _normalTexture?.Dispose();
            var normalDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.None
            };
            _normalTexture = new Texture2D(new SharpDX.Direct3D11.Texture2D(device, normalDesc));
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
            int floatCount = width * height;

            for (int y = 0; y < height; y++)
            {
                IntPtr rowSrc = srcPtr + (y * rowPitch);
                int dstOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    unsafe
                    {
                        depthData[dstOffset + x] = *((float*)(rowSrc + (x * sizeof(float))));
                    }
                }
            }

            // Compute normals using Sobel operator
            var normals = ComputeNormals(depthData, width, height, strength, invert);

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(normals, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var dataOut = new DataBox(handle.AddrOfPinnedObject(), width * 4, 0);
                device.ImmediateContext.UpdateSubresource(dataOut, _normalTexture);
            }
            finally
            {
                handle.Free();
            }

            NormalTexture.Value = _normalTexture;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
        }
    }

    private byte[] ComputeNormals(float[] depth, int width, int height, float strength, bool invert)
    {
        var normals = new byte[width * height * 4];

        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                // Sobel kernels
                float sobelX = 0, sobelY = 0;

                // 3x3 neighborhood
                int idx = y * width + x;
                float tl = depth[idx - width - 1];
                float t = depth[idx - width];
                float tr = depth[idx - width + 1];
                float l = depth[idx - 1];
                float r = depth[idx + 1];
                float bl = depth[idx + width - 1];
                float b = depth[idx + width];
                float br = depth[idx + width + 1];

                // Sobel X
                sobelX = (tr + 2 * r + br) - (tl + 2 * l + bl);

                // Sobel Y
                sobelY = (bl + 2 * b + br) - (tl + 2 * t + tr);

                // Create normal vector
                var normal = new Vector3(-sobelX, -sobelY, 1.0f / strength);
                normal = Vector3.Normalize(normal);

                if (invert)
                    normal.Z = -normal.Z;

                // Convert to 0-1 range
                normal = (normal + Vector3.One) * 0.5f;

                int rgbaIdx = (y * width + x) * 4;
                normals[rgbaIdx + 0] = (byte)(normal.X * 255); // R
                normals[rgbaIdx + 1] = (byte)(normal.Y * 255); // G
                normals[rgbaIdx + 2] = (byte)(normal.Z * 255); // B
                normals[rgbaIdx + 3] = 255;                   // A
            }
        });

        return normals;
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

        _normalTexture?.Dispose();
        _normalTexture = null;

        lock (_textureCacheLock)
        {
            foreach (var texture in _cachedStagingTextures.Values)
                texture?.Dispose();
            _cachedStagingTextures.Clear();
        }

        base.Dispose(isDisposing);
    }

    #region Inputs

    [Input(Guid = "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")]
    public readonly InputSlot<Texture2D> DepthTexture = new();

    [Input(Guid = "b2c3d4e5-f6a7-b8c9-d0e1-f2a3b4c5d6e7")]
    public readonly InputSlot<float> Strength = new(1.0f);

    [Input(Guid = "c3d4e5f6-a7b8-c9d0-e1f2-a3b4c5d6e7f8")]
    public readonly InputSlot<bool> Invert = new(false);

    #endregion

    #region Private Fields

    private Texture2D? _normalTexture;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, Format), Texture2D> _cachedStagingTextures = new();
    private readonly object _textureCacheLock = new object();

    #endregion
}
