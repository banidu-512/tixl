using SharpDX.Direct3D11;
using T3.Core.DataTypes;
using T3.Core.Operator.Attributes;
using T3.Core.Operator;
using T3.Core.Operator.Interfaces;
using T3.Core.Rendering;
using T3.Core.Resource;
using T3.Core.Utils;
using Utilities = T3.Core.Utils.Utilities;

namespace Lib.laser.camera;

[Guid("2CCC2F86-FE71-42FF-B91D-812763F5C6CF")]
internal sealed class LaserCamera : Instance<LaserCamera>
{
    [Output(Guid = "972566B4-E695-4AC5-8A35-F364E3A00CD2")]
    public readonly Slot<StructuredList> LaserPoints = new();

    [Output(Guid = "96FFBCEE-EB76-4A81-BF5A-B8C7F72E2666", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PointCount = new();

    [Output(Guid = "9A391338-75DB-4B1B-9B34-2A0D91BFD774", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int[]> SampleX = new();

    [Output(Guid = "5D962770-7DE7-4000-8A09-DA028D941DF1", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int[]> SampleY = new();

    public LaserCamera()
    {
        LaserPoints.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var obj = Camera.GetValue(context);
        if (obj == null || obj is not ICamera camera)
        {
            SetEmptyOutput();
            return;
        }

        var pointBuffer = PointBuffer.GetValue(context);
        if (pointBuffer?.Buffer == null || pointBuffer.Srv == null)
        {
            SetEmptyOutput();
            return;
        }

        var useAsync = UseAsync.GetValue(context);
        var updateContinuously = UpdateContinuously.GetValue(context);
        var printToLog = PrintToLog.GetValue(context);
        _printToLog = printToLog;
        var wasTriggered = MathUtils.WasTriggered(TriggerUpdate.GetValue(context), ref _triggerUpdate);

        if (wasTriggered)
        {
            TriggerUpdate.SetTypedInputValue(false);
        }

        var startIndex = StartIndex.GetValue(context).ClampMin(0);
        var requestedMaxCount = MaxCount.GetValue(context);
        var resolution = Resolution.GetValue(context);

        var stride = pointBuffer.Buffer.Description.StructureByteStride;
        if (stride <= 0)
        {
            SetEmptyOutput();
            return;
        }

        var totalElements = pointBuffer.Srv.Description.Buffer.ElementCount;
        if (startIndex >= totalElements)
        {
            SetEmptyOutput();
            return;
        }

        var maxCount = requestedMaxCount > 0 ? requestedMaxCount : int.MaxValue;
        var outputCount = (int)Math.Min(totalElements - startIndex, (long)maxCount);

        if (useAsync && (updateContinuously || wasTriggered))
        {
            _pendingStartIndex = startIndex;
            _pendingMaxCount = outputCount;
            _pendingStride = stride;
            _pendingCamera = camera;
            _pendingResolution = resolution;

            _bufferReader.InitiateRead(pointBuffer.Buffer, totalElements, stride, OnAsyncReadComplete);
            _bufferReader.Update();
            LaserPoints.DirtyFlag.Trigger = updateContinuously ? DirtyFlagTrigger.Animated : DirtyFlagTrigger.None;
            return;
        }

        var d3DDevice = ResourceManager.Device;
        var immediateContext = d3DDevice.ImmediateContext;

        if (wasTriggered
            || updateContinuously
            || _bufferWithViewsCpuAccess == null
            || _bufferWithViewsCpuAccess.Buffer == null
            || _bufferWithViewsCpuAccess.Buffer.Description.SizeInBytes != pointBuffer.Buffer.Description.SizeInBytes
            || _bufferWithViewsCpuAccess.Buffer.Description.StructureByteStride != stride)
        {
            try
            {
                if (_bufferWithViewsCpuAccess != null)
                    Utilities.Dispose(ref _bufferWithViewsCpuAccess.Buffer);

                _bufferWithViewsCpuAccess ??= new BufferWithViews();

                if (_bufferWithViewsCpuAccess.Buffer == null ||
                    _bufferWithViewsCpuAccess.Buffer.Description.SizeInBytes != pointBuffer.Buffer.Description.SizeInBytes)
                {
                    _bufferWithViewsCpuAccess.Buffer?.Dispose();
                    var bufferDesc = new BufferDescription
                    {
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                        SizeInBytes = pointBuffer.Buffer.Description.SizeInBytes,
                        OptionFlags = ResourceOptionFlags.BufferStructured,
                        StructureByteStride = stride,
                        CpuAccessFlags = CpuAccessFlags.Read
                    };
                    _bufferWithViewsCpuAccess.Buffer = new Buffer(ResourceManager.Device, bufferDesc);
                }
            }
            catch (Exception e)
            {
                Log.Error("LaserCamera: Failed to setup structured buffer " + e.Message, this);
                SetEmptyOutput();
                return;
            }

            ResourceManager.CreateStructuredBufferSrv(_bufferWithViewsCpuAccess.Buffer, ref _bufferWithViewsCpuAccess.Srv);
            immediateContext.CopyResource(pointBuffer.Buffer, _bufferWithViewsCpuAccess.Buffer);
        }

        immediateContext.MapSubresource(_bufferWithViewsCpuAccess.Buffer, 0, MapMode.Read, MapFlags.None, out var sourceStream);

        using (sourceStream)
        {
            sourceStream.Position = (long)startIndex * stride;

            var points = outputCount > 0
                             ? sourceStream.ReadRange<Point>(outputCount)
                             : Array.Empty<Point>();

            var result = ProjectPoints(points, camera, resolution);
            LaserPoints.Value = result;
        }

        immediateContext.UnmapSubresource(_bufferWithViewsCpuAccess.Buffer, 0);
        LaserPoints.DirtyFlag.Trigger = updateContinuously ? DirtyFlagTrigger.Animated : DirtyFlagTrigger.None;
    }

    private static readonly StructuredList s_emptyList = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());

    private void SetEmptyOutput()
    {
        LaserPoints.Value = s_emptyList;
        PointCount.Value = 0;
    }

    private void OnAsyncReadComplete(StructuredBufferReadAccess.ReadRequestItem item, IntPtr dataPointer, SharpDX.DataStream stream)
    {
        using (stream)
        {
            if (_pendingCamera == null)
            {
                LaserPoints.Value = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());
                return;
            }

            stream.Position = (long)_pendingStartIndex * _pendingStride;
            var points = _pendingMaxCount > 0 ? stream.ReadRange<Point>(_pendingMaxCount) : Array.Empty<Point>();

            var result = ProjectPoints(points, _pendingCamera, _pendingResolution);
            LaserPoints.Value = result;
            LaserPoints.DirtyFlag.Invalidate();
        }
    }

    private StructuredList ProjectPoints(Point[] points, ICamera camera, Int2 resolution)
    {
        if (points.Length == 0)
        {
            PointCount.Value = 0;
            return s_emptyList;
        }

        var worldToCamera = camera.WorldToCamera;
        var cameraToClipSpace = camera.CameraToClipSpace;
        var combined = worldToCamera * cameraToClipSpace;

        var laserPointCount = 0;
        var culledDepth = 0;
        var culledFrustum = 0;

        // Resize buffer if needed
        if (_tempPoints.Length < points.Length)
        {
            _tempPoints = new LaserPoint[points.Length];
        }

        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            var posWs = p.Position;

            var posCs = Vector3.Transform(posWs, combined);

            // More permissive depth check for testing
            if (posCs.Z <= 0.001f || posCs.Z > 1000f)
            {
                culledDepth++;
                continue;
            }

            var ndcX = posCs.X / posCs.Z;
            var ndcY = posCs.Y / posCs.Z;

            if (ndcX < -10 || ndcX > 10 || ndcY < -10 || ndcY > 10)
            {
                culledFrustum++;
                continue;
            }

            // Clamp to valid range for DAC
            ndcX = Math.Clamp(ndcX, -1, 1);
            ndcY = Math.Clamp(ndcY, -1, 1);

            var laserX = (int)(ndcX * 32767f);
            var laserY = (int)(ndcY * 32767f);

            var color = p.Color;
            var r = (int)Math.Clamp(color.X * 65535, 0, 65535);
            var g = (int)Math.Clamp(color.Y * 65535, 0, 65535);
            var b = (int)Math.Clamp(color.Z * 65535, 0, 65535);

            _tempPoints[laserPointCount++] = new LaserPoint(laserX, laserY, r, g, b);
        }

        if (_printToLog) Log.Debug($"LaserCamera: Input {points.Length} points, Output {laserPointCount} points (Culled: {culledDepth} depth, {culledFrustum} frustum)", this);

        // Create result array of exact size
        var resultArray = new LaserPoint[laserPointCount];
        Array.Copy(_tempPoints, resultArray, laserPointCount);
        PointCount.Value = laserPointCount;
        PointCount.DirtyFlag.Invalidate();

        // Update sample arrays for inspection
        var sampleCount = Math.Min(10, laserPointCount);
        for (var i = 0; i < sampleCount; i++)
        {
            _sampleX[i] = resultArray[i].X;
            _sampleY[i] = resultArray[i].Y;
        }
        SampleX.Value = _sampleX;
        SampleX.DirtyFlag.Invalidate();
        SampleY.Value = _sampleY;
        SampleY.DirtyFlag.Invalidate();

        return new StructuredList<LaserPoint>(resultArray);
    }

    private bool _triggerUpdate;
    private bool _printToLog;
    private BufferWithViews _bufferWithViewsCpuAccess = new();
    private readonly StructuredBufferReadAccess _bufferReader = new();
    private int _pendingStartIndex;
    private int _pendingMaxCount;
    private int _pendingStride;
    private ICamera _pendingCamera;
    private Int2 _pendingResolution;
    private int[] _sampleX = new int[10];
    private int[] _sampleY = new int[10];
    private LaserPoint[] _tempPoints = new LaserPoint[1024];

    [Input(Guid = "419DECC0-D0C2-475B-B618-2DB7B371F144")]
    public readonly InputSlot<BufferWithViews> PointBuffer = new();

    [Input(Guid = "F151B1B0-B820-47F6-B2CF-BE1C2B339DCA")]
    public readonly InputSlot<Object> Camera = new();

    [Input(Guid = "633130FA-67A9-4C8A-8C69-DEB4ED198A50")]
    public readonly InputSlot<Int2> Resolution = new(new Int2(1920, 1080));

    [Input(Guid = "154B095B-D6FC-4283-B2B5-86778F6E68DD")]
    public readonly InputSlot<int> StartIndex = new();

    [Input(Guid = "8C823C87-4D14-43E3-843D-D452E008B1AE")]
    public readonly InputSlot<int> MaxCount = new();

    [Input(Guid = "950943BF-935A-484E-9667-16180784ADC2")]
    public readonly InputSlot<bool> TriggerUpdate = new();

    [Input(Guid = "E5C90440-378B-49D3-8C5C-72DCD65A2CDA")]
    public readonly InputSlot<bool> UpdateContinuously = new();

    [Input(Guid = "1E63BA21-B821-4AA4-9612-8F620523A72E")]
    public readonly InputSlot<bool> UseAsync = new(false);

    [Input(Guid = "73241494-C686-4013-96C0-EF40D5932F7F")]
    public readonly InputSlot<bool> PrintToLog = new(false);
}
