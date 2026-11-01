using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.Tools;
using T3.Core.DataTypes.Volume.IO;
using T3.Core.Logging;
using T3.Graphics.Compat;

namespace Lib.volume.gpu;

/// Flattens the volume into a NanoVDB buffer - the GPU-ready linear grid layout - and uploads it as a structured uint buffer for custom HLSL traversal. Feed the ShaderResourceView output to [NanoVdbRender] or your own shader. The Vector4 outputs carry the tree offsets relative to the buffer start: leaf / lower / upper / root.
[Guid("564e17a3-182b-5dbb-b402-088f0e717f9d")]
internal sealed class VolumeToNanoVdb : Instance<VolumeToNanoVdb>
{
    [Output(Guid = "7b63ace0-265a-54c5-90d9-239e84f7b317")]
    public readonly Slot<BufferWithViews> Buffer = new();

    [Output(Guid = "11c12656-918e-5780-a877-8042575ff67a")]
    public readonly Slot<ShaderResourceView> ShaderResourceView = new();

    [Output(Guid = "2464ac6a-e5cb-5d0d-8b51-4e916600002c")]
    public readonly Slot<Vector4> TreeOffsets = new();

    [Output(Guid = "ef5eb577-22de-57b2-a0b7-273a40b64cd4")]
    public readonly Slot<float> VoxelCount = new();

    [Output(Guid = "f6a0d0bb-7bca-5e16-a0e4-6d13f3c1ee75")]
    public readonly Slot<float> VoxelSize = new();


    public VolumeToNanoVdb()
    {
        // Any output can be the one a consumer pulls, so each must trigger Update.
        Buffer.UpdateAction = Update;
        ShaderResourceView.UpdateAction = Update;
        TreeOffsets.UpdateAction = Update;
        VoxelCount.UpdateAction = Update;
        VoxelSize.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var volume = Volume.GetValue(context);
        var grid = volume?.GetFloatGrid();
        if (grid == null)
        {
            ClearOutputs();
            _lastVolume = null;
            return;
        }

        if (ReferenceEquals(volume, _lastVolume))
            return;

        // Remembered before the attempt so a failing volume logs once instead of on every evaluation.
        _lastVolume = volume;

        try
        {
            var nanoBuffer = NanoVdbBuffer.FromGrid(grid);
            var summary = nanoBuffer.ReadSummary();

            var byteCount = (nanoBuffer.Buffer.Length + 3) / 4 * 4;
            using var data = new DataStream(byteCount, true, true);
            data.Write(nanoBuffer.Buffer, 0, nanoBuffer.Buffer.Length);
            data.Position = 0;

            // Reuse one BufferWithViews so the SRV and UAV are only recreated when the buffer
            // itself is replaced; a fresh object every evaluation leaked one view per rebuild.
            ResourceManager.SetupStructuredBuffer(data, byteCount, sizeof(uint), ref _bufferWithViews.Buffer);
            ResourceManager.CreateStructuredBufferSrv(_bufferWithViews.Buffer, ref _bufferWithViews.Srv);

            // Tree node offsets, relative to the tree data start (first 32 bytes of TreeData).
            var treeStart = NanoVdbBuffer.GridDataSize;
            var leafOffset = BitConverter.ToUInt64(nanoBuffer.Buffer, treeStart);
            var lowerOffset = BitConverter.ToUInt64(nanoBuffer.Buffer, treeStart + 8);
            var upperOffset = BitConverter.ToUInt64(nanoBuffer.Buffer, treeStart + 16);
            var rootOffset = BitConverter.ToUInt64(nanoBuffer.Buffer, treeStart + 24);

            Buffer.Value = _bufferWithViews;
            ShaderResourceView.Value = _bufferWithViews.Srv;
            TreeOffsets.Value = new Vector4(leafOffset, lowerOffset, upperOffset, rootOffset);
            VoxelCount.Value = summary.VoxelCount;
            VoxelSize.Value = grid.Transform.VoxelSize;
        }
        catch (Exception e)
        {
            Log.Warning($"VolumeToNanoVdb failed: {e.Message}", this);
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
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bufferWithViews.Dispose();

        base.Dispose(disposing);
    }

    private VdbVolume? _lastVolume;
    private readonly BufferWithViews _bufferWithViews = new();

    [Input(Guid = "8a677502-fd4d-5284-ab06-fd659490a4fa")]
    public readonly InputSlot<VdbVolume> Volume = new();

}