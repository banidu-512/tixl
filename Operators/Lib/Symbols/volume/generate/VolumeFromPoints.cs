using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.generate;

/// Splats a point list into a sparse density volume (OpenVDB's particles-to-volume): every point contributes a normalized falloff kernel over its radius. Use it to volumize particles, or points scattered from geometry with [ScatterPointsInVolume].
[Guid("c698dc60-e135-5cc0-8346-31eb7ee77637")]
internal sealed class VolumeFromPoints : Instance<VolumeFromPoints>
{
    [Output(Guid = "d6f70e3d-84d5-57da-b94d-ffc284fca668")]
    public readonly Slot<VdbVolume> Volume = new();


    private enum Falloffs
    {
        Gaussian,
        Smoothstep,
        Constant,
    }

    public VolumeFromPoints()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var points = Points.GetValue(context) as StructuredList<Point>;
        var voxelSize = VoxelSize.GetValue(context);
        var radius = Radius.GetValue(context);
        var density = Density.GetValue(context);
        var falloff = (PointsToVolume.Falloff)Falloff.GetValue(context);

        if (points == null || points.NumElements == 0)
        {
            Volume.Value = null;
            _lastCount = -1;
            return;
        }

        // The list is reused in place by upstream operators, so identity alone cannot tell if the points moved.
        var positionsHash = HashPositions(points);
        if (points.NumElements == _lastCount
            && positionsHash == _lastPositionsHash
            && Math.Abs(voxelSize - _lastVoxelSize) < 1e-6f
            && Math.Abs(radius - _lastRadius) < 1e-6f
            && Math.Abs(density - _lastDensity) < 1e-6f
            && falloff == _lastFalloff)
        {
            return;
        }

        var positions = new Vector3[points.NumElements];
        for (var i = 0; i < points.NumElements; i++)
            positions[i] = points.TypedElements[i].Position;

        try
        {
            var grid = PointsToVolume.Splat(positions, null, voxelSize, radius, density, falloff, seed: 0);
            Volume.Value = new VdbVolume(grid);
            _lastPositionsHash = positionsHash;
            _lastCount = points.NumElements;
            _lastVoxelSize = voxelSize;
            _lastRadius = radius;
            _lastDensity = density;
            _lastFalloff = falloff;
            _lastError = null;
        }
        catch (Exception e)
        {
            Volume.Value = null;
            if (_lastError != e.Message)
                Log.Warning($"VolumeFromPoints failed: {e.Message}", this);
            _lastError = e.Message;

            // Remember the failing inputs so a bad radius does not retry (and log) every frame.
            _lastPositionsHash = positionsHash;
            _lastCount = points.NumElements;
            _lastVoxelSize = voxelSize;
            _lastRadius = radius;
            _lastDensity = density;
            _lastFalloff = falloff;
        }
    }

    private static int HashPositions(StructuredList<Point> points)
    {
        var hash = 17;
        for (var i = 0; i < points.NumElements; i++)
        {
            var position = points.TypedElements[i].Position;
            hash = hash * 31 + BitConverter.SingleToInt32Bits(position.X);
            hash = hash * 31 + BitConverter.SingleToInt32Bits(position.Y);
            hash = hash * 31 + BitConverter.SingleToInt32Bits(position.Z);
        }

        return hash;
    }

    private int _lastPositionsHash;
    private int _lastCount = -1;
    private float _lastVoxelSize = -1;
    private float _lastRadius = -1;
    private float _lastDensity = -1;
    private PointsToVolume.Falloff _lastFalloff = (PointsToVolume.Falloff)(-1);
    private string _lastError;

    [Input(Guid = "51935275-be34-555d-a9dc-7fbc6a55db39")]
    public readonly InputSlot<StructuredList> Points = new();

    [Input(Guid = "234a9c82-72f4-5388-9473-8bff628ba0fe")]
    public readonly InputSlot<float> VoxelSize = new();

    [Input(Guid = "8a3c8412-7266-50ce-83ba-7841bb431863")]
    public readonly InputSlot<float> Radius = new();

    [Input(Guid = "5f863764-57b0-5e98-b6ac-4c621fcb5380")]
    public readonly InputSlot<float> Density = new();

    [Input(Guid = "5fbe41e2-1376-5ead-b9aa-97c925092e26", MappedType = typeof(Falloffs))]
    public readonly InputSlot<int> Falloff = new();

}