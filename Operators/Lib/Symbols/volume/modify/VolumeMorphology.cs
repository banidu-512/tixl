using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.modify;

/// Morphological dilate/erode on volumes. Level sets offset the surface outward (Dilate) or inward (Erode); Open and Close apply both to remove thin features or pinholes. Fog volumes use min/max stencil filtering instead.
[Guid("f3b93568-90a7-5119-a0a9-fcd9f1ee60e0")]
internal sealed class VolumeMorphology : Instance<VolumeMorphology>
{
    [Output(Guid = "e385bebb-b9e7-576f-9c3a-f794d84d5d48")]
    public readonly Slot<VdbVolume> Result = new();


    private enum Operations
    {
        Dilate,
        Erode,
        Open,
        Close,
    }

    public VolumeMorphology()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var operation = (T3.Core.DataTypes.Volume.Tools.VolumeMorphology.Operation)Operation.GetValue(context);
        var radius = Radius.GetValue(context);

        var grid = volume?.GetFloatGrid();
        if (grid == null || radius <= 0)
        {
            Result.Value = volume;
            _last = default;
            return;
        }

        var key = (volume, operation, radius);
        if (key == _last)
            return;

        try
        {
            Result.Value = new VdbVolume(T3.Core.DataTypes.Volume.Tools.VolumeMorphology.Apply(grid, operation, radius));
            _last = key;
            _lastError = null;
        }
        catch (Exception e)
        {
            // An exception escaping an update is fatal to the editor; degrade to an empty output.
            Result.Value = null;
            _last = default;
            if (_lastError != e.Message)
                Log.Warning($"VolumeMorphology failed: {e.Message}", this);
            _lastError = e.Message;
        }
    }

    private (VdbVolume, T3.Core.DataTypes.Volume.Tools.VolumeMorphology.Operation, float) _last = (null, (T3.Core.DataTypes.Volume.Tools.VolumeMorphology.Operation)(-1), -1);
    private string _lastError;

    [Input(Guid = "f762b0d3-0f82-5660-9169-c94524c5a414")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "2ad87fa8-ac60-5972-a42e-38a4a224c4f1", MappedType = typeof(Operations))]
    public readonly InputSlot<int> Operation = new();

    [Input(Guid = "afb22fff-6a74-5f28-893e-950eb71eba4d")]
    public readonly InputSlot<float> Radius = new();

}