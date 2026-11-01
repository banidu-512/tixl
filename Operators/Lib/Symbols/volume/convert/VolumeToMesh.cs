using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.convert;

/// Extracts an isosurface from a volume as a triangle mesh (OpenVDB's volumeToMesh, marching cubes with shared vertices and gradient normals). Isovalue 0 extracts a level set's surface; for fog volumes pick your density cutoff.
/// 
/// Render with [DrawMesh] after [GeometryToMesh].
[Guid("5bc7d656-8a0f-5834-88da-14c3048d760c")]
internal sealed class VolumeToMesh : Instance<VolumeToMesh>
{
    [Output(Guid = "362c33b2-88c6-537e-ad87-035c85afd29d")]
    public readonly Slot<MeshGeometry> Geometry = new();


    public VolumeToMesh()
    {
        Geometry.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var isovalue = Isovalue.GetValue(context);

        var grid = volume?.GetFloatGrid();
        if (grid == null)
        {
            Geometry.Value = null;
            _last = default;
            return;
        }

        var key = (volume, isovalue);
        if (key == _last)
            return;

        try
        {
            Geometry.Value = T3.Core.DataTypes.Volume.VolumeToMesh.Extract(grid, isovalue);
            _last = key;
            _lastError = null;
        }
        catch (Exception e)
        {
            // An exception escaping an update is fatal to the editor; degrade to an empty output.
            Geometry.Value = null;
            _last = default;
            if (_lastError != e.Message)
                Log.Warning($"VolumeToMesh failed: {e.Message}", this);
            _lastError = e.Message;
        }
    }

    private (VdbVolume?, float) _last = (null, float.NaN);
    private string? _lastError;

    [Input(Guid = "19c6ca3f-d7ff-507f-8d28-2490f0edb66e")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "345f946b-8c36-5548-a569-4123e0544b8f")]
    public readonly InputSlot<float> Isovalue = new();

}