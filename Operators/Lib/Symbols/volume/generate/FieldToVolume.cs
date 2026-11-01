using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.generate;

/// Samples a CPU scalar field (e.g. from [CustomScalarField] or [VolumeToField]) into a sparse grid over a box region. Treat the field as a signed distance to get a valid level set, or as density for a fog volume.
[Guid("34203fa5-2ff2-548b-98ee-fc427923ebb1")]
internal sealed class FieldToVolume : Instance<FieldToVolume>
{
    [Output(Guid = "a34b2f67-46e3-58ab-9cd0-c6c137869fa1")]
    public readonly Slot<VdbVolume> Volume = new();


    private enum Modes
    {
        Sdf,
        Fog,
    }

    /** Sampling the field is serial and the box is dense, so this bounds the freeze a tiny voxel size could cause. */
    private const long MaxSampleCount = 16_777_216;

    public FieldToVolume()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var field = Field.GetValue(context);
        var center = Center.GetValue(context);
        var size = Size.GetValue(context);
        var voxelSize = VoxelSize.GetValue(context);
        var mode = (Modes)Mode.GetValue(context);

        if (field == null || size.X <= 0 || size.Y <= 0 || size.Z <= 0 || voxelSize <= 0)
        {
            Volume.Value = null;
            return;
        }

        var bandWorld = 3f * voxelSize;
        var min = center - size * 0.5f;
        var max = center + size * 0.5f;

        var isSdf = mode == Modes.Sdf;
        var grid = new VdbGrid<float>(isSdf ? bandWorld : 0f, voxelSize,
                                      isSdf ? "LS" : "density",
                                      isSdf ? VdbGridClass.LevelSet : VdbGridClass.FogVolume)
        {
            Transform = new VdbTransform(voxelSize, Vector3.Zero),
        };

        var indexMin = grid.Transform.WorldToIndex(min - new Vector3(bandWorld));
        var indexMax = grid.Transform.WorldToIndex(max + new Vector3(bandWorld));

        var sampleCount = (long)(indexMax.X - indexMin.X + 1) * (indexMax.Y - indexMin.Y + 1) * (indexMax.Z - indexMin.Z + 1);
        if (sampleCount > MaxSampleCount)
        {
            Log.Warning($"FieldToVolume: {sampleCount} samples exceed the limit of {MaxSampleCount}; increase VoxelSize or reduce Size.", this);
            Volume.Value = null;
            return;
        }

        for (var z = indexMin.Z; z <= indexMax.Z; z++)
        {
            for (var y = indexMin.Y; y <= indexMax.Y; y++)
            {
                for (var x = indexMin.X; x <= indexMax.X; x++)
                {
                    var coord = new VdbCoord(x, y, z);
                    var value = field.Sample(grid.Transform.IndexToWorld(coord));
                    if (isSdf)
                    {
                        if (value > bandWorld)
                            continue;

                        // Inside the band the distance is active; deeper inside only the sign is kept,
                        // as inactive -band, so the solid does not read as outside.
                        if (value < -bandWorld)
                        {
                            grid.Tree.SetValueOnly(coord, -bandWorld);
                            continue;
                        }
                    }
                    else if (MathF.Abs(value) <= 1e-6f)
                    {
                        continue;
                    }

                    grid.Tree.SetValueOn(coord, value);
                }
            }
        }

        grid.Tree.Prune();
        Volume.Value = new VdbVolume(grid);
    }

    [Input(Guid = "623c40fb-5337-5a30-98c8-a0542cc6ee09")]
    public readonly InputSlot<ScalarField> Field = new();

    [Input(Guid = "07e12791-cb1a-57db-94ac-eeb2d8011cff")]
    public readonly InputSlot<Vector3> Center = new();

    [Input(Guid = "5ca91e75-52a4-542a-b317-fe1e35dd6a3e")]
    public readonly InputSlot<Vector3> Size = new();

    [Input(Guid = "cf9b56dc-27cd-58e9-96ff-29a4770f6277")]
    public readonly InputSlot<float> VoxelSize = new();

    [Input(Guid = "56318452-ae77-53d2-b4b1-55d93eaffcd7", MappedType = typeof(Modes))]
    public readonly InputSlot<int> Mode = new();

}