using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.modify;

/// Smoothing and value filters on the active voxels (OpenVDB's tools/Filter): Gaussian and Box smooth noise (DreamWorks' cloud trick), Median removes outliers, Offset shifts all values.
[Guid("4c253b2e-fc94-586f-b6c9-c30c7862cf9d")]
internal sealed class VolumeFilter : Instance<VolumeFilter>
{
    [Output(Guid = "7fb850c0-2036-5922-89df-565c9c0ba8b7")]
    public readonly Slot<VdbVolume> Result = new();


    private enum FilterTypes
    {
        Gaussian,
        Box,
        Median,
        Offset,
    }

    public VolumeFilter()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var filterType = (T3.Core.DataTypes.Volume.Tools.VolumeFilter.FilterType)FilterType.GetValue(context);
        var radius = Radius.GetValue(context);
        var iterations = (int)Iterations.GetValue(context);
        var offset = Offset.GetValue(context);

        var grid = volume?.GetFloatGrid();
        if (grid == null)
        {
            Result.Value = volume;
            _last = default;
            return;
        }

        var key = (volume, filterType, radius, iterations, offset);
        if (key == _last)
            return;

        Result.Value = new VdbVolume(T3.Core.DataTypes.Volume.Tools.VolumeFilter.Apply(grid, filterType,
                                                        (int)MathF.Round(radius / grid.Transform.VoxelSize),
                                                        offset, iterations));
        _last = key;
    }

    private (VdbVolume?, T3.Core.DataTypes.Volume.Tools.VolumeFilter.FilterType, float, int, float) _last = (null, (T3.Core.DataTypes.Volume.Tools.VolumeFilter.FilterType)(-1), -1, -1, 0);

    [Input(Guid = "0241402e-4fae-5ca3-98be-eb4bcd4fc339")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "f4356aa3-0941-5c80-bc46-4670bb4edf85", MappedType = typeof(FilterTypes))]
    public readonly InputSlot<int> FilterType = new();

    [Input(Guid = "caed5556-f587-5958-89df-6521430780da")]
    public readonly InputSlot<float> Radius = new();

    [Input(Guid = "12e5440b-633d-5f77-a421-df4b4d39c4c5")]
    public readonly InputSlot<int> Iterations = new();

    [Input(Guid = "d3ff33b1-1a0e-5921-be50-0486032ad9bc")]
    public readonly InputSlot<float> Offset = new();

}