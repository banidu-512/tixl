using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;

namespace Lib.volume.io;

/// Writes the volume as a NanoVDB .nvdb file: the GPU-flattened grid format used by CUDA / renderers (and readable by the reference NanoVDB library).
[Guid("e90edc95-86bb-5508-a36c-fc77f5d14459")]
internal sealed class SaveNanoVdb : Instance<SaveNanoVdb>
{
    [Output(Guid = "4ce764eb-1a7a-5d04-96e9-4ddeba3804d9")]
    public readonly Slot<bool> Saved = new();


    public SaveNanoVdb()
    {
        Saved.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var path = PathSaved.GetValue(context)?.Trim();
        var save = Save.GetValue(context);

        if (!save)
        {
            _wasSave = false;
            Saved.Value = false;
            return;
        }

        if (_wasSave)
        {
            Saved.Value = false;
            return;
        }

        _wasSave = true;
        Saved.Value = false;

        if (volume == null || string.IsNullOrEmpty(path))
            return;

        try
        {
            NanoVdbBuffer? buffer = volume.GetFloatGrid() is { } floatGrid ? NanoVdbBuffer.FromGrid(floatGrid)
                                       : volume.GetVectorGrid() is { } vecGrid ? NanoVdbBuffer.FromGrid(vecGrid)
                                       : null;

            if (buffer == null)
            {
                Log.Warning("SaveNanoVdb: no float or vector grid to save", this);
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            buffer.Save(path);
            var summary = buffer.ReadSummary();
            Log.Debug($"Saved NanoVDB ({summary.VoxelCount} active voxels) to {path}", this);
            Saved.Value = true;
        }
        catch (Exception e)
        {
            Log.Warning($"SaveNanoVdb failed for {path}: {e.Message}", this);
        }
    }

    private bool _wasSave;

    [Input(Guid = "7ddbba10-4371-5f6e-ba1f-ce3f26c47d6e")]
    public readonly InputSlot<VdbVolume> Volume = new();

    [Input(Guid = "413cb751-966b-5e5e-b8ee-e6c10a0db501")]
    private readonly InputSlot<string> PathSaved = new();

    [Input(Guid = "c7946670-7bed-50c6-bd36-59b69be6e85e")]
    public readonly InputSlot<bool> Save = new();

}