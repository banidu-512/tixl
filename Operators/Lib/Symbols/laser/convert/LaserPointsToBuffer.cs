using System;
using System.Numerics;
using T3.Core.DataTypes;

namespace Lib.laser.convert;

[Guid("624747a6-e974-4650-b60c-a7bdb078a104")]
internal sealed class LaserPointsToBuffer : Instance<LaserPointsToBuffer>
{
    [Output(Guid = "61bb56cf-8b97-468b-8811-84dd3fdc3649")]
    public readonly Slot<BufferWithViews> Buffer = new();

    [Output(Guid = "fcee2f07-b573-4dd0-a365-36bad0cce3c5")]
    public readonly Slot<int> PointCount = new();

    public LaserPointsToBuffer()
    {
        Buffer.UpdateAction += Update;
        PointCount.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        // Any exception (GPU, network, corrupted upstream) must not escape to TiXL's evaluator.
        try
        {
            var inputPoints = LaserPoints.GetValue(context);
            if (inputPoints is not StructuredList<LaserPoint> laserPoints
                || laserPoints.TypedElements == null
                || laserPoints.NumElements == 0)
            {
                PointCount.Value = 0;
                PointCount.DirtyFlag.Invalidate();
                Buffer.Value = null;
                return;
            }

            var scale = Scale.GetValue(context);
            var source = laserPoints.TypedElements;
            // Glitchy input (e.g. malformed network frames) must not allocate a gigantic GPU buffer.
            var count = Math.Min(laserPoints.NumElements, MaxPoints);

            // Build Point array with current scale applied. The array always has exactly the length
            // of the GPU buffer, so the upload matches the allocation (avoids native access
            // violations); the tail beyond the frame holds separators, which are never drawn.
            EnsureCapacity(count);
            for (var i = count; i < _filledCount; i++)
                _points[i] = T3.Core.DataTypes.Point.Separator();

            _filledCount = count;

            for (var i = 0; i < count; i++)
            {
                var lp = source[i];
                // Use the full ctor so untouched fields match the Point conventions (F1/F2 = 1,
                // Scale = 1, valid Orientation). Zero-initialized FX/Scale fields collapse points
                // to zero size in DrawPoints when UsePointScale or ScaleFX is enabled.
                _points[i] = new T3.Core.DataTypes.Point
                                 {
                                     Position = new Vector3(lp.X * scale, lp.Y * scale, 0),
                                     Color = new Vector4(
                                         lp.R / 65535f,
                                         lp.G / 65535f,
                                         lp.B / 65535f,
                                         lp.I / 65535f
                                     )
                                 };
            }

            // Upload to GPU buffer
            if (!UpdateBuffer(_points.Length))
            {
                PointCount.Value = 0;
                PointCount.DirtyFlag.Invalidate();
                Buffer.Value = null;
                return;
            }

            PointCount.Value = count;
            PointCount.DirtyFlag.Invalidate();
            Buffer.Value = _bufferWithViews;
        }
        catch (Exception e)
        {
            Log.Error($"LaserPointsToBuffer: update failed – {e.Message}", this);
            PointCount.Value = 0;
            PointCount.DirtyFlag.Invalidate();
            Buffer.Value = null;
        }
    }

    /// <summary>
    /// Laser frames change length from frame to frame. The buffer only grows (with headroom) and shrinks
    /// after the content has stayed small for a while, so a changing point count does not create and
    /// destroy GPU resources every frame.
    /// </summary>
    private void EnsureCapacity(int count)
    {
        var current = _points?.Length ?? 0;
        if (count > current)
        {
            Resize(Math.Min(MaxPoints, Math.Max(MinCapacity, count + count / 4)));
            _slackFrames = 0;
            return;
        }

        if (current <= MinCapacity || count * 4 > current)
        {
            _slackFrames = 0;
            return;
        }

        if (++_slackFrames < ShrinkAfterFrames)
            return;

        _slackFrames = 0;
        Resize(Math.Max(MinCapacity, count + count / 4));
    }

    private void Resize(int capacity)
    {
        _points = new T3.Core.DataTypes.Point[capacity];
        var separator = T3.Core.DataTypes.Point.Separator();
        Array.Fill(_points, separator);
        _filledCount = 0;
    }

    private bool UpdateBuffer(int count)
    {
        // Any GPU failure here must never escape Update: TiXL terminates on unhandled operator
        // exceptions. Create an empty structured buffer, then upload via UpdateSubresource -
        // never via CreateBuffer-with-initial-data, whose native copy has crashed the process
        // (uncatchable access violation inside the driver).
        try
        {
            var totalBytes = count * T3.Core.DataTypes.Point.Stride;
            if (_buffer == null || _buffer.Description.SizeInBytes != totalBytes)
            {
                ReleaseBuffer();
                ResourceManager.SetupStructuredBuffer(totalBytes, T3.Core.DataTypes.Point.Stride, ref _buffer);
                if (_buffer == null)
                {
                    Log.Error("Failed to create laser point buffer", this);
                    return false;
                }

                ResourceManager.CreateStructuredBufferSrv(_buffer, ref _bufferWithViews.Srv);
                ResourceManager.CreateStructuredBufferUav(_buffer, UnorderedAccessViewBufferFlags.None, ref _bufferWithViews.Uav);
                if (_bufferWithViews.Srv == null || _bufferWithViews.Uav == null)
                {
                    Log.Error("Failed to create views for laser point buffer", this);
                    ReleaseBuffer();
                    return false;
                }

                _bufferWithViews.Buffer = _buffer;
            }

            ResourceManager.Device.ImmediateContext.UpdateSubresource(_points, _buffer);
            // Laser-frame data drawn back into a scene is a preview - mark the buffer so a
            // LaserCamera capturing that scene never captures its own output (feedback echo).
            T3.Core.Rendering.GeometryCapture.MarkExcludedFromCapture(_buffer);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to upload laser point buffer: {e.Message} - releasing GPU resources, dropping this frame.", this);
            ReleaseBuffer();
            return false;
        }
    }

    private void ReleaseBuffer()
    {
        _bufferWithViews.Srv?.Dispose();
        _bufferWithViews.Uav?.Dispose();
        _buffer?.Dispose();
        _bufferWithViews.Srv = null;
        _bufferWithViews.Uav = null;
        _bufferWithViews.Buffer = null;
        _buffer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ReleaseBuffer();

        base.Dispose(disposing);
    }

    private Buffer _buffer;
    private readonly BufferWithViews _bufferWithViews = new();
    private T3.Core.DataTypes.Point[] _points;
    private int _filledCount;
    private int _slackFrames;

    private const int MinCapacity = 256;
    private const int ShrinkAfterFrames = 120;

    /// <summary>Hard cap on converted points (1M points = 64 MB GPU buffer) - bounds damage from corrupt input.</summary>
    private const int MaxPoints = 1 << 20;

    [Input(Guid = "55ed02f0-2cd5-4566-b15a-580fddda2484")]
    public readonly InputSlot<StructuredList> LaserPoints = new();

    [Input(Guid = "112189ef-6929-4cee-b54b-c2dfafa12ee5")]
    public readonly InputSlot<float> Scale = new();
}
