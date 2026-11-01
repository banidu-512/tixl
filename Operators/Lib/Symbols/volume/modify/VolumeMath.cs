using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.modify;

/// Arithmetic between two volumes or a volume and a constant: Add, Subtract, Multiply, Divide, Min, Max. With UseConstant off, B participates; voxels missing from either volume read as background.
[Guid("608bc5be-57d2-5450-a9c2-f85a68fe95eb")]
internal sealed class VolumeMath : Instance<VolumeMath>
{
    [Output(Guid = "1b9bd8b1-5d32-547f-ad2f-fe0f1a7171f3")]
    public readonly Slot<VdbVolume> Result = new();


    private enum Operations
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Min,
        Max,
    }

    public VolumeMath()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var a = VolumeA.GetValue(context);
        var b = VolumeB.GetValue(context);
        var operation = (T3.Core.DataTypes.Volume.Tools.VolumeMath.BinaryOp)Operation.GetValue(context);
        var useConstant = UseConstant.GetValue(context);
        var constant = Constant.GetValue(context);

        var gridA = a?.GetFloatGrid();
        if (gridA == null)
        {
            Result.Value = null;
            _last = default;
            return;
        }

        var gridB = useConstant ? null : b?.GetFloatGrid();
        var key = (a, useConstant ? null : b, operation, useConstant, constant);
        if (key == _last)
            return;

        VdbGrid<float> result;
        if (gridB != null)
        {
            result = T3.Core.DataTypes.Volume.Tools.VolumeMath.Combine(gridA, gridB, operation);
        }
        else
        {
            result = T3.Core.DataTypes.Volume.Tools.VolumeMath.Constant(gridA, constant, operation);
        }

        Result.Value = new VdbVolume(result);
        _last = key;
    }

    private (VdbVolume?, VdbVolume?, T3.Core.DataTypes.Volume.Tools.VolumeMath.BinaryOp, bool, float) _last = (null, null, (T3.Core.DataTypes.Volume.Tools.VolumeMath.BinaryOp)(-1), false, 0);

    [Input(Guid = "db65839e-a02b-58a2-b1f7-341ea1b5b390")]
    public readonly InputSlot<VdbVolume> VolumeA = new();

    [Input(Guid = "7d8e6b01-c2e2-53d9-8d11-e96db67ec6fb")]
    public readonly InputSlot<VdbVolume> VolumeB = new();

    [Input(Guid = "4aeafb41-fe6e-556a-abc1-2346f9898b51", MappedType = typeof(Operations))]
    public readonly InputSlot<int> Operation = new();

    [Input(Guid = "9eb64387-fc91-54dd-a187-ac6110ec0188")]
    public readonly InputSlot<bool> UseConstant = new();

    [Input(Guid = "d7a5ce51-3e0f-57b5-81b6-3599334f0d4d")]
    public readonly InputSlot<float> Constant = new();

}