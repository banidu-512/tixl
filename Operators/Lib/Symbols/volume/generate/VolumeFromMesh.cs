using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.generate;

/// Converts a mesh into an OpenVDB-style sparse volume: either a signed distance field (SDF, negative inside the surface) or a fog volume (density 0..1 falling off from the surface). Distances are exact within a narrow band around the surface (BandWidth voxels).
/// 
/// Convert back with [VolumeToMesh], raymarch via [VolumeToTexture3D] + [VolumeField], or export with [SaveVdb].
[Guid("1cd14db1-d084-5d7b-935e-0c8cac5b9bd6")]
internal sealed class VolumeFromMesh : Instance<VolumeFromMesh>
{
    [Output(Guid = "09b1560a-62c1-5824-965e-c99ca55057de")]
    public readonly Slot<VdbVolume> Volume = new();


    private enum VolumeModes
    {
        Sdf,
        Fog,
    }

    public VolumeFromMesh()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var mesh = Geometry.GetValue(context);
        var voxelSize = VoxelSize.GetValue(context);
        var bandWidth = (int)BandWidth.GetValue(context);
        var mode = (VolumeModes)Mode.GetValue(context);

        if (mesh == null || mesh.PointCount == 0 || mesh.FaceCount == 0)
        {
            Volume.Value = null;
            _lastMesh = null;
            return;
        }

        // Rebuild only when the mesh, its topology version or parameters changed.
        if (ReferenceEquals(mesh, _lastMesh)
            && mesh.Version == _lastMeshVersion
            && Math.Abs(voxelSize - _lastVoxelSize) < 1e-6f
            && bandWidth == _lastBand
            && mode == _lastMode)
        {
            return;
        }

        try
        {
            var grid = MeshToVolume.ConvertGeometry(mesh.Positions, mesh.FaceCornerOffsets, mesh.CornerPointIndices,
                                                    voxelSize, bandWidth,
                                                    mode == VolumeModes.Sdf ? VdbVolumeType.Sdf : VdbVolumeType.Fog);
            Volume.Value = new VdbVolume(grid);
            _lastMesh = mesh;
            _lastMeshVersion = mesh.Version;
            _lastVoxelSize = voxelSize;
            _lastBand = bandWidth;
            _lastMode = mode;
        }
        catch (Exception e)
        {
            Log.Warning($"VolumeFromMesh failed: {e.Message}", this);
            Volume.Value = null;
        }
    }

    private MeshGeometry? _lastMesh;
    private int _lastMeshVersion = -1;
    private float _lastVoxelSize = -1;
    private int _lastBand = -1;
    private VolumeModes _lastMode = (VolumeModes)(-1);

    [Input(Guid = "e4c8434c-5d4a-57b7-996e-5006785e8cf8")]
    public readonly InputSlot<MeshGeometry?> Geometry = new();

    [Input(Guid = "5dc83de1-24e8-52c8-9ef4-2ad2aafab51f")]
    public readonly InputSlot<float> VoxelSize = new();

    [Input(Guid = "780d9a0e-ef3e-5db7-a842-3aeeef647cad")]
    public readonly InputSlot<float> BandWidth = new();

    [Input(Guid = "674cd211-f65b-5e98-bca5-0a78c68e770a", MappedType = typeof(VolumeModes))]
    public readonly InputSlot<int> Mode = new();

}