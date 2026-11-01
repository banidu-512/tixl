using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;
using T3.Core.Resource;

namespace Lib.volume.io;

/// Loads grids from an OpenVDB .vdb file (reads uncompressed, zip and blosc-compressed data as written by Houdini, Blender or TiXL). Float grids become the volume's primary data; vector grids (velocity, color) ride along in the same set. Reloads when the file changes.
[Guid("70b83837-48fa-53ad-8b68-504bceff2848")]
internal sealed class LoadVdb : Instance<LoadVdb>
{
    [Output(Guid = "f25cded9-8ca1-5431-9399-8d1285e00703")]
    public readonly Slot<VdbVolume> Volume = new();


    public LoadVdb()
    {
        Volume.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var address = Path.GetValue(context)?.Trim();
        var gridName = GridName.GetValue(context)?.Trim();

        if (string.IsNullOrEmpty(address))
        {
            Volume.Value = null;
            _lastPath = null;
            return;
        }

        // Accept package addresses ("Examples:volumes/bunny.vdb") in addition to plain disk
        // paths, matching [LoadImage], [LoadObj] and the other asset loaders.
        var path = address;
        if (!File.Exists(path)
            && !IsDrivePath(address)
            && FileResource.TryGetFileResource(address, this, out var fileResource))
        {
            path = fileResource.AbsolutePath;
        }

        if (!File.Exists(path))
        {
            Volume.Value = null;
            _lastPath = null;
            return;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (path == _lastPath && gridName == _lastGridName && lastWrite == _lastWriteTime)
            return;

        // Remembered before loading so a broken file warns once instead of on every evaluation.
        _lastPath = path;
        _lastGridName = gridName;
        _lastWriteTime = lastWrite;

        try
        {
            var volume = VdbFile.Load(path);
            if (!string.IsNullOrEmpty(gridName))
            {
                var filtered = new VdbVolume();
                var match = volume.GetGrid(gridName);
                if (match != null)
                    filtered.Grids.Add(match);
                volume = filtered;
            }

            if (volume.Grids.Count == 0)
            {
                Log.Warning($"No supported grids found in {path}", this);
                Volume.Value = null;
            }
            else
            {
                Volume.Value = volume;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"LoadVdb failed for {path}: {e.Message}", this);
            Volume.Value = null;
        }
    }

    /// <summary>Windows drive paths are plain disk paths, never package addresses.</summary>
    private static bool IsDrivePath(string path)
        => path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private string? _lastPath;
    private string? _lastGridName;
    private DateTime _lastWriteTime;

    [Input(Guid = "e16c0dad-a228-5a6f-964b-82986746836e")]
    public readonly InputSlot<string> Path = new();

    [Input(Guid = "1e3dcc23-489a-5a54-b1af-c04d85ac3b8a")]
    public readonly InputSlot<string> GridName = new();

}