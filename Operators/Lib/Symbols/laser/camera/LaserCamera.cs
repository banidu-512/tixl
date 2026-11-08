using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using T3.Graphics.Compat;
using T3.Core.DataTypes;
using T3.Core.Operator.Attributes;
using T3.Core.Operator;
using T3.Core.Operator.Interfaces;
using T3.Core.Output;
using T3.Core.Rendering;
using T3.Core.Resource;
using T3.Core.Utils;
using T3.Core.Utils.Geometry;
using Lib.Utils;
using static Lib.Utils.LaserProjection;

namespace Lib.laser.camera;

/// <summary>
/// Universal geometry-to-laser converter and a standard TiXL camera in one: like the common
/// Camera op it has its own lens (selectable in the output window camera dropdown, mouse-drivable),
/// and every point/line/mesh drawn inside the Scene connected to it is captured and encoded into
/// a laser frame - dots, line segments and mesh wireframes merged, with the Group/transform scopes
/// active at draw time applied. Point cloud content becomes isolated dwell dots, line strips become
/// blank-separated segments, meshes become wireframes of their unique edges.
/// The projection is driven either by the op's own aim (Position/Target or Use Rotation angles,
/// FieldOfView) or, when a CameraReference is connected, by that camera's lens - its own aim inputs
/// are then ignored. OutputRef only routes the frame to a laser output of the output setup, whose
/// driver streams it to the bound device.
/// Like SendToOutput it is a supplier of the output setup: it registers itself, lists as CONTENT
/// there and can be frozen with Update. The Command output renders NOTHING: laser data leaves only
/// through the LaserPoints conversion chain, and the only thing the command path carries is the
/// gizmo. The scene is captured exactly once per laser pump, on the pump's budget - never per
/// rendered frame - which is what keeps a rig of cameras cheap.
/// </summary>
[Guid("2CCC2F86-FE71-42FF-B91D-812763F5C6CF")]
internal sealed class LaserCamera : Instance<LaserCamera>, ICamera, ICameraPropertiesProvider, ILaserContentSupplier, IStatusProvider, ITransformable,
                                    ICameraViewGizmo
{
    [Output(Guid = "073058B3-38AC-4560-BFBB-A1CC42BA3F22")]
    public readonly TransformCallbackSlot<Command> Output = new();

    [Output(Guid = "972566B4-E695-4AC5-8A35-F364E3A00CD2")]
    public readonly Slot<StructuredList> LaserPoints = new();

    [Output(Guid = "81C5A82D-0C99-40B2-9832-262C95DEDF59")]
    public readonly TransformCallbackSlot<Object> Reference = new();

    public LaserCamera()
    {
        LaserPoints.UpdateAction += UpdateLaserPoints;
        Output.UpdateAction += UpdateOutputWithSubtree;
        Output.TransformableOp = this;
        Reference.UpdateAction += UpdateCameraDefinition;
        Reference.TransformableOp = this;
        Reference.Value = this;
        LaserContentRegistry.Register(this);
    }

    /// <summary>
    /// The current laser frame for the output setup - the camera is laser content. Pulls the slot
    /// rather than reading it, so a setup preview (which nothing else pumps while the graph view is
    /// in setup mode) evaluates the camera chain on demand, like the texture content resolver does.
    /// </summary>
    StructuredList ILaserContentSupplier.GetLaserPoints(EvaluationContext context)
    {
        return LaserPoints.GetValue(context);
    }

    bool ILaserContentSupplier.GetUpdateEnabled(EvaluationContext context) => Update.GetValue(context);

    IInputSlot ILaserContentSupplier.UpdateInput => Update;

    IStatusProvider.StatusLevel IStatusProvider.GetStatusLevel()
    {
        if (_lastError != null)
            return IStatusProvider.StatusLevel.Error;

        if (!Scene.HasInputConnections)
            return IStatusProvider.StatusLevel.Warning;

        if (CameraReference.HasInputConnections && !_hasCameraReference)
            return IStatusProvider.StatusLevel.Warning;

        return OutputRef.Value == Guid.Empty
                   ? IStatusProvider.StatusLevel.Notice
                   : IStatusProvider.StatusLevel.Success;
    }

    string? IStatusProvider.GetStatusMessage()
    {
        if (_lastError != null)
            return $"Update failed, streaming an empty frame: {_lastError}";

        if (!Scene.HasInputConnections)
            return "No Scene connected, nothing to capture.";

        if (CameraReference.HasInputConnections && !_hasCameraReference)
            return "Camera reference is not usable - using this op's own aim.";

        if (OutputRef.Value == Guid.Empty)
            return "Not routed to a laser output. Drop it on one in the Output Setup.";

        return Update.Value ? $"{_lastPointCount} points" : $"Frozen at {_lastPointCount} points";
    }

    /// <summary>
    /// The Command output renders nothing - deliberately. The scene is never re-drawn through this
    /// camera into a render pass: wiring this output into a graph would otherwise re-run the whole
    /// scene (and its GPU readbacks) once per rendered frame per camera, outside the pump's budget.
    /// The only thing that flows through here is the gizmo callback (this slot is a transform
    /// callback slot); the laser frame leaves exclusively through the LaserPoints conversion chain,
    /// pulled by the output setup's driver at pump rate.
    /// </summary>
    private void UpdateOutputWithSubtree(EvaluationContext context)
    {
    }

    /// <summary>
    /// The one pipeline path: pulled by the downstream laser chain (the output setup's driver at
    /// pump rate, or a setup preview on demand). Evaluates the scene with the capture collector
    /// installed, converts what was drawn into this camera's laser frame, and publishes it.
    /// </summary>
    private void UpdateLaserPoints(EvaluationContext context)
    {
        // Frozen: the last frame keeps streaming and the scene is not touched. The first frame is
        // always built, or a camera saved with Update off would never output anything.
        if (_hasFrame && !Update.GetValue(context))
            return;

        // TiXL terminates the process on an unhandled operator exception. Scene evaluation and GPU
        // readback can fail for reasons outside our control - degrade to an empty frame instead.
        try
        {
            RunPipeline(context);
            _lastError = null;
        }
        catch (Exception e)
        {
            ReportFailure(e);
            SetEmptyOutput();
        }
    }

    /** A persistent failure repeats at pump rate; the log gets it once per message and interval. */
    private void ReportFailure(Exception e)
    {
        var now = Stopwatch.GetTimestamp();
        if (e.Message == _lastError && now - _lastErrorLogTimestamp < Stopwatch.Frequency * 5)
            return;

        _lastError = e.Message;
        _lastErrorLogTimestamp = now;
        Log.Error($"LaserCamera: update failed, outputting empty frame - {e.Message}", this);
    }

    private void RunPipeline(EvaluationContext context)
    {
        UpdateCameraDefinition(context);
        Reference.DirtyFlag.Clear();

        CapturedScene? captured = null;
        if (Scene.HasInputConnections)
            captured = EvaluateSceneWithCapture(context);

        ConvertAndPublish(context, captured);
    }

    /// <summary>
    /// Evaluates the Scene subtree with a geometry collector installed on the context, like the
    /// common Camera op installs its view matrices. Draw ops register what they draw; nothing is
    /// rasterized at the same time. The result is read to the CPU once and SHARED with every
    /// other laser camera wired to the same scene subtree for the rest of the frame: a rig
    /// of N cameras on one scene must not evaluate it (and read its buffers back) N times - that
    /// is the difference between one scene pass per pump and N scene passes per frame.
    /// </summary>
    private CapturedScene? EvaluateSceneWithCapture(EvaluationContext context)
    {
        Instance? source = null;
        if (Scene.TryGetFirstConnection(out var connected))
            source = connected.Parent;

        var now = Stopwatch.GetTimestamp();
        var tick = DirtyFlag.GlobalTicks;
        if (source != null
            && _sharedCaptures.TryGetValue(source, out var shared)
            && shared.IsReusable(tick, now))
        {
            // Another camera already ran and read this exact scene subtree THIS FRAME (the driver's
            // camera pulls all happen within one frame, however long they take) - reuse its capture
            // (the camera-independent part) and project it below.
            return shared.Scene;
        }

        var prevCapture = context.GeometryCapture;
        var prevWorldToCamera = context.WorldToCamera;
        var prevCameraToClipSpace = context.CameraToClipSpace;
        var prevCaptureOnlyDraws = context.CaptureOnlyDraws;

        _capture.BeginPass();
        context.GeometryCapture = _capture;
        // Capture needs the buffers, not pixels: draw ops register their geometry and skip the
        // rasterization. Rasterizing here would paint into whatever render target is bound (the
        // visible viewport during the pump) with THIS camera's matrices - the scene then flashes
        // between its own render and the capture pass.
        context.CaptureOnlyDraws = true;
        if (!context.BypassCameras)
        {
            context.WorldToCamera = WorldToCamera;
            context.CameraToClipSpace = CameraToClipSpace;
        }

        var evalStart = Stopwatch.GetTimestamp();
        try
        {
            // When the laser pull is the scene's only driver (laser-only rigs, the setup view), the
            // subtree keeps its cached results: draw ops would re-register their stale buffers and
            // animated sources upstream (rotators, oscillators) would never re-run - the frame
            // would freeze on its first capture. Invalidate so the pull re-evaluates the scene.
            Scene.InvalidateGraph();
            Scene.GetValue(context);
        }
        finally
        {
            context.GeometryCapture = prevCapture;
            context.WorldToCamera = prevWorldToCamera;
            context.CameraToClipSpace = prevCameraToClipSpace;
            context.CaptureOnlyDraws = prevCaptureOnlyDraws;
            _capture.EndPass();
            _lastEvalMs = (Stopwatch.GetTimestamp() - evalStart) * 1000.0 / Stopwatch.Frequency;
        }

        var readStart = Stopwatch.GetTimestamp();
        ReadCapture(_scene);
        _lastReadMs = (Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency;

        // Weakly keyed: a scene whose op is gone takes its capture with it.
        if (source != null)
            _sharedCaptures.AddOrUpdate(source, new SharedCapture(_scene, _scene.Generation, tick, now));

        return _scene;
    }

    private void ConvertAndPublish(EvaluationContext context, CapturedScene? captured)
    {
        if (!Scene.HasInputConnections)
        {
            SetEmptyOutput();
            return;
        }

        var dotDwell = DotDwellPoints.GetValue(context).Clamp(1, MaxDotDwellPoints);
        var maxEdgeStep = MaxEdgeStep.GetValue(context);
        maxEdgeStep = float.IsFinite(maxEdgeStep) ? MathF.Max(1f, maxEdgeStep) : DefaultMaxEdgeStep;

        // Transport budget: the driver derives the bound plugs' point cap (a CITP feed truncates at
        // 12,000), so the encoders stop there instead of producing points the encode drops.
        var cap = LaserContentRegistry.PointBudget;
        if (cap <= 0 || cap > MaxOutputPoints)
            cap = MaxOutputPoints;

        // Scene capture content changes silently from the GPU's point of view, so it reconverts
        // every frame.
        var combined = WorldToCamera * CameraToClipSpace;
        var count = AppendCaptured(captured, combined, dotDwell, maxEdgeStep, cap, out var stats);

        if (PrintToLog.GetValue(context))
            LogDiagnostics(captured, count, stats);

        LaserPoints.Value = Finish(count);
        _lastPointCount = count;
        _hasFrame = true;
        LaserPoints.DirtyFlag.Trigger = DirtyFlagTrigger.Animated;
    }

    /// <summary>First lit points, world and projected bounds and the camera setup: says whether a
    /// collapsed frame comes from the capture, the projection or the aim.</summary>
    private void LogDiagnostics(CapturedScene? captured, int count, in EncodeStats stats)
    {
        Log.Debug($"LaserCamera: captured sources {stats.Sources}, dots {stats.Dots}, segments {stats.Segments}, wireframe edges {stats.Edges}, output {count} (scene {_lastEvalMs:0.0}ms, readback {_lastReadMs:0.0}ms, data age {_lastDataAgeMs:0.0}ms, {_lastStaleReads} stale reads)",
                  this);

        // Logged even for a collapsed frame: a broken aim (unresolved reference, NaN projection) is
        // exactly what an empty output needs to be told apart from a scene that captured nothing.
        var def = _cameraDefinition;
        Log.Debug($"LaserCamera: camera pos {def.Position} target {def.Target} fov {def.FieldOfView:0.0} aspect {def.AspectRatio:0.00}"
                  + $" view[{WorldToCamera.M11:0.00} {WorldToCamera.M22:0.00} {WorldToCamera.M33:0.00} | {WorldToCamera.M41:0.00} {WorldToCamera.M42:0.00} {WorldToCamera.M43:0.00}]"
                  + $" proj[{CameraToClipSpace.M11:0.000} {CameraToClipSpace.M22:0.000}]"
                  + (_hasCameraReference ? " (aim from camera reference)" : " (own aim)"), this);

        if (count <= 0)
            return;

        for (var i = 0; i < Math.Min(3, count); i++)
        {
            var s = _scratch[i];
            Log.Debug($"LaserCamera: sample[{i}] x={s.X} y={s.Y} r={s.R} g={s.G} b={s.B} i={s.I}", this);
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var i = 0; i < count; i++)
        {
            minX = Math.Min(minX, _scratch[i].X);
            maxX = Math.Max(maxX, _scratch[i].X);
            minY = Math.Min(minY, _scratch[i].Y);
            maxY = Math.Max(maxY, _scratch[i].Y);
        }

        if (captured != null)
        {
            for (var s = 0; s < captured.Count; s++)
            {
                var source = captured.Sources[s];
                if (source.Kind == GeometryPrimitiveKind.Mesh)
                {
                    var step = Math.Max(1, source.VertexCount / 4096);
                    LogWorldBounds("mesh", source.VertexCount, step, source.ObjectToWorld, i => source.Vertices[i].Position);
                }
                else
                {
                    LogWorldBounds("points", source.PointCount, 1, source.ObjectToWorld, i => source.Points[i].Position);
                }
            }
        }

        Log.Debug($"LaserCamera: frame bounds x[{minX}..{maxX}] y[{minY}..{maxY}]", this);
    }

    private void LogWorldBounds(string label, int length, int step, Matrix4x4 objectToWorld, Func<int, Vector3> positionAt)
    {
        if (length <= 0)
            return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < length; i += step)
        {
            var world = Vector3.Transform(positionAt(i), objectToWorld);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        Log.Debug($"LaserCamera: {label} world bounds x[{min.X:0.00}..{max.X:0.00}] y[{min.Y:0.00}..{max.Y:0.00}] z[{min.Z:0.00}..{max.Z:0.00}] over {length} elements",
                  this);
    }

    #region scene capture encoding

    /// <summary>
    /// One captured scene source, already read to the CPU: the primitive kind, the object-to-world
    /// transform active at draw time, and the data. The capture is camera-independent - every
    /// camera watching the same scene projects this same data through its own lens. Mesh edges are
    /// deduplicated once here, not per camera. The arrays are reused across captures, so only the
    /// leading <c>*Count</c> elements are valid.
    /// </summary>
    private sealed class CapturedSource
    {
        public GeometryPrimitiveKind Kind;
        public Matrix4x4 ObjectToWorld;
        public Point[] Points = [];        // dot clouds and line strips
        public int PointCount;
        public PbrVertex[] Vertices = [];  // mesh wireframes
        public int VertexCount;
        public int[] EdgeIndices = [];     // mesh wireframes: unique edge vertex-index pairs (i0, i1, ...)
        public int EdgeIndexCount;
    }

    /// <summary>The sources of one capture pass. Pooled by the owning camera: a pass allocates nothing
    /// once the scene has been seen, so <see cref="Generation"/> tells a sharing camera whether the
    /// scene it found still holds the capture it was published with.</summary>
    private sealed class CapturedScene
    {
        public readonly List<CapturedSource> Sources = [];
        public int Count;
        public int Generation;

        public void Reset()
        {
            Count = 0;
            Generation++;
        }

        public CapturedSource Rent()
        {
            if (Count == Sources.Count)
                Sources.Add(new CapturedSource());

            return Sources[Count++];
        }
    }

    private sealed class SharedCapture(CapturedScene scene, int generation, int tick, long timestamp)
    {
        public readonly CapturedScene Scene = scene;

        /// <summary>A shared capture is reused by the other cameras of the SAME frame only (keyed on the
        /// global frame tick), and never older than a fraction of a second: the minimized keep-alive
        /// path pumps without advancing the frame counter, so the age bound keeps its lasers
        /// re-capturing. A pooled scene that was captured into again no longer matches.</summary>
        public bool IsReusable(int currentTick, long currentTimestamp)
        {
            return tick == currentTick
                   && Scene.Generation == generation
                   && currentTimestamp - timestamp < ShareMaxAgeTicks;
        }
    }

    /// <summary>Scene subtree op to its latest capture. Weak on the key so a deleted scene frees its
    /// capture. Render-thread only, like every laser cache.</summary>
    private static readonly ConditionalWeakTable<Instance, SharedCapture> _sharedCaptures = new();

    private static readonly long ShareMaxAgeTicks = Stopwatch.Frequency / 4; // 250 ms

    private struct EncodeStats
    {
        public int Sources;
        public int Dots;
        public int Segments;
        public int Edges;
    }

    /// <summary>
    /// Reads every buffer the scene pass registered into CPU arrays - the expensive part, done ONCE
    /// per scene and frame and shared by all cameras. Each drawn buffer gets its own staging
    /// pair, keyed by its position in the capture (the same draw order every frame), and the read is
    /// ALWAYS one pump interval behind: copy this frame, map the previous frame's copy. A galvo
    /// frame ~33ms old is invisible, and the CPU never stalls on the GPU the way a same-frame map
    /// would - which matters because scene buffers can be recreated between evaluations, defeating
    /// any same-frame pipelining. Reads are bounded to the drawn slice, since whole buffers would
    /// turn their zero-filled tails into phantom points.
    /// </summary>
    private void ReadCapture(CapturedScene scene)
    {
        scene.Reset();
        var entries = _capture.Entries;
        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            var entry = entries[entryIndex];
            try
            {
                var description = entry.VertexBuffer.Description;
                var stride = description.StructureByteStride;
                var sizeInBytes = description.SizeInBytes;
                if (stride <= 0 || sizeInBytes < stride)
                    continue;

                switch (entry.Kind)
                {
                    case GeometryPrimitiveKind.Auto:
                    case GeometryPrimitiveKind.PointCloud:
                    case GeometryPrimitiveKind.Lines:
                        ReadPointSource(scene, entry, entryIndex, stride, sizeInBytes);
                        break;

                    case GeometryPrimitiveKind.Mesh:
                        ReadMeshSource(scene, entry, entryIndex, stride, sizeInBytes);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Debug($"LaserCamera: skipping captured buffer - {e.Message}", this);
            }
        }

        SummarizeStaging(entries.Count * 2);
        ReleaseIdleStaging(entries.Count * 2);
    }

    /// <summary>The oldest data this pass served and how many reads found the GPU copy unfinished and kept the
    /// previous data - what the output lags behind the scene by, shown by PrintToLog.</summary>
    private void SummarizeStaging(int usedSlots)
    {
        var oldest = 0.0;
        var stale = 0;
        for (var slot = 0; slot < Math.Min(usedSlots, _captureStaging.Length); slot++)
        {
            var pair = _captureStaging[slot];
            if (pair == null)
                continue;

            oldest = Math.Max(oldest, pair.LastAgeMs);
            if (pair.LastReadWasStale)
                stale++;
        }

        _lastDataAgeMs = oldest;
        _lastStaleReads = stale;
    }

    private void ReadPointSource(CapturedScene scene, GeometryCapture.Entry entry, int entryIndex, int stride, int sizeInBytes)
    {
        if (stride != Point.Stride)
            return;

        var capacity = sizeInBytes / stride;
        var firstElement = Math.Max(0, entry.FirstElement);
        var elementCount = entry.ElementCount >= 0
                               ? Math.Min(entry.ElementCount, Math.Max(0, capacity - firstElement))
                               : capacity;
        if (elementCount <= 0)
            return;

        var pointCount = GetCaptureStaging(entryIndex * 2).Read(entry.VertexBuffer, sizeInBytes, stride, elementCount, firstElement, out Point[] points);
        if (pointCount <= 0)
            return;

        // Draw graphs declare their kind; Auto falls back to detecting line
        // strips through their NaN-scale separator points.
        var isLines = entry.Kind == GeometryPrimitiveKind.Lines
                      || (entry.Kind == GeometryPrimitiveKind.Auto && pointCount > 1 && HasLineSeparators(points, pointCount));

        var source = scene.Rent();
        source.Kind = isLines ? GeometryPrimitiveKind.Lines : GeometryPrimitiveKind.PointCloud;
        source.ObjectToWorld = entry.ObjectToWorld;
        source.Points = points;
        source.PointCount = pointCount;
    }

    private void ReadMeshSource(CapturedScene scene, GeometryCapture.Entry entry, int entryIndex, int stride, int sizeInBytes)
    {
        if (stride != PbrVertex.Stride || entry.IndexBuffer == null)
            return;

        var vertexCount = Math.Min(sizeInBytes / stride, MaxMeshVertices);
        var indexDescription = entry.IndexBuffer.Description;
        var faceCount = Math.Min(indexDescription.SizeInBytes / FaceStride, MaxMeshFaces);

        // The dispatch knows its index count - faces beyond it are the buffer's
        // uninitialized tail and draw stray edges.
        if (entry.ElementCount >= 0)
            faceCount = Math.Min(faceCount, entry.ElementCount / 3);

        if (faceCount <= 0)
            return;

        var readVertices = GetCaptureStaging(entryIndex * 2).Read(entry.VertexBuffer, sizeInBytes, stride, vertexCount, 0, out PbrVertex[] vertices);
        var readFaces = GetCaptureStaging(entryIndex * 2 + 1).Read(entry.IndexBuffer, indexDescription.SizeInBytes, FaceStride, faceCount, 0, out Int3[] faces);
        if (readVertices <= 0 || readFaces <= 0)
            return;

        var source = scene.Rent();
        source.Kind = GeometryPrimitiveKind.Mesh;
        source.ObjectToWorld = entry.ObjectToWorld;
        source.Vertices = vertices;
        source.VertexCount = readVertices;
        CollectUniqueMeshEdges(faces, readFaces, vertices, readVertices, source);
    }

    /// <summary>
    /// Encodes the shared capture into this camera's laser frame: each source is projected through
    /// THIS camera's lens (its ObjectToWorld transform folded in) - dots become isolated dwell dots,
    /// line strips blank-separated segments, meshes wireframes of their unique edges. Every captured
    /// kind is encoded: a laser frame is exactly what the scene contains, and the capture collector
    /// is the only place that decides what is drawn (Draw ops and their capture inputs).
    /// </summary>
    private int AppendCaptured(CapturedScene? captured, Matrix4x4 viewClip, int dotDwell, float maxEdgeStep, int cap, out EncodeStats stats)
    {
        stats = default;
        var count = 0;
        if (captured == null)
            return count;

        for (var s = 0; s < captured.Count && count < cap; s++)
        {
            var source = captured.Sources[s];

            // pos · ObjectToWorld · WorldToCamera · CameraToClipSpace
            var objectToClip = source.ObjectToWorld * viewClip;

            switch (source.Kind)
            {
                case GeometryPrimitiveKind.Lines:
                    count = AppendLineSegments(source.Points, source.PointCount, objectToClip, maxEdgeStep, cap, count, out var segments);
                    stats.Segments += segments;
                    stats.Sources++;
                    break;

                case GeometryPrimitiveKind.PointCloud:
                    count = AppendDots(source.Points, source.PointCount, objectToClip, dotDwell, cap, count, out var dots);
                    stats.Dots += dots;
                    stats.Sources++;
                    break;

                case GeometryPrimitiveKind.Mesh:
                    count = AppendMeshWireframe(source.Vertices, source.EdgeIndices, source.EdgeIndexCount, objectToClip, maxEdgeStep, cap, count, out var edges);
                    stats.Edges += edges;
                    stats.Sources++;
                    break;
            }
        }

        return count;
    }

    /// <summary>
    /// One staging pair per captured draw slot (keyed by the entry's position in the capture - the
    /// same draw order every frame), so identical-layout buffers never share a pair and every
    /// source pipelines independently. Pairs are created on first use and hold GPU memory only after
    /// their first read.
    /// </summary>
    private StagingPair GetCaptureStaging(int slot)
    {
        return _captureStaging[slot] ??= new StagingPair();
    }

    /// <summary>Frees the staging buffers of draw slots a shrunken scene no longer uses, once they
    /// have been idle for a while - a scene that flickers between sizes keeps its buffers.</summary>
    private void ReleaseIdleStaging(int usedSlots)
    {
        for (var slot = 0; slot < _captureStaging.Length; slot++)
        {
            var pair = _captureStaging[slot];
            if (pair == null)
                continue;

            if (slot < usedSlots)
            {
                pair.IdlePasses = 0;
            }
            else if (++pair.IdlePasses > IdlePassesBeforeRelease)
            {
                pair.Dispose();
                _captureStaging[slot] = null;
            }
        }
    }

    #endregion

    #region primitive encoding

    /// <summary>Makes room for <paramref name="needed"/> more points after <paramref name="count"/>.
    /// False when that would exceed the cap: a primitive is emitted whole or not at all, never as
    /// an unclosed circle or a half-drawn stroke.</summary>
    private bool Reserve(int count, int needed, int cap)
    {
        if (count + needed > cap)
            return false;

        if (count + needed > _scratch.Length)
            Array.Resize(ref _scratch, Math.Min(Math.Max(_scratch.Length * 2, count + needed), cap));

        return true;
    }

    /// <summary>
    /// Point content: every projected point becomes an isolated dot - blanked jump to
    /// it, then a dwell run with the beam on. Without the blank the scanner would draw
    /// a lit line from the previous dot straight to this one.
    /// The per-point Scale (a multiplier - see DotRadiusBase) sizes the dot: scaled-up
    /// points draw as small circles for brightness, unsized points as dwell dots.
    /// </summary>
    private int AppendDots(Point[] points, int pointCount, Matrix4x4 objectToClip, int dotDwell, int cap, int count, out int dotsEmitted)
    {
        dotsEmitted = 0;
        pointCount = Math.Min(pointCount, points.Length);
        var haveLast = false;
        var lastX = 0;
        var lastY = 0;

        for (var i = 0; i < pointCount; i++)
        {
            var p = points[i];
            if (!TryProject(p.Position, objectToClip, out var x, out var y))
                continue;

            // Point Scale is a multiplier (1 = normal size), not a world-space radius - feeding it
            // straight into the perspective conversion turned every default-sized dot into a
            // third-of-the-field circle.
            var radius = Math.Clamp(FiniteOrZero(p.Scale.X) * DotRadiusBase, 0f, DotRadiusMax);
            var isCircle = radius >= 2f;
            var sameSpot = haveLast && lastX == x && lastY == y;
            var needed = (sameSpot ? 0 : 1) + (isCircle ? CircleSegments + 1 : dotDwell);
            if (!Reserve(count, needed, cap))
                break;

            if (!sameSpot)
            {
                _scratch[count++] = LaserPoint.CreateBlanked(x, y);
                lastX = x;
                lastY = y;
                haveLast = true;
            }

            ToColor(p.Color.X, p.Color.Y, p.Color.Z, 1f, out var r, out var g, out var b);
            if (isCircle)
            {
                // closed circle of lit samples instead of a dwell run
                for (var k = 0; k <= CircleSegments; k++)
                {
                    var angle = k * (MathF.PI * 2f) / CircleSegments;
                    var px = (int)MathF.Round(x + MathF.Cos(angle) * radius);
                    var py = (int)MathF.Round(y + MathF.Sin(angle) * radius);
                    _scratch[count++] = new LaserPoint(px, py, r, g, b);
                }
            }
            else
            {
                for (var d = 0; d < dotDwell; d++)
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
    /// length. A segment that starts where the previous one ended continues it, so a
    /// polyline costs one blanked approach instead of one per segment; anything else gets
    /// its own blanked approach so separate lines are never connected by lit strokes.
    /// The per-point Scale authored by the ops upstream of DrawLines is honored: sized
    /// segments are drawn as thin perspective-corrected rectangles, unsized ones as
    /// single strokes.
    /// </summary>
    private int AppendLineSegments(Point[] points, int pointCount, Matrix4x4 objectToClip, float maxEdgeStep, int cap, int count, out int segmentsEmitted)
    {
        segmentsEmitted = 0;
        pointCount = Math.Min(pointCount, points.Length);
        var haveStroke = false;
        var strokeEndX = 0;
        var strokeEndY = 0;

        for (var i = 0; i < pointCount - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];

            if (IsSeparator(a) || IsSeparator(b))
                continue;

            // Clipped, not clamped: a segment that leaves the frame is cut at the border instead
            // of both ends being pinned to it (which bent lines along the edge - the morphing
            // seen when content approaches the border), and one entirely outside is dropped.
            if (!TryProjectSegment(a.Position, b.Position, objectToClip, out var ax, out var ay, out var bx, out var by))
                continue;

            var dx = bx - ax;
            var dy = by - ay;
            var edgeLength = MathF.Sqrt(dx * dx + dy * dy);
            if (edgeLength < 1f)
                continue; // zero-length joint = strip break, nothing to draw

            // Same multiplier semantics as the dot radius: Scale.X scales a small base width,
            // it is not a world-space size (default-sized lines drew as field-wide rectangles).
            var half = Math.Clamp(FiniteOrZero((a.Scale.X + b.Scale.X) * 0.5f) * LineHalfWidthBase, 0f, LineHalfWidthMax);
            var isRectangle = half >= 1f;

            ToColor(a.Color.X, a.Color.Y, a.Color.Z, 1f, out var r, out var g, out var bl);
            ToColor(b.Color.X, b.Color.Y, b.Color.Z, 1f, out var r2, out var g2, out var b2);

            if (isRectangle)
            {
                // thin rectangle: two parallel lines + end caps, closed
                if (!Reserve(count, 6, cap))
                    break;

                var nx = -dy / edgeLength * half;
                var ny = dx / edgeLength * half;
                var p1x = (int)MathF.Round(ax + nx);
                var p1y = (int)MathF.Round(ay + ny);
                _scratch[count++] = LaserPoint.CreateBlanked(p1x, p1y);
                _scratch[count++] = new LaserPoint(p1x, p1y, r, g, bl);
                _scratch[count++] = new LaserPoint((int)MathF.Round(bx + nx), (int)MathF.Round(by + ny), r2, g2, b2);
                _scratch[count++] = new LaserPoint((int)MathF.Round(bx - nx), (int)MathF.Round(by - ny), r2, g2, b2);
                _scratch[count++] = new LaserPoint((int)MathF.Round(ax - nx), (int)MathF.Round(ay - ny), r, g, bl);
                _scratch[count++] = new LaserPoint(p1x, p1y, r, g, bl);
                haveStroke = false;
            }
            else
            {
                // A galvo needs several samples to trace a straight run - one lit point
                // pair per stroke smears into endpoint dashes at scan rates above ~200Hz.
                var steps = Math.Max(1, (int)MathF.Ceiling(edgeLength / maxEdgeStep));
                steps = Math.Min(steps, MaxSamplesPerEdge);

                var startX = (int)MathF.Round(ax);
                var startY = (int)MathF.Round(ay);
                var continuesStroke = haveStroke && startX == strokeEndX && startY == strokeEndY;
                if (!Reserve(count, (continuesStroke ? 0 : 2) + steps, cap))
                    break;

                if (!continuesStroke)
                {
                    _scratch[count++] = LaserPoint.CreateBlanked(startX, startY);
                    _scratch[count++] = new LaserPoint(startX, startY, r, g, bl);
                }

                for (var s = 1; s < steps; s++)
                {
                    var t = (float)s / steps;
                    var sr = (int)MathF.Round(r + (r2 - r) * t);
                    var sg = (int)MathF.Round(g + (g2 - g) * t);
                    var sb = (int)MathF.Round(bl + (b2 - bl) * t);
                    _scratch[count++] = new LaserPoint((int)MathF.Round(ax + dx * t), (int)MathF.Round(ay + dy * t), sr, sg, sb);
                }

                // The end point closes the stroke - it is also where a continuing segment resumes.
                strokeEndX = (int)MathF.Round(bx);
                strokeEndY = (int)MathF.Round(by);
                _scratch[count++] = new LaserPoint(strokeEndX, strokeEndY, r2, g2, b2);
                haveStroke = true;
            }

            segmentsEmitted++;
        }

        return count;
    }

    /// <summary>
    /// Mesh content: each unique triangle edge becomes an isolated lit run - blanked
    /// approach, then straight-line samples from start to end. The edges were deduplicated
    /// once when the capture was read (shared by all cameras), so this only projects.
    /// </summary>
    private int AppendMeshWireframe(PbrVertex[] vertices, int[] edgeIndices, int edgeIndexCount, Matrix4x4 objectToClip, float maxEdgeStep, int cap, int count,
                                    out int edgesEmitted)
    {
        edgesEmitted = 0;
        if (edgeIndices == null || vertices == null)
            return count;

        for (var e = 0; e < edgeIndexCount - 1 && e + 1 < edgeIndices.Length; e += 2)
        {
            var i0 = edgeIndices[e];
            var i1 = edgeIndices[e + 1];
            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length)
                continue;

            // Clipped, not clamped: an edge that leaves the frame is cut at the border; one
            // entirely outside the camera is not drawn at all.
            if (!TryProjectSegment(vertices[i0].Position, vertices[i1].Position, objectToClip,
                                   out var x0, out var y0, out var x1, out var y1))
                continue;

            var dx = x1 - x0;
            var dy = y1 - y0;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var steps = Math.Min(Math.Max(1, (int)MathF.Ceiling(distance / maxEdgeStep)), MaxSamplesPerEdge);

            // blanked approach + steps + 1 lit samples
            if (!Reserve(count, steps + 2, cap))
                break;

            _scratch[count++] = LaserPoint.CreateBlanked((int)MathF.Round(x0), (int)MathF.Round(y0));

            var c = vertices[i0].ColorRgb;
            ToColor(c.X, c.Y, c.Z, 1f, out var r, out var g, out var bl);
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (float)steps;
                var x = (int)MathF.Round(x0 + dx * t);
                var y = (int)MathF.Round(y0 + dy * t);
                _scratch[count++] = new LaserPoint(x, y, r, g, bl);
            }

            edgesEmitted++;
        }

        return count;
    }

    /// <summary>Deduplicates a mesh's triangle edges into vertex-index pairs (i0, i1, ...) - done
    /// once per capture instead of once per camera, so the per-camera encode only projects.
    /// An edge strictly interior to a flat surface is dropped: a quad built from two triangles
    /// shares a diagonal, and drawing it turns every face into an X - a cube reads as a tangle of
    /// diagonals instead of a wireframe. Curved tessellation keeps its edges; only a near-exactly
    /// coplanar seam is culled.</summary>
    private void CollectUniqueMeshEdges(Int3[] faces, int faceCount, PbrVertex[] vertices, int vertexCount, CapturedSource target)
    {
        _edgeRecords.Clear();
        target.EdgeIndexCount = 0;

        for (var f = 0; f < faceCount; f++)
        {
            var face = faces[f];
            var hasNormal = TryGetFaceNormal(face, vertices, vertexCount, out var normal);

            for (var e = 0; e < 3; e++)
            {
                var i0 = e == 0 ? face.X : e == 1 ? face.Y : face.Z;
                var i1 = e == 0 ? face.Y : e == 1 ? face.Z : face.X;

                if (i0 < 0 || i0 >= vertexCount || i1 < 0 || i1 >= vertexCount)
                    continue;

                var a = Math.Min(i0, i1);
                var b = Math.Max(i0, i1);
                var key = ((long)a << 32) | (uint)b;

                ref var record = ref CollectionsMarshal.GetValueRefOrAddDefault(_edgeRecords, key, out var exists);
                if (!exists)
                {
                    record = new EdgeRecord { A = a, B = b, Faces = 1, Normal = normal, HasNormal = hasNormal };
                    continue;
                }

                record.Faces++;
                if (record.HasNormal && hasNormal)
                {
                    record.Comparisons++;
                    if (MathF.Abs(Vector3.Dot(record.Normal, normal)) > CoplanarSeamDot)
                        record.CoplanarComparisons++;
                }
            }
        }

        foreach (var record in _edgeRecords.Values)
        {
            // Border edges have nothing to compare against; a seam needs every neighbour coplanar.
            if (record.Faces > 1 && record.Comparisons > 0 && record.CoplanarComparisons == record.Comparisons)
                continue;

            if (target.EdgeIndexCount >= MaxEdges * 2)
            {
                Log.Debug($"LaserCamera: wireframe capped at {MaxEdges} edges", this);
                break;
            }

            if (target.EdgeIndices.Length < target.EdgeIndexCount + 2)
                Array.Resize(ref target.EdgeIndices, Math.Max(target.EdgeIndexCount + 2, target.EdgeIndices.Length * 2));

            target.EdgeIndices[target.EdgeIndexCount++] = record.A;
            target.EdgeIndices[target.EdgeIndexCount++] = record.B;
        }
    }

    /// <summary>Unit normal of a triangle, or false when it is degenerate or references missing
    /// vertices - either way it cannot justify culling an edge.</summary>
    private static bool TryGetFaceNormal(Int3 face, PbrVertex[] vertices, int vertexCount, out Vector3 normal)
    {
        normal = default;
        if (face.X < 0 || face.X >= vertexCount
            || face.Y < 0 || face.Y >= vertexCount
            || face.Z < 0 || face.Z >= vertexCount)
            return false;

        var p0 = vertices[face.X].Position;
        var cross = Vector3.Cross(vertices[face.Y].Position - p0, vertices[face.Z].Position - p0);
        var lengthSquared = cross.LengthSquared();
        if (!(lengthSquared >= 1e-12f) || float.IsInfinity(lengthSquared))
            return false;

        normal = cross / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static bool IsSeparator(in Point p)
    {
        var s = p.Scale.X;
        return float.IsNaN(s) || float.IsInfinity(s);
    }

    /// <summary>Line strips written by PairPointsForLines carry NaN-scale separator points -
    /// their presence is what distinguishes line content from point clouds at capture time.</summary>
    private static bool HasLineSeparators(Point[] points, int pointCount)
    {
        for (var i = 0; i < pointCount; i++)
        {
            if (IsSeparator(points[i]))
                return true;
        }

        return false;
    }

    // Dot and line sizing: Point.Scale is authored as a multiplier (1 = normal size), so these
    // base sizes in laser units define what "normal" means on the scanner - a small bright dot
    // and a thin stroke. The clamps bound the damage of extreme scale values.
    private const float DotRadiusBase = 100f;      // ≈ 2 px at 1080p-equivalent framing
    private const float DotRadiusMax = 1200f;
    private const float LineHalfWidthBase = 40f;
    private const float LineHalfWidthMax = 600f;
    private const int CircleSegments = 12;

    private StructuredList Finish(int count)
    {
        count = Math.Min(count, _scratch.Length);
        TrimScratch(count);

        // Published frames are never mutated, so an unchanged frame (a static scene) is republished
        // as the same list instead of being copied again.
        if (_published != null
            && _published.TypedElements.Length == count
            && MemoryMarshal.AsBytes(_scratch.AsSpan(0, count)).SequenceEqual(MemoryMarshal.AsBytes(_published.TypedElements.AsSpan())))
        {
            return _published;
        }

        // StructuredList takes its length from the array, so the published frame is an exact-size
        // copy; the scratch itself is reused.
        var resultArray = new LaserPoint[count];
        Array.Copy(_scratch, resultArray, count);
        _published = new StructuredList<LaserPoint>(resultArray);
        return _published;
    }

    /// <summary>A spike of content leaves the scratch buffer at its peak size; once frames have stayed
    /// far below it for a while it is reallocated at the size they need.</summary>
    private void TrimScratch(int count)
    {
        if (_scratch.Length <= ScratchTrimAboveLength || count * 4 > _scratch.Length)
        {
            _scratchSlackFrames = 0;
            return;
        }

        if (++_scratchSlackFrames < ScratchTrimAfterFrames)
            return;

        _scratchSlackFrames = 0;
        Array.Resize(ref _scratch, Math.Max(InitialScratchLength, count * 2));
    }

    #endregion

    #region camera

    private void UpdateCameraDefinition(EvaluationContext context)
    {
        LastObjectToWorld = context.ObjectToWorld;

        // A connected camera reference makes this op the lens of THAT camera: its view matrix is
        // borrowed and its aim inputs are ignored. The projection is forced to the scanner field's
        // square aspect - a 16:9 render camera would otherwise feed a wide, squeezed view into the
        // square field (circles become ellipses on the scanner).
        if (TryGetReferenceCamera(context, out var referenceCamera))
        {
            _hasCameraReference = true;
            WorldToCamera = referenceCamera.WorldToCamera;
            CameraToClipSpace = SquareFieldProjection(referenceCamera.CameraToClipSpace);

            if (referenceCamera is ICamera referenceAsCamera)
            {
                var definition = referenceAsCamera.CameraDefinition;
                definition.AspectRatio = 1f;
                _cameraDefinition = definition;
            }
            else
            {
                _cameraDefinition = DeriveCameraDefinition(referenceCamera.WorldToCamera, CameraToClipSpace, _cameraDefinition);
            }

            return;
        }

        _hasCameraReference = false;

        // OutputRef is pure routing (the output's driver pulls this op's frame through it) - the
        // projection is always this op's own camera: the output setup's scanner camera no longer
        // overrides the aim, so routed cameras keep their own separate viewpoints.
        var aspectRatio = AspectRatio.GetValue(context);
        if (!float.IsFinite(aspectRatio) || aspectRatio < 0.0001f)
        {
            // The laser frame IS the scanner field: TryProject maps NDC X and Y onto ±32767 with
            // no aspect correction, so the frustum must be square or the picture is squeezed
            // horizontally. Deriving it from the render resolution (typically 16:9) squashed every
            // frame to ~56% width - the "mushed" look. An explicit value is still honored for
            // anamorphic framing.
            aspectRatio = 1f;
        }

        // Rotation aim: the Rotation input carries (Pitch°, Yaw°, Roll°) around the Position and
        // the Target vector is ignored - same angle order as the transform ops.
        if (UseRotation.GetValue(context))
        {
            var rotation = Rotation.GetValue(context);
            var position = Position.GetValue(context);
            var fieldOfView = FieldOfView.GetValue(context).ToRadians();
            var orientation = Quaternion.CreateFromYawPitchRoll(rotation.Y.ToRadians(), rotation.X.ToRadians(), rotation.Z.ToRadians());
            var forward = Vector3.Transform(-Vector3.UnitZ, orientation);
            var up = Vector3.Transform(Vector3.UnitY, orientation);
            var clip = ClipPlanes.GetValue(context);

            WorldToCamera = GraphicsMath.LookAtRH(position, position + forward, up);
            CameraToClipSpace = GraphicsMath.PerspectiveFovRH(fieldOfView,
                                                              aspectRatio,
                                                              MathF.Max(0.001f, clip.X),
                                                              MathF.Max(clip.Y, clip.X + 1f));

            _cameraDefinition = new CameraDefinition
                                    {
                                        NearFarClip = clip,
                                        Position = position,
                                        Target = position + forward,
                                        Up = up,
                                        AspectRatio = aspectRatio,
                                        FieldOfView = fieldOfView,
                                        Roll = rotation.Z,
                                    };
            return;
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

    /// <summary>
    /// Resolves the optional camera reference. Its definition is pulled before its matrices are
    /// read: the laser pump is not part of the graph's regular invalidation pass, so nothing else
    /// would refresh an animated source camera - the same reason the scene is invalidated in
    /// <see cref="EvaluateSceneWithCapture"/>.
    /// </summary>
    private bool TryGetReferenceCamera(EvaluationContext context, out ICameraPropertiesProvider camera)
    {
        camera = null;
        if (!CameraReference.HasInputConnections)
            return false;

        // The pump runs outside any viewport, so RequestedResolution is 0x0 here. Cameras that
        // derive their aspect ratio from it ([Camera], [OrbitCamera], ...) then divide 0/0 and
        // publish a NaN projection, which would silently collapse this frame to zero points. Give
        // them the scanner field's square resolution for the pull - the same aspect the op's own
        // aim uses - and restore the context afterwards.
        var previousResolution = context.RequestedResolution;
        context.RequestedResolution = SquareFieldResolution;
        object? reference;
        try
        {
            CameraReference.InvalidateGraph();
            reference = CameraReference.GetValue(context);
        }
        finally
        {
            context.RequestedResolution = previousResolution;
        }

        camera = reference as ICameraPropertiesProvider;
        if (camera == null)
        {
            if (PrintToLog.GetValue(context))
                Log.Debug($"LaserCamera: camera reference '{reference?.GetType().Name ?? "null"}' is not a camera", this);

            return false;
        }

        // Defensive: a borrowed projection without a usable vertical scale (and with it the square
        // horizontal one derived from it) can't draw anything. Report the aim as unresolvable so
        // the op falls back to its own inputs - and says so in its status - instead of streaming a
        // blank frame that looks like a scene problem.
        var projection = camera.CameraToClipSpace;
        var view = camera.WorldToCamera;
        if (!float.IsFinite(projection.M22) || MathF.Abs(projection.M22) < 1e-6f
            || !float.IsFinite(view.M11) || !float.IsFinite(view.M41))
        {
            if (PrintToLog.GetValue(context))
                Log.Debug($"LaserCamera: camera reference '{camera.GetType().Name}' has a degenerate projection - ignoring it", this);

            camera = null;
            return false;
        }

        return true;
    }

    /// <summary>The scanner field is square (see <see cref="UpdateCameraDefinition"/>); cameras
    /// evaluating against it must not read a 0x0 viewport.</summary>
    private static readonly Int2 SquareFieldResolution = new(4096, 4096);

    /// <summary>
    /// Ties the borrowed projection's horizontal scale to its vertical one, i.e. aspect 1 - the
    /// scanner field is square, whatever the source camera's lens says. This also repairs a NaN or
    /// infinite horizontal scale, which is what cameras that derive their aspect from a 0x0 viewport
    /// publish (they are evaluated with <see cref="SquareFieldResolution"/> for exactly that reason,
    /// but a cached definition from a real viewport can still carry an anamorphic scale).
    /// </summary>
    private static Matrix4x4 SquareFieldProjection(Matrix4x4 projection)
    {
        projection.M11 = projection.M22;
        return projection;
    }

    /// <summary>
    /// Aim of a referenced camera that only exposes matrices (no <see cref="ICamera"/> definition):
    /// derived from the view/projection pair so the view gizmo still sits on that camera instead of
    /// on this op's ignored inputs. A non-perspective matrix leaves the previous definition alone.
    /// </summary>
    private static CameraDefinition DeriveCameraDefinition(Matrix4x4 worldToCamera, Matrix4x4 cameraToClipSpace, CameraDefinition fallback)
    {
        if (!Matrix4x4.Invert(worldToCamera, out var cameraToWorld))
            return fallback;

        // PerspectiveFovRH: M22 = 1/tan(fov/2), M11 = M22/aspect. Orthographic matrices carry no
        // meaningful fov, so their frame is not worth guessing.
        var projectionScale = MathF.Abs(cameraToClipSpace.M22);
        if (!float.IsFinite(projectionScale) || projectionScale < 1e-6f || projectionScale > 1e6f)
            return fallback;

        var position = cameraToWorld.Translation;
        var forward = new Vector3(-cameraToWorld.M31, -cameraToWorld.M32, -cameraToWorld.M33);
        if (forward.LengthSquared() < 1e-12f || !float.IsFinite(forward.LengthSquared()))
            return fallback;

        var horizontalScale = MathF.Abs(cameraToClipSpace.M11);
        return new CameraDefinition
                   {
                       NearFarClip = fallback.NearFarClip,
                       Position = position,
                       Target = position + Vector3.Normalize(forward),
                       Up = Vector3.Normalize(new Vector3(cameraToWorld.M21, cameraToWorld.M22, cameraToWorld.M23)),
                       FieldOfView = 2f * MathF.Atan(1f / projectionScale),
                       AspectRatio = horizontalScale > 1e-6f ? projectionScale / horizontalScale : 1f,
                   };
    }

    public CameraDefinition CameraDefinition => _cameraDefinition;
    private CameraDefinition _cameraDefinition;

    public Matrix4x4 CameraToClipSpace { get; set; }
    public Matrix4x4 WorldToCamera { get; set; }
    public Matrix4x4 LastObjectToWorld { get; set; }

    // Implement ICamera. With Use Rotation on, the aim is the Rotation angles (the Target vector
    // is ignored), so the target point the editor drags is derived from the position + orientation,
    // and dragging converts back to yaw/pitch. A connected CameraReference owns the aim: the
    // getters report that camera and the setters are inert, so mouse control and the gizmo cannot
    // write to inputs the projection ignores.
    public Vector3 CameraPosition
    {
        get => _hasCameraReference ? _cameraDefinition.Position : Position.Value;
        set
        {
            if (_hasCameraReference)
                return;

            Animator.UpdateVector3InputValue(Position, value);
        }
    }

    public Vector3 CameraTarget
    {
        get
        {
            if (_hasCameraReference)
                return _cameraDefinition.Target;

            return UseRotation.Value && _cameraDefinition.Position != default
                       ? _cameraDefinition.Target
                       : Target.Value;
        }
        set
        {
            if (_hasCameraReference)
                return;

            if (UseRotation.Value)
            {
                var delta = value - Position.Value;
                var length = delta.Length();
                if (length < 0.0001f)
                    return;

                var yaw = MathF.Atan2(-delta.X, -delta.Z).ToDegrees();
                var pitch = MathF.Asin(Math.Clamp(delta.Y / length, -1f, 1f)).ToDegrees();
                var current = Rotation.Value;
                Animator.UpdateVector3InputValue(Rotation, new Vector3(pitch, yaw, current.Z));
                return;
            }

            Animator.UpdateVector3InputValue(Target, value);
        }
    }

    public float CameraRoll
    {
        get => _hasCameraReference ? _cameraDefinition.Roll : Roll.Value;
        set
        {
            if (_hasCameraReference)
                return;

            Animator.UpdateFloatInputValue(Roll, value);
        }
    }

    #endregion

    #region gizmo

    // The transform gizmo layer moves the camera position (TranslationInput) and, because the op
    // implements ICameraViewGizmo, also draws and drags the aim target. With Use Rotation on, the
    // target drag writes the Rotation angles instead of the (ignored) Target vector. A connected
    // CameraReference owns the aim, so both handles disappear instead of editing dead inputs.
    IInputSlot ITransformable.TranslationInput => _hasCameraReference ? null : Position;

    IInputSlot ITransformable.RotationInput => null;

    IInputSlot ITransformable.ScaleInput => null;

    public Action<Instance, EvaluationContext> TransformCallback { get; set; }

    IInputSlot ICameraViewGizmo.TargetInput => _hasCameraReference
                                                   ? null
                                                   : UseRotation.Value
                                                       ? Rotation
                                                       : Target;

    #endregion

    #region GPU readback

    /// <summary>
    /// Pair of staging buffers for pipelined GPU readback. Frame N issues a copy into one
    /// buffer and maps what frame N-1 copied into the other, giving the GPU a full frame
    /// to complete the transfer - the CPU almost never stalls. The CPU side is one reusable
    /// array per pair, so a steady scene reads back without allocating.
    /// </summary>
    private sealed class StagingPair
    {
        /// <summary>Capture passes since this pair last served a draw slot.</summary>
        public int IdlePasses;

        /// <summary>Time between recording the copy that was last read and reading it.</summary>
        public double LastAgeMs;

        /// <summary>The last read found the GPU copy unfinished and served the previous data.</summary>
        public bool LastReadWasStale;

        /// <summary>
        /// Copies the source, then reads the PREVIOUS frame's copy - always one frame
        /// behind, never same-frame. A same-frame map waits for the GPU to finish the copy (a full
        /// pipeline flush), and scene buffers can be recreated between evaluations, which defeated
        /// the old per-source pipelining and turned every read back into a stall. The previous
        /// copy is mapped WITHOUT waiting: when the GPU is still behind, the last good frame is
        /// served instead of blocking the render thread on the whole queue. On the very first use
        /// of a pair the fresh copy is read synchronously once.
        /// Returns the element count; <paramref name="data"/> is reused by the next read of this pair,
        /// so only its first <c>count</c> elements are valid and only until then.
        /// </summary>
        public int Read<T>(Buffer source, int sourceBytes, int stride, int elementCount, int offsetElements, out T[] data) where T : struct
        {
            try
            {
                Ensure(sourceBytes, stride);

                ResourceManager.Device.ImmediateContext.CopyResource(source, _buffers[_writeIndex]);
                _copyStamp[_writeIndex] = Stopwatch.GetTimestamp();

                // The slice belongs to the copy, not to the read: mapping last frame's copy with this
                // frame's (larger) element count would read elements that copy never drew - whatever
                // the buffer held before, served as one frame of phantom points.
                _copyElementCount[_writeIndex] = elementCount;
                _copyOffsetElements[_writeIndex] = offsetElements;

                if (!_hasPrevious)
                {
                    // First use: nothing to read yet, so wait for the fresh copy once.
                    TryMapInto<T>(_writeIndex, true, sourceBytes, stride);
                    _hasPrevious = true;
                    LastAgeMs = 0;
                    LastReadWasStale = false;
                    _writeIndex = 1 - _writeIndex;
                }
                else
                {
                    var readIndex = 1 - _writeIndex;
                    if (TryMapInto<T>(readIndex, false, sourceBytes, stride))
                    {
                        LastAgeMs = (Stopwatch.GetTimestamp() - _copyStamp[readIndex]) * 1000.0 / Stopwatch.Frequency;
                        LastReadWasStale = false;
                        _writeIndex = readIndex; // the next copy overwrites the buffer just read
                    }
                    else
                    {
                        LastReadWasStale = true;

                        // The GPU has not finished the previous copy yet. Flip the write target so
                        // the NEXT copy goes into the already-read buffer and the freshly copied
                        // frame is preserved for the next read - leaving the target alone would
                        // overwrite that pending copy on the very next pass.
                        _writeIndex = readIndex;
                    }
                }

                _failureLogged = false;
                data = _data as T[] ?? [];
                return data.Length == 0 ? 0 : _dataCount;
            }
            catch (Exception e)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Log.Error($"LaserCamera: GPU readback of {typeof(T).Name} failed - {e.Message}");
                }

                data = [];
                return 0;
            }
        }

        public void Dispose()
        {
            if (_buffers != null)
            {
                foreach (var buffer in _buffers)
                {
                    buffer?.Dispose();
                }

                _buffers = null;
            }

            _hasPrevious = false;
            _data = null;
            _dataCount = 0;
        }

        private void Ensure(int sourceBytes, int stride)
        {
            // CopyResource needs identical layouts, so both size and stride have to match.
            if (_buffers != null && _buffers[0].Description.SizeInBytes == sourceBytes && _buffers[0].Description.StructureByteStride == stride)
                return;

            Log.Debug($"LaserCamera: capture staging reallocated to {sourceBytes} bytes (stride {stride})");

            _buffers ??= new Buffer[2];
            for (var i = 0; i < 2; i++)
            {
                _buffers[i]?.Dispose();
                _buffers[i] = new Buffer(ResourceManager.Device, new BufferDescription
                                                                     {
                                                                         Usage = ResourceUsage.Staging,
                                                                         CpuAccessFlags = CpuAccessFlags.Read,
                                                                         SizeInBytes = sourceBytes,
                                                                         OptionFlags = ResourceOptionFlags.BufferStructured,
                                                                         StructureByteStride = stride
                                                                     });
            }

            _writeIndex = 0;
            _hasPrevious = false;
            _data = null;
            _dataCount = 0;
        }

        /// <summary>Maps one staging buffer and copies the requested slice into the reusable array.
        /// With <paramref name="wait"/> off it does not stall on queued GPU work: false means "copy not
        /// finished" (the blocking map was 7-30ms per pull).</summary>
        private bool TryMapInto<T>(int index, bool wait, int sourceBytes, int stride) where T : struct
        {
            var elementCount = _copyElementCount[index];
            var offsetElements = _copyOffsetElements[index];
            var context = ResourceManager.Device.ImmediateContext;
            DataStream stream = null;
            var mapped = false;
            try
            {
                if (wait)
                {
                    context.MapSubresource(_buffers[index], 0, MapMode.Read, MapFlags.None, out stream);
                }
                else if (!context.TryMapSubresourceForRead(_buffers[index], 0, out stream))
                {
                    return false;
                }

                mapped = true;

                // The stream reads raw memory without a bounds check: never past the mapped size.
                var available = Math.Max(0, sourceBytes / stride - offsetElements);
                var count = Math.Min(elementCount, available);
                if (count <= 0)
                {
                    _dataCount = 0;
                    return true;
                }

                var target = _data as T[];
                if (target == null || target.Length < count || (target.Length > ShrinkAboveElements && count * 4 < target.Length))
                {
                    target = new T[count + count / 4 + 16];
                    _data = target;
                }

                stream.Position = (long)offsetElements * stride;
                stream.ReadRange(target, 0, count);
                _dataCount = count;
                return true;
            }
            finally
            {
                stream?.Dispose();
                if (mapped)
                    context.UnmapSubresource(_buffers[index], 0);
            }
        }

        /** Above this a reusable array is replaced once its content has shrunk to a quarter. */
        private const int ShrinkAboveElements = 1 << 16;

        private Buffer[] _buffers;
        private readonly long[] _copyStamp = new long[2];
private readonly int[] _copyElementCount = new int[2];
private readonly int[] _copyOffsetElements = new int[2];
        private int _writeIndex;
        private bool _hasPrevious;
        private bool _failureLogged;

        /** Last successfully mapped frame - served while the GPU copy of the current one is still in flight. */
        private Array _data;
        private int _dataCount;
    }

    private void SetEmptyOutput()
    {
        LaserPoints.Value = s_emptyList;
        // Animated so a transient failure self-heals on the next frame instead of
        // leaving the laser chain stuck on an empty frame forever
        LaserPoints.DirtyFlag.Trigger = DirtyFlagTrigger.Animated;
        // Fully reset so the next frame self-recovers (e.g. right after project load)
        // without needing a manual nudge on any input.
        _hasFrame = false;
        _lastPointCount = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LaserContentRegistry.Unregister(this);
            _published = null;

            for (var slot = 0; slot < _captureStaging.Length; slot++)
            {
                _captureStaging[slot]?.Dispose();
                _captureStaging[slot] = null;
            }
        }

        base.Dispose(disposing);
    }

    #endregion

    private static readonly StructuredList s_emptyList = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());

    private readonly GeometryCapture _capture = new();
    private readonly CapturedScene _scene = new();

    /** Two staging pairs per captured draw slot (vertices and indices). */
    private readonly StagingPair[] _captureStaging = new StagingPair[GeometryCapture.MaxEntries * 2];

    private double _lastEvalMs;
    private double _lastReadMs;
    private double _lastDataAgeMs;
    private int _lastStaleReads;
    private bool _hasFrame;
    private int _lastPointCount;
    private string? _lastError;
    private long _lastErrorLogTimestamp;

    /** The last camera definition resolved a CameraReference: the own aim inputs are ignored. */
    private bool _hasCameraReference;

    private LaserPoint[] _scratch = new LaserPoint[InitialScratchLength];

    /** Last frame handed to the output; compared with the next one so static content publishes without a copy. */
    private StructuredList<LaserPoint>? _published;
    private int _scratchSlackFrames;

    /// <summary>Per-capture edge table: first face normal plus how many neighbours were coplanar.
    /// Reused across captures (cleared, capacity kept) so a wireframe pass allocates no per-edge
    /// objects.</summary>
    private readonly Dictionary<long, EdgeRecord> _edgeRecords = new();

    private struct EdgeRecord
    {
        public int A;
        public int B;
        public int Faces;
        public int Comparisons;
        public int CoplanarComparisons;
        public Vector3 Normal;
        public bool HasNormal;
    }

    /// <summary>Cos of the largest angle between two faces that still counts as one flat surface
    /// (~1°). A quad's triangle diagonal sits at exactly 1, so the seam is culled; noisy
    /// tessellation stays below it and keeps its edges.</summary>
    private const float CoplanarSeamDot = 0.99985f;

    /// <summary>Hard cap on generated points (1M points = 24 MB scratch) - bounds damage
    /// from huge input.</summary>
    private const int MaxOutputPoints = 1 << 20;

    private const int InitialScratchLength = 1024;

    /** The scratch buffer is only trimmed above this length... */
    private const int ScratchTrimAboveLength = 1 << 16;

    /** ...and after this many consecutive frames that needed less than a quarter of it. */
    private const int ScratchTrimAfterFrames = 600;

    /// <summary>Upper dwell limit per dot. Kept at or below the setup pipeline's default
    /// MaxStaticPoints (64) so dots survive the static-dwell limiter unblanked.</summary>
    private const int MaxDotDwellPoints = 64;

    private const float DefaultMaxEdgeStep = 4000f;

    /// <summary>Upper bound on unique wireframe edges per frame - keeps huge meshes from
    /// ballooning the laser frame.</summary>
    private const int MaxEdges = 65536;

    /// <summary>Upper bound on lit samples per single edge; bounds the cost of a tiny
    /// MaxEdgeStep against a full-field edge.</summary>
    private const int MaxSamplesPerEdge = 256;

    /// <summary>Mesh readback size caps - bigger meshes are truncated, not skipped.</summary>
    private const int MaxMeshVertices = 131072;
    private const int MaxMeshFaces = 131072;

    /** One triangle: three 32 bit indices. */
    private const int FaceStride = 3 * 4;

    /** Capture passes an unused staging pair keeps its GPU buffers before they are released. */
    private const int IdlePassesBeforeRelease = 240;

    [Input(Guid = "4F55D55B-B51D-4A29-8788-E1AE21A80960")]
    public readonly InputSlot<float> MaxEdgeStep = new(DefaultMaxEdgeStep);

    [Input(Guid = "0C48ED58-1242-4F8B-869A-7396CB055931")]
    public readonly InputSlot<int> DotDwellPoints = new(8);

    /// <summary>The laser output of the active output setup this camera's frame is routed to;
    /// empty = not routed.</summary>
    [Input(Guid = "E7A2C41D-9B58-4C0F-A3D6-8E41F2B7C5A9")]
    public readonly InputSlot<Guid> OutputRef = new();

    /// <summary>When off the camera holds its last frame and stops evaluating its scene, like
    /// SendToOutput freezes its texture.</summary>
    [Input(Guid = "2F4B9989-7E26-4DE9-A6E0-804704344DF3")]
    public readonly InputSlot<bool> Update = new(true);

    /// <summary>When on, the camera aims by the Rotation angles (Pitch°/Yaw°/Roll° around the
    /// Position) and the Target vector is ignored.</summary>
    [Input(Guid = "8E5C3A71-2D94-4B6F-A0C8-51E97B3D62F4")]
    public readonly InputSlot<bool> UseRotation = new();

    /// <summary>The aim angles in degrees when Use Rotation is on, in the transform ops' order:
    /// X = Pitch°, Y = Yaw°, Z = Roll°. Ignored when Use Rotation is off (the Target point applies).</summary>
    [Input(Guid = "7B2D9F04-63A8-4C51-9E2B-D84F06A73C18")]
    public readonly InputSlot<Vector3> Rotation = new();

    [Input(Guid = "73241494-C686-4013-96C0-EF40D5932F7F")]
    public readonly InputSlot<bool> PrintToLog = new();

    [Input(Guid = "6475CA9C-6ED6-4659-87D2-A602813C823C")]
    public readonly InputSlot<Command> Scene = new();

    /// <summary>Reference output of another camera ([Camera], [OrbitCamera], a rig's master
    /// [LaserCamera], ...). Connected: that camera's view and lens drive the projection and this
    /// op's own Position/Target/Rotation/FieldOfView are ignored. Empty: the op aims itself.</summary>
    [Input(Guid = "24AF02C5-0683-4986-91AC-2BA3FA0579CC")]
    public readonly InputSlot<Object> CameraReference = new();

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
}
