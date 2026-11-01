using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.modify;

/// Changes a volume's voxel size, resampling values trilinearly (OpenVDB's resampleToMatch). Smaller voxels = finer detail and more memory; larger = cheaper.
[Guid("5c4dc3a7-fc82-59c1-afde-daf023bad7c9")]
internal sealed class VolumeResample : Instance<VolumeResample>
{
    [Output(Guid = "def20e90-909a-5405-b7ad-0e6a953727db")]
    public readonly Slot<VdbVolume> Result = new();


    public VolumeResample()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var voxelSize = VoxelSize.GetValue(context);

        var grid = volume?.GetFloatGrid();
        if (grid == null || voxelSize <= 0)
        {
            Result.Value = volume;
            _last = default;
            return;
        }

        var key = (volume, voxelSize);
        if (key == _last)
            return;

        try
        {
            Result.Value = new VdbVolume(T3.Core.DataTypes.Volume.Tools.VolumeDense.Resample(grid, voxelSize));
            _last = key;
            _lastError = null;
        }
        catch (Exception e)
        {
            // An exception escaping an update is fatal to the editor. The core rejects results that
            // would exhaust memory, so report that instead of passing a broken volume downstream.
            Result.Value = null;
            _last = default;
            if (_lastError != e.Message)
                Log.Warning($"VolumeResample failed: {e.Message}", this);
            _lastError = e.Message;
        }
    }

    private (VdbVolume?, float) _last = (null, -1);
    private string? _lastError;

    [Input(Guid = "3b90bbbd-0607-5c2b-8e65-61c8a7e04030")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "d21b910d-a7d9-507f-b7f6-4053dbdee86a")]
    public readonly InputSlot<float> VoxelSize = new();

}