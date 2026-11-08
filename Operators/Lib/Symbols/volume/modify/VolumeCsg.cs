using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.modify;

/// Constructive solid geometry on level-set volumes (OpenVDB's csgUnion/Intersection/Difference): Union merges, Intersection keeps the overlap, Difference carves B out of A. The result is a fresh level set; feed it to [VolumeToMesh] or keep combining.
[Guid("49449447-78bd-525f-96ad-83759321316d")]
internal sealed class VolumeCsg : Instance<VolumeCsg>
{
    [Output(Guid = "e7711aec-252e-5e6c-bcea-a772dbd93e2c")]
    public readonly Slot<VdbVolume> Volume = new();


    private enum Operations
    {
        Union,
        Intersection,
        Difference,
    }

    public VolumeCsg()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var a = VolumeA.GetValue(context);
        var b = VolumeB.GetValue(context);
        var operation = (T3.Core.DataTypes.Volume.Tools.VolumeCsg.Operation)Operation.GetValue(context);

        var gridA = a?.GetFloatGrid();
        var gridB = b?.GetFloatGrid();
        if (gridA == null || gridB == null)
        {
            Volume.Value = null;
            _lastA = null;
            _lastB = null;
            return;
        }

        if (ReferenceEquals(a, _lastA) && ReferenceEquals(b, _lastB) && operation == _lastOp)
            return;

        try
        {
            Volume.Value = new VdbVolume(T3.Core.DataTypes.Volume.Tools.VolumeCsg.Combine(gridA, gridB, operation));
            _lastA = a;
            _lastB = b;
            _lastOp = operation;
            _lastError = null;
        }
        catch (Exception e)
        {
            // An exception escaping an update is fatal to the editor, so a broken input volume
            // must degrade to an empty output instead.
            Volume.Value = null;
            _lastA = null;
            _lastB = null;
            if (_lastError != e.Message)
                Log.Warning($"VolumeCsg failed: {e.Message}", this);
            _lastError = e.Message;
        }
    }

    private VdbVolume _lastA;
    private VdbVolume _lastB;
    private T3.Core.DataTypes.Volume.Tools.VolumeCsg.Operation _lastOp = (T3.Core.DataTypes.Volume.Tools.VolumeCsg.Operation)(-1);
    private string _lastError;

    [Input(Guid = "c8eda1a2-1379-572e-92d1-ea640a6b9e61")]
    public readonly InputSlot<VdbVolume> VolumeA = new();

    [Input(Guid = "91c95ddc-98c5-5e99-947e-a9f94cbaf1de")]
    public readonly InputSlot<VdbVolume> VolumeB = new();

    [Input(Guid = "e0616bd7-2a4a-5953-95a5-a11929871bd9", MappedType = typeof(Operations))]
    public readonly InputSlot<int> Operation = new();

}