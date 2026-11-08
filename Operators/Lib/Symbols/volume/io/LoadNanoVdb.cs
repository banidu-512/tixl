using System;
using System.Numerics;
using System.Threading;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.Logging;
using T3.Core.Resource;
using T3.Graphics.Compat;

namespace Lib.volume.io;

/// Loads a NanoVDB .nvdb file and uploads it as a structured uint buffer, ready for [NanoVdbRender] or a custom HLSL traversal. Unlike [LoadVdb] this skips the CPU grid entirely, so a pre-converted cache plays without parsing or rebuilding anything. Outputs mirror [VolumeToNanoVdb], and WorldMin/Max frame the grid.
[Guid("fc358fd5-f6ba-4971-9d40-2090e51aaa71")]
internal sealed class LoadNanoVdb : Instance<LoadNanoVdb>
{
    [Output(Guid = "4e4faeca-b648-4704-98b8-f0785bf6953d")]
    public readonly Slot<BufferWithViews> Buffer = new();

    [Output(Guid = "fb21f061-79e7-486c-b08c-dcce202daf34")]
    public readonly Slot<ShaderResourceView> ShaderResourceView = new();

    [Output(Guid = "0b6b0f47-6272-4f4e-ad92-a90bf1d9c911")]
    public readonly Slot<Vector4> TreeOffsets = new();

    [Output(Guid = "676f0880-d184-40ad-9df9-f05c49c06b20")]
    public readonly Slot<float> VoxelCount = new();

    [Output(Guid = "ff56e7b9-b7de-4d27-b025-5a70033803d8")]
    public readonly Slot<float> VoxelSize = new();

    [Output(Guid = "8ec10b96-380d-4043-8a15-b4dec1b3daf7")]
    public readonly Slot<string> GridName = new();

    [Output(Guid = "cd870b7c-8aa0-4761-8273-9c0d1ca36008")]
    public readonly Slot<Vector3> WorldMin = new();

    [Output(Guid = "30f4d753-17da-4610-b704-6cceaf334407")]
    public readonly Slot<Vector3> WorldMax = new();

    public LoadNanoVdb()
    {
        // Any output can be the one a consumer pulls, so each must trigger Update.
        Buffer.UpdateAction = Update;
        ShaderResourceView.UpdateAction = Update;
        TreeOffsets.UpdateAction = Update;
        VoxelCount.UpdateAction = Update;
        VoxelSize.UpdateAction = Update;
        GridName.UpdateAction = Update;
        WorldMin.UpdateAction = Update;
        WorldMax.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var address = Path.GetValue(context)?.Trim();

        if (string.IsNullOrEmpty(address))
        {
            CancelPending();
            ClearOutputs();
            _lastPath = null;
            return;
        }

        // Accept package addresses in addition to plain disk paths, like [LoadVdb] does.
        var path = address;
        if (!File.Exists(path)
            && !IsDrivePath(address)
            && FileResource.TryGetFileResource(address, this, out var fileResource))
        {
            path = fileResource.AbsolutePath;
        }

        if (!File.Exists(path))
        {
            CancelPending();
            ClearOutputs();
            _lastPath = null;
            return;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (path != _lastPath || lastWrite != _lastWriteTime)
        {
            // Remembered before loading so a broken file schedules once instead of on every evaluation.
            _lastPath = path;
            _lastWriteTime = lastWrite;
            StartLoad(path);
        }

        PublishCompletedLoad();
    }

    /// <summary>
    /// A production cache can be hundreds of megabytes, so the read happens on a worker thread and
    /// only the finished grid is published here, on the evaluation thread. GPU resources are never
    /// created off the evaluation thread: fine on D3D11, not guaranteed on Vulkan.
    /// </summary>
    private void StartLoad(string path)
    {
        CancelPending();

        _loadTask = Task.Run(() =>
                                 {
                                     LoadResult result;
                                     try
                                     {
                                         var length = new FileInfo(path).Length;
                                         if (length > MaxFileBytes)
                                         {
                                             result = new LoadResult
                                                          {
                                                              Path = path,
                                                              Error = $"file is {length / 1024 / 1024} MB, above the {MaxFileBytes / 1024 / 1024} MB limit for the GPU path",
                                                          };
                                         }
                                         else
                                         {
                                             var grid = NanoVdbBuffer.LoadFile(path);
                                             result = grid.TryValidate(out var invalid)
                                                          ? new LoadResult { Grid = grid, Path = path }
                                                          : new LoadResult { Path = path, Error = invalid };
                                         }
                                     }
                                     catch (Exception e)
                                     {
                                         result = new LoadResult { Path = path, Error = e.Message };
                                     }

                                     // Wake the graph: the slot itself is clean, so without this the
                                     // finished grid would only be published if something else
                                     // invalidated the chain. Bump the version atomically rather than
                                     // calling Invalidate(), which also rewrites the pass-scoped tick
                                     // owned by the evaluation thread.
                                     Interlocked.Increment(ref Buffer.DirtyFlag.SourceVersion);
                                     return result;
                                 });
    }

    private void PublishCompletedLoad()
    {
        if (_loadTask is not { IsCompleted: true })
            return;

        var task = _loadTask;
        _loadTask = null;

        var result = task.Result;
        if (result.Error != null)
        {
            Log.Warning($"LoadNanoVdb failed for {result.Path}: {result.Error}", this);
            ClearOutputs();
            return;
        }

        var grid = result.Grid;
        if (grid == null)
        {
            ClearOutputs();
            return;
        }

        try
        {
            var info = grid.ReadInfo();

            var byteCount = (grid.Buffer.Length + 3) / 4 * 4;
            using var data = new DataStream(byteCount, true, true);
            data.Write(grid.Buffer, 0, grid.Buffer.Length);
            data.Position = 0;

            // Reuse one BufferWithViews so the SRV is only recreated when the buffer is replaced.
            ResourceManager.SetupStructuredBuffer(data, byteCount, sizeof(uint), ref _bufferWithViews.Buffer);
            ResourceManager.CreateStructuredBufferSrv(_bufferWithViews.Buffer, ref _bufferWithViews.Srv);

            var treeStart = NanoVdbBuffer.GridDataSize;
            Buffer.Value = _bufferWithViews;
            ShaderResourceView.Value = _bufferWithViews.Srv;
            TreeOffsets.Value = new Vector4(BitConverter.ToUInt64(grid.Buffer, treeStart),
                                            BitConverter.ToUInt64(grid.Buffer, treeStart + 8),
                                            BitConverter.ToUInt64(grid.Buffer, treeStart + 16),
                                            BitConverter.ToUInt64(grid.Buffer, treeStart + 24));
            VoxelCount.Value = info.VoxelCount;
            VoxelSize.Value = info.VoxelSize;
            GridName.Value = info.Name;
            WorldMin.Value = info.WorldMin;
            WorldMax.Value = info.WorldMax;
        }
        catch (Exception e)
        {
            Log.Warning($"LoadNanoVdb failed to upload {result.Path}: {e.Message}", this);
            ClearOutputs();
        }
    }

    private void ClearOutputs()
    {
        Buffer.Value = null;
        ShaderResourceView.Value = null;
        TreeOffsets.Value = Vector4.Zero;
        VoxelCount.Value = 0;
        VoxelSize.Value = 0;
        GridName.Value = string.Empty;
        WorldMin.Value = Vector3.Zero;
        WorldMax.Value = Vector3.Zero;
    }

    private void CancelPending()
    {
        // A running read cannot be aborted mid-parse; dropping the task discards its result.
        _loadTask = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bufferWithViews.Dispose();

        base.Dispose(disposing);
    }

    private sealed class LoadResult
    {
        public NanoVdbBuffer Grid;
        public string Error;
        public string Path = string.Empty;
    }

    /// <summary>Windows drive paths are plain disk paths, never package addresses.</summary>
    private static bool IsDrivePath(string path)
        => path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private const long MaxFileBytes = 1L << 30;

    private string _lastPath;
    private DateTime _lastWriteTime;
    private Task<LoadResult> _loadTask;
    private readonly BufferWithViews _bufferWithViews = new();

    [Input(Guid = "69a0c6d6-243c-4cf2-b022-c557955660fd")]
    public readonly InputSlot<string> Path = new();
}
