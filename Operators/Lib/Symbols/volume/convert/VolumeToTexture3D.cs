using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;
using T3.Graphics.Compat;

namespace Lib.volume.convert;

/// Bakes a volume into a dense 3D texture (R32Float) plus its world-space bounds - the GPU bridge for raymarching. Connect the texture to [VolumeField] and feed that into [RaymarchField].
[Guid("db930e54-f0aa-5894-88ae-67b54353c513")]
internal sealed class VolumeToTexture3D : Instance<VolumeToTexture3D>
{
    [Output(Guid = "18e3a08c-edc3-5c42-a11f-eccaee753caa")]
    public readonly Slot<Texture3dWithViews> Texture = new();

    [Output(Guid = "1449308e-6d89-52eb-9ce2-124c4a972dab")]
    public readonly Slot<Vector3> BoundsMin = new();

    [Output(Guid = "ca612cd0-3d23-59a1-912e-cad23f75d645")]
    public readonly Slot<Vector3> BoundsMax = new();


    public VolumeToTexture3D()
    {
        // Any output can be the one a consumer pulls, so each must trigger Update.
        Texture.UpdateAction = Update;
        BoundsMin.UpdateAction = Update;
        BoundsMax.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var maxResolution = Math.Clamp((int)MaxResolution.GetValue(context), 8, 2048);

        var grid = volume?.GetFloatGrid();
        if (grid == null)
        {
            Texture.Value = null;
            _last = default;
            return;
        }

        var key = (volume, maxResolution);
        if (key == _last)
            return;

        var dense = VolumeDense.ExtractDense(grid, maxResolution);
        if (dense == null)
        {
            Texture.Value = null;
            return;
        }

        try
        {
            var description = new Texture3DDescription
                                  {
                                      Width = dense.SizeX,
                                      Height = dense.SizeY,
                                      Depth = dense.SizeZ,
                                      MipLevels = 1,
                                      Format = Format.R32_Float,
                                      BindFlags = BindFlags.ShaderResource,
                                      CpuAccessFlags = CpuAccessFlags.None,
                                      Usage = ResourceUsage.Default,
                                      OptionFlags = ResourceOptionFlags.None,
                                  };

            var texture = Texture3D.CreateTexture3D(description);
            var handle = GCHandle.Alloc(dense.Data, GCHandleType.Pinned);
            try
            {
                // A 3D texture is a single subresource, so the upload has to carry every depth
                // slice. The DataBox overload sizes the copy from SlicePitch alone, which uploads
                // one slice and leaves the rest of the volume zero - the raymarcher then samples
                // 0 everywhere and reports a surface right at the camera.
                var rowPitch = dense.SizeX * sizeof(float);
                var slicePitch = rowPitch * dense.SizeY;
                var totalBytes = slicePitch * dense.SizeZ;
                unsafe
                {
                    var data = new ReadOnlySpan<byte>((void*)handle.AddrOfPinnedObject(), totalBytes);
                    ResourceManager.Device.ImmediateContext.UpdateSubresource(texture, 0, data, rowPitch, slicePitch);
                }
            }
            finally
            {
                handle.Free();
            }

            var withViews = new Texture3dWithViews
                                {
                                    Texture = texture,
                                };
            texture.CreateShaderResourceView(ref withViews.Srv, "VolumeTexture3D");

            Texture.Value = withViews;

            var worldMin = grid.Transform.IndexCornerToWorld(dense.Origin.ToVector3());
            var worldMax = grid.Transform.IndexCornerToWorld(new Vector3(dense.Origin.X + dense.SizeX, dense.Origin.Y + dense.SizeY, dense.Origin.Z + dense.SizeZ));
            BoundsMin.Value = Vector3.Min(worldMin, worldMax);
            BoundsMax.Value = Vector3.Max(worldMin, worldMax);

            _lastTexture?.Texture.Dispose();
            _lastTexture = withViews;
            _last = key;
        }
        catch (Exception e)
        {
            Log.Warning($"VolumeToTexture3D failed: {e.Message}", this);
            Texture.Value = null;
        }
    }

    private Texture3dWithViews? _lastTexture;
    private (VdbVolume?, int) _last = (null, -1);

    [Input(Guid = "208b3d98-a7d7-5453-96cc-b29c89d1141b")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "b88c73a2-e66c-530e-9e72-a52bf2c27e13")]
    public readonly InputSlot<int> MaxResolution = new();

}