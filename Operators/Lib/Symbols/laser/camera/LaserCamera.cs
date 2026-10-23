using SharpDX.Direct3D11;
using T3.Core.DataTypes;
using T3.Core.Operator.Attributes;
using T3.Core.Operator;
using T3.Core.Operator.Interfaces;
using T3.Core.Output;
using T3.Core.Rendering;
using T3.Core.Resource;
using T3.Core.Utils;
using T3.Core.Utils.Geometry;

namespace Lib.laser.camera;

/// <summary>
/// Universal geometry-to-laser converter and a standard TiXL camera in one: like the common
/// Camera op it renders the scene connected to Scene through its own lens (selectable in the
/// output window camera dropdown, mouse-drivable), while every point/line/mesh drawn inside
/// that scene is captured and encoded into the same laser frame - dots, line segments and mesh
/// wireframes merged, with the Group/transform scopes active at draw time applied.
/// Point cloud content becomes isolated dwell dots, line strips become blank-separated segments,
/// meshes become wireframes of their unique edges.
/// The legacy direct inputs (PointBuffer / LineBuffer / Mesh) keep working and merge into the
/// same frame; a camera op connected to Camera still drives the projection, so older patches
/// behave unchanged.
/// An output-setup laser output referenced by OutputRef drives the projection instead: its
/// scanner camera (a calibration solve when present, else the manual look-at) aligns the laser
/// frame with the venue, so stage-aligned content lands on the physical walls.
/// </summary>
[Guid("2CCC2F86-FE71-42FF-B91D-812763F5C6CF")]
internal sealed class LaserCamera : Instance<LaserCamera>, ICamera, ICameraPropertiesProvider, ILaserContentSupplier
{
    [Output(Guid = "972566B4-E695-4AC5-8A35-F364E3A00CD2")]
    public readonly Slot<StructuredList> LaserPoints = new();

    [Output(Guid = "96FFBCEE-EB76-4A81-BF5A-B8C7F72E2666", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PointCount = new();

    [Output(Guid = "9A391338-75DB-4B1B-9B34-2A0D91BFD774", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int[]> SampleX = new();

    [Output(Guid = "5D962770-7DE7-4000-8A09-DA028D941DF1", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int[]> SampleY = new();

    [Output(Guid = "073058B3-38AC-4560-BFBB-A1CC42BA3F22")]
    public readonly Slot<Command> Output = new();

    [Output(Guid = "81C5A82D-0C99-40B2-9832-262C95DEDF59")]
    public readonly Slot<Object> Reference = new();

    public LaserCamera()
    {
        LaserPoints.UpdateAction += UpdateLaserPoints;
        Output.UpdateAction += UpdateOutputWithSubtree;
        Reference.UpdateAction += UpdateCameraDefinition;
        Reference.Value = this;
        LaserContentRegistry.Register(this);
    }

    /// <summary>
    /// The current laser frame for the output setup — the camera is laser content. Pulls the slot
    /// rather than reading it, so a setup preview (which nothing else pumps while the graph view is
    /// in setup mode) evaluates the camera chain on demand, like the texture content resolver does.
    /// </summary>
    StructuredList ILaserContentSupplier.GetLaserPoints(EvaluationContext context)
    {
        return LaserPoints.GetValue(context);
    }

    /// <summary>
    /// Render path: the scene is drawn through this camera's lens while its geometry is captured,
    /// then the laser frame is published. Same pipeline as the laser path below.
    /// </summary>
    private void UpdateOutputWithSubtree(EvaluationContext context)
    {
        // TiXL terminates the process on an unhandled operator exception. Scene evaluation and GPU
        // readback can fail for reasons outside our control - degrade to an empty output instead.
        try
        {
            RunPipeline(context);
        }
        catch (Exception e)
        {
            Log.Error($"LaserCamera: scene update failed, outputting empty frame - {e.Message}", this);
            SetEmptyOutput();
        }
    }

    /// <summary>
    /// Laser path: pulled by the downstream laser chain. If the scene was already rendered and
    /// converted by the render path this frame, keep that frame instead of re-evaluating the
    /// subtree; otherwise (laser-only rigs without a render chain) run the pipeline here.
    /// </summary>
    private void UpdateLaserPoints(EvaluationContext context)
    {
        if (Output.DirtyFlag.FramesSinceLastUpdate == 0 && _hasResult)
            return;

        try
        {
            RunPipeline(context);
        }
        catch (Exception e)
        {
            Log.Error($"LaserCamera: update failed, outputting empty frame - {e.Message}", this);
            SetEmptyOutput();
        }
    }

    private void RunPipeline(EvaluationContext context)
    {
        UpdateCameraDefinition(context);
        Reference.DirtyFlag.Clear();

        if (Scene.HasInputConnections)
            EvaluateSceneWithCapture(context);

        ConvertAndPublish(context);
    }

    /// <summary>
    /// Evaluates the Scene subtree with a geometry collector installed on the context, like the
    /// common Camera op installs its view matrices. Draw ops register what they draw; everything
    /// renders normally at the same time.
    /// </summary>
    private void EvaluateSceneWithCapture(EvaluationContext context)
    {
        var prevCapture = context.GeometryCapture;
        var prevWorldToCamera = context.WorldToCamera;
        var prevCameraToClipSpace = context.CameraToClipSpace;

        _capture.BeginPass();
        context.GeometryCapture = _capture;
        if (!context.BypassCameras)
        {
            context.WorldToCamera = WorldToCamera;
            context.CameraToClipSpace = CameraToClipSpace;
        }

        try
        {
            // When the laser pull is the scene's only driver (laser-only rigs, the setup view), the
            // subtree keeps its cached results: draw ops would re-register their stale buffers and
            // animated sources upstream (rotators, oscillators) would never re-run — the frame
            // would freeze on its first capture. Invalidate so the pull re-evaluates the scene.
            Scene.InvalidateGraph();
            Scene.GetValue(context);
        }
        finally
        {
            context.GeometryCapture = prevCapture;
            context.WorldToCamera = prevWorldToCamera;
            context.CameraToClipSpace = prevCameraToClipSpace;
            _capture.EndPass();
        }
    }

    private void ConvertAndPublish(EvaluationContext context)
    {
        var pointBuffer = PointBuffer.GetValue(context);
        var lineBuffer = LineBuffer.GetValue(context);
        var mesh = Mesh.GetValue(context);

        var hasPoints = pointBuffer?.Buffer != null && pointBuffer.Srv != null;
        var hasLines = lineBuffer?.Buffer != null && lineBuffer.Srv != null;
        var hasMesh = mesh?.VertexBuffer?.Buffer != null && mesh.VertexBuffer.Srv != null
                                                         && mesh.IndicesBuffer?.Buffer != null && mesh.IndicesBuffer.Srv != null;

        var sceneConnected = Scene.HasInputConnections;
        if (!sceneConnected && !hasPoints && !hasLines && !hasMesh)
        {
            SetEmptyOutput();
            return;
        }

        var updateContinuously = UpdateContinuously.GetValue(context);
        var wasTriggered = MathUtils.WasTriggered(TriggerUpdate.GetValue(context), ref _triggerUpdate);
        if (wasTriggered)
        {
            TriggerUpdate.SetTypedInputValue(false);
        }

        _printToLog = PrintToLog.GetValue(context);
        var dotDwell = DotDwellPoints.GetValue(context).Clamp(1, MaxDotDwellPoints);
        var maxEdgeStep = MathF.Max(1f, MaxEdgeStep.GetValue(context));

        // Source fingerprint - without a scene connection, any change (buffer size, connected
        // source, parameter) triggers a GPU re-read; otherwise the last frame is kept. Scene
        // capture content changes silently from the GPU's point of view, so it reconverts
        // every frame and skips the fingerprint gate.
        var pointBytes = hasPoints ? pointBuffer.Buffer.Description.SizeInBytes : 0;
        var lineBytes = hasLines ? lineBuffer.Buffer.Description.SizeInBytes : 0;
        var meshVertexBytes = hasMesh ? mesh.VertexBuffer.Buffer.Description.SizeInBytes : 0;
        var meshIndexBytes = hasMesh ? mesh.IndicesBuffer.Buffer.Description.SizeInBytes : 0;

        var fingerprint = (dotDwell, maxEdgeStep, pointBytes, lineBytes, meshVertexBytes, meshIndexBytes);
        if (!sceneConnected && !wasTriggered && !updateContinuously && _hasResult && fingerprint == _lastSources)
        {
            LaserPoints.DirtyFlag.Trigger = DirtyFlagTrigger.None;
            return;
        }

        _lastSources = fingerprint;

        var startIndex = StartIndex.GetValue(context).ClampMin(0);
        var requestedMaxCount = MaxCount.GetValue(context);

        _scratch ??= new LaserPoint[1024];
        var count = 0;
        var capturedSources = 0;
        var dotsEmitted = 0;
        var segmentsEmitted = 0;
        var edgesEmitted = 0;

        var combined = WorldToCamera * CameraToClipSpace;

        if (sceneConnected)
        {
            var captureDots = CaptureDots.GetValue(context);
            var captureLines = CaptureLines.GetValue(context);
            var captureMeshes = CaptureMeshes.GetValue(context);
            count = AppendCaptured(combined, dotDwell, maxEdgeStep, captureDots, captureLines, captureMeshes,
                                   count, out dotsEmitted, out segmentsEmitted, out edgesEmitted, out capturedSources);
        }

        if (hasPoints)
        {
            var points = ReadPointSlice(pointBuffer, _pointStaging, startIndex, requestedMaxCount);
            if (points is { Length: > 0 })
            {
                count = AppendDots(points, combined, dotDwell, count, out var directDots);
                dotsEmitted += directDots;
            }
        }

        if (hasLines)
        {
            var points = ReadPointSlice(lineBuffer, _lineStaging, startIndex, requestedMaxCount);
            if (points is { Length: > 1 })
            {
                count = AppendLineSegments(points, combined, maxEdgeStep, count, out var directSegments);
                segmentsEmitted += directSegments;
            }
        }

        if (hasMesh)
        {
            var vertexCount = Math.Min(mesh.VertexBuffer.Srv.Description.Buffer.ElementCount, MaxMeshVertices);
            var faceCount = Math.Min(mesh.IndicesBuffer.Srv.Description.Buffer.ElementCount, MaxMeshFaces);

            var vertices = _meshVertexStaging.Read<PbrVertex>(mesh.VertexBuffer.Buffer, meshVertexBytes, PbrVertex.Stride, vertexCount);
            var faces = _meshIndexStaging.Read<Int3>(mesh.IndicesBuffer.Buffer, meshIndexBytes, 3 * 4, faceCount);

            if (vertices != null && faces != null && vertices.Length > 0)
            {
                count = AppendMeshWireframe(vertices, faces, combined, maxEdgeStep, count, out var directEdges);
                edgesEmitted += directEdges;
            }
        }

        if (_printToLog)
        {
            Log.Debug($"LaserCamera: captured sources {capturedSources}, dots {dotsEmitted}, segments {segmentsEmitted}, wireframe edges {edgesEmitted}, output {count}", this);
            if (count > 0)
            {
                // First lit point with full values - reveals empty colors or collapsed
                // coordinates when the frame looks right in counts but empty on the laser.
                for (var i = 0; i < Math.Min(3, count); i++)
                {
                    var s = _scratch[i];
                    Log.Debug($"LaserCamera: sample[{i}] x={s.X} y={s.Y} r={s.R} g={s.G} b={s.B} i={s.I}", this);
                }
            }
        }

        LaserPoints.Value = Finish(count);
        _hasResult = true;
        LaserPoints.DirtyFlag.Trigger = sceneConnected || updateContinuously ? DirtyFlagTrigger.Animated : DirtyFlagTrigger.None;
    }

    #region scene capture encoding

    /// <summary>
    /// Encodes the geometry captured during the last scene pass. Each entry carries the object-to-
    /// world transform active at draw time, which is folded into the combined matrix so the existing
    /// encoders project the content exactly where it was rendered.
    /// </summary>
    private int AppendCaptured(Matrix4x4 viewClip, int dotDwell, float maxEdgeStep,
                               bool captureDots, bool captureLines, bool captureMeshes,
                               int count, out int dotsEmitted, out int segmentsEmitted, out int edgesEmitted, out int capturedSources)
    {
        dotsEmitted = 0;
        segmentsEmitted = 0;
        edgesEmitted = 0;
        capturedSources = 0;

        foreach (var entry in _capture.Entries)
        {
            if (count >= MaxOutputPoints)
                break;

            switch (entry.Kind)
            {
                case GeometryPrimitiveKind.PointCloud when !captureDots:
                case GeometryPrimitiveKind.Lines when !captureLines:
                case GeometryPrimitiveKind.Mesh when !captureMeshes:
                    continue;
            }

            try
            {
                // Buffers captured earlier in the frame can be disposed by the time we read them.
                var description = entry.VertexBuffer.Description;
                var stride = description.StructureByteStride;
                var sizeInBytes = description.SizeInBytes;
                if (stride <= 0 || sizeInBytes < stride)
                    continue;

                // pos · ObjectToWorld · WorldToCamera · CameraToClipSpace
                var capCombined = entry.ObjectToWorld * viewClip;

                switch (entry.Kind)
                {
                    case GeometryPrimitiveKind.Auto:
                    case GeometryPrimitiveKind.PointCloud:
                    case GeometryPrimitiveKind.Lines:
                    {
                        if (stride != Point.Stride)
                            continue;

                        var elementCount = sizeInBytes / stride;
                        var pair = GetCaptureAccess(stride, sizeInBytes);
                        var points = pair.Read<Point>(entry.VertexBuffer, sizeInBytes, stride, elementCount);
                        if (points is not { Length: > 0 })
                            break;

                        // Draw graphs declare their kind; Auto falls back to detecting line
                        // strips through their NaN-scale separator points.
                        var isLines = entry.Kind == GeometryPrimitiveKind.Lines
                                      || (entry.Kind == GeometryPrimitiveKind.Auto && points.Length > 1 && HasLineSeparators(points));

                        if (isLines)
                        {
                            if (!captureLines)
                                break;
                            count = AppendLineSegments(points, capCombined, maxEdgeStep, count, out var segments);
                            segmentsEmitted += segments;
                        }
                        else
                        {
                            if (!captureDots)
                                break;
                            count = AppendDots(points, capCombined, dotDwell, count, out var dots);
                            dotsEmitted += dots;
                        }
                        capturedSources++;
                        break;
                    }
                    case GeometryPrimitiveKind.Mesh:
                    {
                        if (stride != PbrVertex.Stride || entry.IndexBuffer == null)
                            continue;

                        var vertexCount = Math.Min(sizeInBytes / stride, MaxMeshVertices);
                        var indexDescription = entry.IndexBuffer.Description;
                        var faceCount = Math.Min(indexDescription.SizeInBytes / (3 * 4), MaxMeshFaces);

                        var vertexPair = GetCaptureAccess(stride, sizeInBytes);
                        var vertices = vertexPair.Read<PbrVertex>(entry.VertexBuffer, sizeInBytes, stride, vertexCount);
                        var indexPair = GetCaptureAccess(3 * 4, indexDescription.SizeInBytes);
                        var faces = indexPair.Read<Int3>(entry.IndexBuffer, indexDescription.SizeInBytes, 3 * 4, faceCount);

                        if (vertices is { Length: > 0 } && faces is { Length: > 0 })
                        {
                            count = AppendMeshWireframe(vertices, faces, capCombined, maxEdgeStep, count, out var edges);
                            edgesEmitted += edges;
                            capturedSources++;
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug($"LaserCamera: skipping captured buffer - {e.Message}", this);
            }
        }

        return count;
    }

    /// <summary>
    /// CPU-readback staging pairs for captured buffers, keyed by (stride, size) - buffers
    /// with identical layouts share a pair; enough distinct layouts fill a small pool that
    /// is recycled. StagingPair tracks per-source pipelining, so sharing stays correct.
    /// </summary>
    private StagingPair GetCaptureAccess(int stride, int sizeInBytes)
    {
        var key = ((long)stride << 32) | (uint)sizeInBytes;
        if (_captureAccess.TryGetValue(key, out var pair))
            return pair;

        if (_captureAccess.Count >= MaxCaptureAccessBuffers)
        {
            foreach (var stale in _captureAccess.Values)
            {
                stale.Dispose();
            }
            _captureAccess.Clear();
        }

        pair = new StagingPair();
        _captureAccess[key] = pair;
        return pair;
    }

    #endregion

    #region primitive encoding

    /// <summary>
    /// Point content: every projected point becomes an isolated dot - blanked jump to
    /// it, then a dwell run with the beam on. Without the blank the scanner would draw
    /// a lit line from the previous dot straight to this one.
    /// The per-point Scale authored by the ops upstream of DrawPoints is honored: sized
    /// points are drawn as small perspective-corrected circles, unsized points as dwell dots.
    /// </summary>
    private int AppendDots(Point[] points, Matrix4x4 combined, int dotDwell, int count, out int dotsEmitted)
    {
        dotsEmitted = 0;
        var haveLast = false;
        var lastX = 0;
        var lastY = 0;
        const int circleSegments = 12;

        for (var i = 0; i < points.Length && count < _scratch.Length; i++)
        {
            var p = points[i];
            if (!TryProject(p.Position, combined, out var x, out var y, out var viewZ))
                continue;

            var scale = p.Scale.X;
            var radius = (float.IsNaN(scale) || float.IsInfinity(scale) ? 0f : scale) * SizeToLaser(viewZ);
            var isCircle = radius >= 2f;
            var worstCase = 1 + (isCircle ? circleSegments + 1 : dotDwell);

            // worst case: 1 blank + dwell or circle for this dot
            if (count + worstCase > _scratch.Length && _scratch.Length < MaxOutputPoints)
            {
                var newSize = Math.Min(Math.Max(_scratch.Length * 2, count + worstCase), MaxOutputPoints);
                Array.Resize(ref _scratch, newSize);
            }

            if (!haveLast || lastX != x || lastY != y)
            {
                if (count >= _scratch.Length)
                    break;
                _scratch[count++] = LaserPoint.CreateBlanked(x, y);
                lastX = x;
                lastY = y;
                haveLast = true;
            }

            ToColor(new Vector3(p.Color.X, p.Color.Y, p.Color.Z), 1f, out var r, out var g, out var b);
            if (isCircle)
            {
                // closed circle of lit samples instead of a dwell run
                for (var k = 0; k <= circleSegments && count < _scratch.Length; k++)
                {
                    var angle = k * (MathF.PI * 2f) / circleSegments;
                    var px = (int)MathF.Round(x + MathF.Cos(angle) * radius);
                    var py = (int)MathF.Round(y + MathF.Sin(angle) * radius);
                    _scratch[count++] = new LaserPoint(px, py, r, g, b);
                }
            }
            else
            {
                for (var d = 0; d < dotDwell && count < _scratch.Length; d++)
                {
                    _scratch[count++] = new LaserPoint(x, y, r, g, b);
                }
            }

            dotsEmitted++;
        }

        return count;
    }

    /// <summary>
    /// Line content: TiXL line buffers are strips - consecutive points form segments,
    /// and breaks are marked by separator points (Scale.x = NaN/Inf, as written by
    /// PairPointsForLines) which duplicate positions, producing zero-length joints.
    /// A segment is drawn only if neither endpoint is a separator and it has real
    /// length; every drawn segment gets a blanked approach point so separate lines
    /// are never connected by lit strokes.
    /// The per-point Scale authored by the ops upstream of DrawLines is honored: sized
    /// segments are drawn as thin perspective-corrected rectangles, unsized ones as
    /// single strokes.
    /// </summary>
    private int AppendLineSegments(Point[] points, Matrix4x4 combined, float maxEdgeStep, int count, out int segmentsEmitted)
    {
        segmentsEmitted = 0;

        for (var i = 0; i < points.Length - 1 && count < _scratch.Length; i++)
        {
            var a = points[i];
            var b = points[i + 1];

            if (IsSeparator(a) || IsSeparator(b))
                continue;

            if (!TryProject(a.Position, combined, out var ax, out var ay, out var az)
                || !TryProject(b.Position, combined, out var bx, out var by, out var bz))
                continue;

            var dx = (float)(bx - ax);
            var dy = (float)(by - ay);
            var edgeLength = MathF.Sqrt(dx * dx + dy * dy);
            if (edgeLength < 1f)
                continue; // zero-length joint = strip break, nothing to draw

            var zMid = (az + bz) * 0.5f;
            var half = (a.Scale.X + b.Scale.X) * 0.5f * SizeToLaser(zMid);
            var isRectangle = half >= 1f && float.IsFinite(half);

            // A galvo needs several samples to trace a straight run - one lit point
            // pair per stroke smears into endpoint dashes at scan rates above ~200Hz.
            var steps = 1;
            if (!isRectangle)
                steps = Math.Max(1, (int)MathF.Ceiling(edgeLength / MathF.Max(1f, maxEdgeStep)));

            var needed = isRectangle ? 6 : 2 + steps;
            if (count + needed > _scratch.Length && _scratch.Length < MaxOutputPoints)
            {
                var newSize = Math.Min(Math.Max(_scratch.Length * 2, count + needed), MaxOutputPoints);
                Array.Resize(ref _scratch, newSize);
            }

            if (count + needed > _scratch.Length)
                break;

            ToColor(new Vector3(a.Color.X, a.Color.Y, a.Color.Z), 1f, out var r, out var g, out var bl);
            ToColor(new Vector3(b.Color.X, b.Color.Y, b.Color.Z), 1f, out var r2, out var g2, out var b2);

            if (isRectangle)
            {
                // thin rectangle: two parallel lines + end caps, closed
                var len = MathF.Sqrt(dx * dx + dy * dy);
                var nx = -dy / len * half;
                var ny = dx / len * half;
                var p1x = (int)MathF.Round(ax + nx);
                var p1y = (int)MathF.Round(ay + ny);
                _scratch[count++] = LaserPoint.CreateBlanked(p1x, p1y);
                _scratch[count++] = new LaserPoint(p1x, p1y, r, g, bl);
                _scratch[count++] = new LaserPoint((int)MathF.Round(bx + nx), (int)MathF.Round(by + ny), r2, g2, b2);
                _scratch[count++] = new LaserPoint((int)MathF.Round(bx - nx), (int)MathF.Round(by - ny), r2, g2, b2);
                _scratch[count++] = new LaserPoint((int)MathF.Round(ax - nx), (int)MathF.Round(ay - ny), r, g, bl);
                _scratch[count++] = new LaserPoint(p1x, p1y, r, g, bl);
            }
            else
            {
                _scratch[count++] = LaserPoint.CreateBlanked(ax, ay);
                _scratch[count++] = new LaserPoint(ax, ay, r, g, bl);
                for (var s = 1; s < steps; s++)
                {
                    var t = (float)s / steps;
                    var sr = (int)MathF.Round(r + (r2 - r) * t);
                    var sg = (int)MathF.Round(g + (g2 - g) * t);
                    var sb = (int)MathF.Round(bl + (b2 - bl) * t);
                    _scratch[count++] = new LaserPoint((int)MathF.Round(ax + dx * t), (int)MathF.Round(ay + dy * t), sr, sg, sb);
                }
            }

            segmentsEmitted++;
        }

        return count;
    }

    /// <summary>
    /// Mesh content: each unique triangle edge becomes an isolated lit run - blanked
    /// approach, then straight-line samples from start to end. Shared edges are
    /// deduplicated so interior wireframe lines don't scan twice.
    /// </summary>
    private int AppendMeshWireframe(PbrVertex[] vertices, Int3[] faces, Matrix4x4 combined, float maxEdgeStep, int count, out int edgesEmitted)
    {
        edgesEmitted = 0;
        _edgeKeys.Clear();

        for (var f = 0; f < faces.Length && count < _scratch.Length; f++)
        {
            var face = faces[f];

            for (var e = 0; e < 3; e++)
            {
                var i0 = e == 0 ? face.X : e == 1 ? face.Y : face.Z;
                var i1 = e == 0 ? face.Y : e == 1 ? face.Z : face.X;

                if (i0 < 0 || i0 >= vertices.Length || i1 < 0 || i1 >= vertices.Length)
                    continue;

                var a = Math.Min(i0, i1);
                var b = Math.Max(i0, i1);
                if (!_edgeKeys.Add(((long)a << 32) | (uint)b))
                    continue;

                if (_edgeKeys.Count > MaxEdges)
                {
                    if (_printToLog) Log.Debug($"LaserCamera: wireframe capped at {MaxEdges} edges", this);
                    return count;
                }

                if (!TryProject(vertices[i0].Position, combined, out var x0, out var y0, out _)
                    || !TryProject(vertices[i1].Position, combined, out var x1, out var y1, out _))
                    continue;

                var dx = (float)(x1 - x0);
                var dy = (float)(y1 - y0);
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                var steps = Math.Max(1, (int)MathF.Ceiling(distance / maxEdgeStep));
                steps = Math.Min(steps, MaxSamplesPerEdge);

                var needed = 2 + steps;
                if (count + needed > _scratch.Length && _scratch.Length < MaxOutputPoints)
                {
                    var newSize = Math.Min(Math.Max(_scratch.Length * 2, count + needed), MaxOutputPoints);
                    Array.Resize(ref _scratch, newSize);
                }

                if (count >= _scratch.Length)
                    break;

                _scratch[count++] = LaserPoint.CreateBlanked(x0, y0);

                ToColor(vertices[i0].ColorRgb, 1f, out var r, out var g, out var bl);
                for (var s = 0; s <= steps && count < _scratch.Length; s++)
                {
                    var t = s / (float)steps;
                    var x = (int)MathF.Round(x0 + dx * t);
                    var y = (int)MathF.Round(y0 + dy * t);
                    _scratch[count++] = new LaserPoint(x, y, r, g, bl);
                }

                edgesEmitted++;
            }
        }

        return count;
    }

    private static bool IsSeparator(in Point p)
    {
        var s = p.Scale.X;
        return float.IsNaN(s) || float.IsInfinity(s);
    }

    /// <summary>Line strips written by PairPointsForLines carry NaN-scale separator points -
    /// their presence is what distinguishes line content from point clouds at capture time.</summary>
    private static bool HasLineSeparators(Point[] points)
    {
        foreach (var p in points)
        {
            if (IsSeparator(p))
                return true;
        }
        return false;
    }

    private static bool TryProject(Vector3 position, Matrix4x4 combined, out int x, out int y, out float viewZ)
    {
        var posCs = Vector3.Transform(position, combined);
        viewZ = posCs.Z;

        // Same culling window as before: drop content behind the camera or absurdly
        // far outside the view, clamp the rest into the frame
        if (posCs.Z <= 0.001f || posCs.Z > 1000f)
        {
            x = 0;
            y = 0;
            return false;
        }

        var ndcX = posCs.X / posCs.Z;
        var ndcY = posCs.Y / posCs.Z;

        if (ndcX < -10 || ndcX > 10 || ndcY < -10 || ndcY > 10)
        {
            x = 0;
            y = 0;
            return false;
        }

        ndcX = Math.Clamp(ndcX, -1, 1);
        ndcY = Math.Clamp(ndcY, -1, 1);

        x = (int)(ndcX * 32767f);
        y = (int)(ndcY * 32767f);
        return true;
    }

    /// <summary>Perspective factor that converts a world-space size at the projected
    /// point's depth into ILDA units - same shrink-with-distance as the screen render.</summary>
    private static float SizeToLaser(float viewZ) => viewZ > 0.001f ? 32767f / viewZ : 0f;

    private static void ToColor(Vector3 colorRgb, float brightness, out int r, out int g, out int b)
    {
        r = (int)(Math.Clamp(colorRgb.X * brightness, 0f, 1f) * 65535f);
        g = (int)(Math.Clamp(colorRgb.Y * brightness, 0f, 1f) * 65535f);
        b = (int)(Math.Clamp(colorRgb.Z * brightness, 0f, 1f) * 65535f);
    }

    private StructuredList Finish(int count)
    {
        count = Math.Min(count, _scratch.Length);

        var resultArray = new LaserPoint[count];
        Array.Copy(_scratch, resultArray, count);

        PointCount.Value = count;
        PointCount.DirtyFlag.Invalidate();

        var sampleCount = Math.Min(10, count);
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

    #endregion

    #region camera

    private void UpdateCameraDefinition(EvaluationContext context)
    {
        LastObjectToWorld = context.ObjectToWorld;

        // Backwards compatibility: a connected camera op still drives the projection exactly like
        // before this op became a camera itself (including mouse-dragging, which then routes into
        // that camera's inputs).
        if (Camera.GetValue(context) is ICamera externalCamera)
        {
            _cameraDefinition = externalCamera.CameraDefinition;
            WorldToCamera = externalCamera.WorldToCamera;
            CameraToClipSpace = externalCamera.CameraToClipSpace;
            return;
        }

        // An output-setup laser output drives the projection from the venue: its scanner camera, a
        // calibration solve when present (pose + scan lens, lens shift included) and else its manual
        // look-at. The scan field is square, so the lens aspect is fixed at 1.
        var outputRef = OutputRef.GetValue(context);
        if (outputRef != Guid.Empty && ActiveSetup.FindOutput(outputRef)?.Camera is { } setupCam)
        {
            var clip = ClipPlanes.GetValue(context);
            Vector3 position;
            if (setupCam.Pose is { } pose && setupCam.Lens is { } lens)
            {
                WorldToCamera = pose.ToViewMatrix();
                CameraToClipSpace = lens.GetMatrix(1f);
                position = pose.Position;
            }
            else
            {
                var fovY = setupCam.ManualFovYDegrees.ToRadians();
                WorldToCamera = GraphicsMath.LookAtRH(setupCam.ManualPosition, setupCam.ManualTarget, new Vector3(0, 1, 0));
                CameraToClipSpace = GraphicsMath.PerspectiveFovRH(fovY, 1f, MathF.Max(0.001f, clip.X), MathF.Max(clip.Y, clip.X + 1f));
                position = setupCam.ManualPosition;
            }

            _cameraDefinition = new CameraDefinition
                                    {
                                        Position = position,
                                        Target = setupCam.Pose is { } solvedPose
                                                     ? position + Vector3.Transform(-Vector3.UnitZ, solvedPose.Orientation)
                                                     : setupCam.ManualTarget,
                                        AspectRatio = 1f,
                                        FieldOfView = setupCam.Lens?.FieldOfViewY ?? setupCam.ManualFovYDegrees.ToRadians(),
                                        NearFarClip = clip,
                                    };
            return;
        }

        var aspectRatio = AspectRatio.GetValue(context);
        if (aspectRatio < 0.0001f)
        {
            aspectRatio = (float)context.RequestedResolution.Width / context.RequestedResolution.Height;
        }

        _cameraDefinition = new CameraDefinition
                                {
                                    NearFarClip = ClipPlanes.GetValue(context),
                                    Position = Position.GetValue(context),
                                    Target = Target.GetValue(context),
                                    Up = Up.GetValue(context),
                                    AspectRatio = aspectRatio,
                                    FieldOfView = FieldOfView.GetValue(context).ToRadians(),
                                    Roll = Roll.GetValue(context),
                                };

        _cameraDefinition.BuildProjectionMatrices(out var camToClipSpace, out var worldToCamera);

        CameraJitter.ApplyFromContext(context, ref camToClipSpace);

        CameraToClipSpace = camToClipSpace;
        WorldToCamera = worldToCamera;
    }

    public CameraDefinition CameraDefinition => _cameraDefinition;
    private CameraDefinition _cameraDefinition;

    public Matrix4x4 CameraToClipSpace { get; set; }
    public Matrix4x4 WorldToCamera { get; set; }
    public Matrix4x4 LastObjectToWorld { get; set; }

    // Implement ICamera - with a connected camera op, access is delegated to it
    public Vector3 CameraPosition
    {
        get { return Camera.Value is ICamera external ? external.CameraPosition : Position.Value; }
        set
        {
            if (Camera.Value is ICamera external)
                external.CameraPosition = value;
            else
                Animator.UpdateVector3InputValue(Position, value);
        }
    }

    public Vector3 CameraTarget
    {
        get { return Camera.Value is ICamera external ? external.CameraTarget : Target.Value; }
        set
        {
            if (Camera.Value is ICamera external)
                external.CameraTarget = value;
            else
                Animator.UpdateVector3InputValue(Target, value);
        }
    }

    public float CameraRoll
    {
        get { return Camera.Value is ICamera external ? external.CameraRoll : Roll.Value; }
        set
        {
            if (Camera.Value is ICamera external)
                external.CameraRoll = value;
            else
                Animator.UpdateFloatInputValue(Roll, value);
        }
    }

    #endregion

    #region GPU readback

    /// <summary>Reads a slice of a structured Point buffer through its CPU-readback staging
    /// pair, honoring StartIndex/MaxCount. Returns null when there is nothing to read.</summary>
    private Point[] ReadPointSlice(BufferWithViews buffer, StagingPair staging, int startIndex, int requestedMaxCount)
    {
        var stride = buffer.Buffer.Description.StructureByteStride;
        var totalElements = buffer.Srv.Description.Buffer.ElementCount;
        var readCount = ComputeSlice(totalElements, startIndex, requestedMaxCount, stride);
        if (readCount <= 0)
            return null;

        return staging.Read<Point>(buffer.Buffer, buffer.Buffer.Description.SizeInBytes, stride, readCount, startIndex);
    }

    /// <summary>
    /// Pair of staging buffers for pipelined GPU readback. Frame N issues a copy into one
    /// buffer and maps what frame N-1 copied into the other, giving the GPU a full frame
    /// to complete the transfer - the CPU almost never stalls. <see cref="LastSource"/>
    /// detects layout-sharing between different buffers within a frame and falls back to a
    /// synchronous read for those, which keeps the pipelined parity per source correct.
    /// </summary>
    private sealed class StagingPair
    {
        public Buffer[] Buffers;
        public int WriteIndex;
        public bool HasPrevious;
        public Buffer LastSource;

        private void Ensure(int sourceBytes, int stride)
        {
            if (Buffers != null && Buffers[0] != null && Buffers[0].Description.SizeInBytes == sourceBytes)
                return;

            for (var i = 0; i < 2; i++)
            {
                Buffers?[i]?.Dispose();
                Buffers ??= new Buffer[2];
                Buffers[i] = new Buffer(ResourceManager.Device, new BufferDescription
                {
                    Usage = ResourceUsage.Staging,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    SizeInBytes = sourceBytes,
                    OptionFlags = ResourceOptionFlags.BufferStructured,
                    StructureByteStride = stride
                });
            }
            WriteIndex = 0;
            HasPrevious = false;
            LastSource = null;
        }

        private T[] MapRead<T>(int index, int stride, int elementCount, int offsetElements) where T : struct
        {
            SharpDX.DataStream stream = null;
            var mapped = false;
            try
            {
                ResourceManager.Device.ImmediateContext.MapSubresource(Buffers[index], 0, MapMode.Read, MapFlags.None, out stream);
                mapped = true;
                if (elementCount <= 0)
                    return Array.Empty<T>();
                stream.Position = (long)offsetElements * stride;
                return stream.ReadRange<T>(elementCount);
            }
            finally
            {
                stream?.Dispose();
                if (mapped)
                    ResourceManager.Device.ImmediateContext.UnmapSubresource(Buffers[index], 0);
            }
        }

        /// <summary>Copies the source, then reads the previous frame's copy (pipelined) or,
        /// on the first use of a source, the fresh copy (synchronous). Returns null when no
        /// completed copy exists yet - callers skip that source for the frame.</summary>
        public T[] Read<T>(Buffer source, int sourceBytes, int stride, int elementCount, int offsetElements = 0) where T : struct
        {
            try
            {
                Ensure(sourceBytes, stride);

                var pipelined = HasPrevious && ReferenceEquals(LastSource, source);
                ResourceManager.Device.ImmediateContext.CopyResource(source, Buffers[WriteIndex]);

                T[] result;
                if (pipelined)
                {
                    var readIndex = 1 - WriteIndex;
                    result = MapRead<T>(readIndex, stride, elementCount, offsetElements);
                    WriteIndex = readIndex;
                }
                else
                {
                    result = MapRead<T>(WriteIndex, stride, elementCount, offsetElements);
                    HasPrevious = true;
                    WriteIndex = 1 - WriteIndex;
                }

                LastSource = source;
                return result;
            }
            catch (Exception e)
            {
                Log.Error($"LaserCamera: GPU readback of {typeof(T).Name} failed - {e.Message}", null);
                return null;
            }
        }

        public void Dispose()
        {
            if (Buffers == null)
                return;
            foreach (var buffer in Buffers)
            {
                buffer?.Dispose();
            }
            Buffers = null;
            HasPrevious = false;
            LastSource = null;
        }
    }

    private static int ComputeSlice(int totalElements, int startIndex, int requestedMaxCount, int stride)
    {
        if (stride <= 0 || startIndex >= totalElements)
            return 0;

        var maxCount = requestedMaxCount > 0 ? requestedMaxCount : int.MaxValue;
        return (int)Math.Min((long)totalElements - startIndex, (long)maxCount);
    }

    private void SetEmptyOutput()
    {
        LaserPoints.Value = s_emptyList;
        // Animated so a transient failure self-heals on the next frame instead of
        // leaving the laser chain stuck on an empty frame forever
        LaserPoints.DirtyFlag.Trigger = DirtyFlagTrigger.Animated;
        PointCount.Value = 0;
        PointCount.DirtyFlag.Invalidate();
        Array.Clear(_sampleX);
        Array.Clear(_sampleY);
        // Fully reset so the next frame self-recovers (e.g. right after project load)
        // without needing a manual nudge on any input.
        _hasResult = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        LaserContentRegistry.Unregister(this);
        _pointStaging.Dispose();
        _lineStaging.Dispose();
        _meshVertexStaging.Dispose();
        _meshIndexStaging.Dispose();

        foreach (var pair in _captureAccess.Values)
        {
            pair.Dispose();
        }
        _captureAccess.Clear();
    }

    #endregion

    private static readonly StructuredList s_emptyList = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());

    private readonly GeometryCapture _capture = new();

    private bool _triggerUpdate;
    private bool _printToLog;
    private readonly StagingPair _pointStaging = new();
    private readonly StagingPair _lineStaging = new();
    private readonly StagingPair _meshVertexStaging = new();
    private readonly StagingPair _meshIndexStaging = new();
    private readonly Dictionary<long, StagingPair> _captureAccess = new();

    private bool _hasResult;
    private (int DotDwell, float MaxEdgeStep, int PointBytes, int LineBytes, int MeshVertexBytes, int MeshIndexBytes) _lastSources;

    private int[] _sampleX = new int[10];
    private int[] _sampleY = new int[10];
    private LaserPoint[] _scratch = new LaserPoint[1024];
    private readonly HashSet<long> _edgeKeys = new();

    /// <summary>Hard cap on generated points (1M points = 24 MB scratch) - bounds damage
    /// from huge input.</summary>
    private const int MaxOutputPoints = 1 << 20;

    /// <summary>Upper dwell limit per dot. Kept at or below the setup pipeline's default
    /// MaxStaticPoints (64) so dots survive the static-dwell limiter unblanked.</summary>
    private const int MaxDotDwellPoints = 64;

    /// <summary>Upper bound on unique wireframe edges per frame - keeps huge meshes from
    /// ballooning the laser frame.</summary>
    private const int MaxEdges = 65536;

    /// <summary>Upper bound on lit samples per single edge; bounds the cost of a tiny
    /// MaxEdgeStep against a full-field edge.</summary>
    private const int MaxSamplesPerEdge = 256;

    /// <summary>Mesh readback size caps - bigger meshes are truncated, not skipped.</summary>
    private const int MaxMeshVertices = 131072;
    private const int MaxMeshFaces = 131072;

    /// <summary>Distinct buffer layouts kept as CPU-readback twins for captured content.</summary>
    private const int MaxCaptureAccessBuffers = 32;

    [Input(Guid = "419DECC0-D0C2-475B-B618-2DB7B371F144")]
    public readonly InputSlot<BufferWithViews> PointBuffer = new();

    [Input(Guid = "5479A43A-F274-4E5D-9BE7-FE214B547616")]
    public readonly InputSlot<BufferWithViews> LineBuffer = new();

    [Input(Guid = "A0C2C2C8-F247-4B9D-8993-730D502B6ACA")]
    public readonly InputSlot<MeshBuffers> Mesh = new();

    [Input(Guid = "4F55D55B-B51D-4A29-8788-E1AE21A80960")]
    public readonly InputSlot<float> MaxEdgeStep = new(4000f);

    [Input(Guid = "0C48ED58-1242-4F8B-869A-7396CB055931")]
    public readonly InputSlot<int> DotDwellPoints = new(8);

    [Input(Guid = "F151B1B0-B820-47F6-B2CF-BE1C2B339DCA")]
    public readonly InputSlot<Object> Camera = new();

    /// <summary>A laser output of the active output setup whose scanner camera drives the projection;
    /// empty = the Camera input / the manual camera inputs below apply.</summary>
    [Input(Guid = "E7A2C41D-9B58-4C0F-A3D6-8E41F2B7C5A9")]
    public readonly InputSlot<Guid> OutputRef = new();

    [Input(Guid = "154B095B-D6FC-4283-B2B5-86778F6E68DD")]
    public readonly InputSlot<int> StartIndex = new();

    [Input(Guid = "8C823C87-4D14-43E3-843D-D452E008B1AE")]
    public readonly InputSlot<int> MaxCount = new();

    [Input(Guid = "950943BF-935A-484E-9667-16180784ADC2")]
    public readonly InputSlot<bool> TriggerUpdate = new();

    /// <summary>Defaults to on: a laser rig must be live the moment a project loads —
    /// GPU content changes without changing the buffer's byte size, so the change-gated
    /// readback would freeze the frame otherwise.</summary>
    [Input(Guid = "E5C90440-378B-49D3-8C5C-72DCD65A2CDA")]
    public readonly InputSlot<bool> UpdateContinuously = new(true);

    [Input(Guid = "73241494-C686-4013-96C0-EF40D5932F7F")]
    public readonly InputSlot<bool> PrintToLog = new();

    [Input(Guid = "6475CA9C-6ED6-4659-87D2-A602813C823C")]
    public readonly InputSlot<Command> Scene = new();

    [Input(Guid = "1C513C59-CC3A-4A8E-B963-D21658CC9D43")]
    public readonly InputSlot<Vector3> Position = new(new Vector3(0, 0, 2.4141f));

    [Input(Guid = "42BCABAD-7B49-48D1-95A3-9B0FC2E4EB91")]
    public readonly InputSlot<Vector3> Target = new();

    [Input(Guid = "1683DC5C-A279-44D5-9FE9-8EF5134067C9")]
    public readonly InputSlot<Vector3> Up = new(new Vector3(0, 1, 0));

    [Input(Guid = "9CE8F8F8-25A0-44C0-952C-D74EA4E22CE5")]
    public readonly InputSlot<float> FieldOfView = new(45f);

    [Input(Guid = "5544EC6E-0F90-4853-9AF0-69DA0F213C22")]
    public readonly InputSlot<float> Roll = new();

    [Input(Guid = "D62B1D2F-8B69-4F58-A8CB-9F9EB89A831D")]
    public readonly InputSlot<float> AspectRatio = new(-1f);

    [Input(Guid = "3F7695E3-6FE2-4034-8337-B4A98C3A4489")]
    public readonly InputSlot<Vector2> ClipPlanes = new(new Vector2(0.01f, 1000f));

    [Input(Guid = "A46A63D2-7359-4942-A19D-F1EF15E68895")]
    public readonly InputSlot<bool> CaptureDots = new(true);

    [Input(Guid = "ED234344-726F-41DE-90A1-5338F7903676")]
    public readonly InputSlot<bool> CaptureLines = new(true);

    [Input(Guid = "4F1F1654-6F6B-464A-8A43-74000C19F2EF")]
    public readonly InputSlot<bool> CaptureMeshes = new(true);
}
