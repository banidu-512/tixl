using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.generate;

/// Generates an analytic primitive as a narrow-band level set volume (OpenVDB's LevelSetSphere and friends). Primitive volumes are exact SDFs, ready for [VolumeCsg], [VolumeNoise] and [VolumeToMesh].
[Guid("6583bddb-034b-5ff0-b12e-6e5c38bdba64")]
internal sealed class VolumePrimitive : Instance<VolumePrimitive>
{
    [Output(Guid = "1c2a036f-72e1-5cb0-a787-f8a5e06defe7")]
    public readonly Slot<VdbVolume> Volume = new();


    private enum Shapes
    {
        Sphere,
        Box,
        RoundedBox,
        Torus,
        Capsule,
        Cylinder,
        Cone,
        Octahedron,
        Plane,
    }

    public VolumePrimitive()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var shape = (VolumePrimitives.Shape)Shape.GetValue(context);
        var size = Size.GetValue(context);
        var center = Center.GetValue(context);
        var voxelSize = VoxelSize.GetValue(context);
        var band = (int)BandWidth.GetValue(context);

        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || voxelSize <= 0)
        {
            Volume.Value = null;
            _last = default;
            return;
        }

        var key = (shape, size, center, voxelSize, band);
        if (key == _last)
            return;

        try
        {
            var grid = VolumePrimitives.Create(shape, size, voxelSize, center, band);
            Volume.Value = new VdbVolume(grid);
            _last = key;
            _lastError = null;
        }
        catch (Exception e)
        {
            Volume.Value = null;
            if (_lastError != e.Message)
                Log.Warning($"VolumePrimitive failed: {e.Message}", this);
            _lastError = e.Message;

            // Remember the failing inputs so an impossible voxel size does not retry every frame.
            _last = key;
        }
    }

    private (VolumePrimitives.Shape, Vector3, Vector3, float, int) _last = ((VolumePrimitives.Shape)(-1), Vector3.Zero, Vector3.Zero, -1, -1);
    private string _lastError;

    [Input(Guid = "d2cf0b36-bd51-5f39-9ae4-ea24893f1222", MappedType = typeof(Shapes))]
    public readonly InputSlot<int> Shape = new();

    [Input(Guid = "1465ff4d-d5af-5bf8-8c87-10c6ab682967")]
    public readonly InputSlot<Vector3> Size = new();

    [Input(Guid = "bc2ac4ee-9a07-5229-87e0-9b527ee316c6")]
    public readonly InputSlot<Vector3> Center = new();

    [Input(Guid = "89ac8fe1-dfda-5b97-8d05-1c5378f896b4")]
    public readonly InputSlot<float> VoxelSize = new();

    [Input(Guid = "91618a1f-bc81-581a-99eb-77714007172d")]
    public readonly InputSlot<float> BandWidth = new();

}