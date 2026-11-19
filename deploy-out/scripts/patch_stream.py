base = r"D:\CrossTixlClaude\Operators\DepthAnything"


def load(path):
    b = open(path, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(path, s, crlf):
    open(path, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c, count=1):
    assert s.count(a) == count, (s.count(a), a[:90])
    return s.replace(a, c)


# ---------------------------------------------------------------- chain: arbitrary shift between windows
p = base + r"\Utils\DepthChunkAlignment.cs"
s, crlf = load(p)
i = s.index("/// <summary>\n/// Chains overlapping windows into one world frame")
s = s[:i] + '''/// <summary>
/// Chains overlapping windows into one world frame the way DA3-Streaming does: the frames two windows share are
/// unprojected in both, and a similarity transform fitted on those point pairs carries the new window onto the previous one.
/// Rotation comes from the shared frames' camera poses (point clouds barely constrain it for a rotating camera); scale and
/// translation come from the points.
/// </summary>
internal sealed class DepthChunkChain
{
    private const int SampleStride = 8;
    private const float MinConfidence = 0.2f;

    private int _chunkIndex;
    private Sim3 _previousToWorld = Sim3.Identity;
    private List<Vector3>[]? _previousPoints;
    private List<float>[]? _previousWeights;
    private Quaternion[]? _previousRotations;

    public Sim3 LastChunkToWorld => _previousToWorld;
    public int LastPairCount { get; private set; }
    public float LastResidualBefore { get; private set; }
    public float LastResidualAfter { get; private set; }

    /** How far a pure point-based rotation fit would have turned from the pose-based rotation, for diagnostics. */
    public float LastPointRotationDegrees { get; private set; }

    private static float QuaternionAngle(Quaternion q) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;

    /**
     * Places a window that follows the previous one by <paramref name="shift"/> frames: view j of this window is view j + shift
     * of the previous one. A shift of 0 means only the newest view changed. Returns the frames to use: all of the first
     * window, afterwards the views that are new. Null when the shared frames cannot be fitted.
     */
    public List<PlacedFrame>? Add(DepthChunk chunk, int shift)
    {
        var views = chunk.Views;
        var first = _previousPoints == null;
        var shared = shift == 0 ? views - 1 : views - shift;
        if (!first)
        {
            var src = new List<Vector3>();
            var dst = new List<Vector3>();
            var weight = new List<float>();
            var accumulated = new Quaternion(0, 0, 0, 0);
            var reference = Quaternion.Identity;
            for (var j = 0; j < shared; j++)
            {
                var points = new List<Vector3>();
                var weights = new List<float>();
                chunk.SamplePoints(j, SampleStride, MinConfidence, points, weights);
                var previousIndex = j + shift;
                for (var i = 0; i < points.Count; i++)
                {
                    var w = MathF.Min(weights[i], _previousWeights![previousIndex][i]);
                    if (w <= 0f)
                        continue;

                    src.Add(points[i]);
                    dst.Add(_previousPoints![previousIndex][i]);
                    weight.Add(w);
                }

                // p_previous = Q * p_new, so Q = Q_previous * Q_new^-1 for this shared frame
                chunk.CameraToWorld(j, out var newMatrix, out _);
                var q = _previousRotations![previousIndex] * Quaternion.Inverse(Quaternion.CreateFromRotationMatrix(newMatrix));
                if (j == 0)
                    reference = q;
                else if (Quaternion.Dot(reference, q) < 0f)
                    q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);

                accumulated = new Quaternion(accumulated.X + q.X, accumulated.Y + q.Y, accumulated.Z + q.Z, accumulated.W + q.W);
            }

            LastPairCount = src.Count;
            var poseRotation = Quaternion.Normalize(accumulated);
            if (Sim3Fit.Fit(src, dst, weight, out var pointFit))
                LastPointRotationDegrees = QuaternionAngle(Quaternion.Concatenate(Quaternion.Inverse(poseRotation), pointFit.Rotation));

            if (!Sim3Fit.FitWithRotation(src, dst, weight, poseRotation, out var newToPrevious))
                return null;

            var before = 0f;
            var after = 0f;
            for (var i = 0; i < src.Count; i++)
            {
                before += Vector3.Distance(src[i], dst[i]);
                after += Vector3.Distance(newToPrevious.Apply(src[i]), dst[i]);
            }

            LastResidualBefore = before / src.Count;
            LastResidualAfter = after / src.Count;
            _previousToWorld = _previousToWorld.Compose(newToPrevious);
        }

        var placed = new List<PlacedFrame>();
        for (var view = first ? 0 : shared; view < views; view++)
        {
            chunk.CameraToWorld(view, out var rotation, out var position);
            var worldPosition = _previousToWorld.Apply(position);
            var worldRotation = rotation * Matrix4x4.CreateFromQuaternion(_previousToWorld.Rotation);
            placed.Add(new PlacedFrame(_chunkIndex, view, worldRotation, worldPosition, _previousToWorld.Scale));
        }

        // Every view of this window, in this window's frame, is a candidate shared frame for the next one
        _previousPoints = new List<Vector3>[views];
        _previousWeights = new List<float>[views];
        _previousRotations = new Quaternion[views];
        for (var view = 0; view < views; view++)
        {
            _previousPoints[view] = new List<Vector3>();
            _previousWeights[view] = new List<float>();
            chunk.SamplePoints(view, SampleStride, MinConfidence, _previousPoints[view], _previousWeights[view]);
            chunk.CameraToWorld(view, out var matrix, out _);
            _previousRotations[view] = Quaternion.CreateFromRotationMatrix(matrix);
        }

        _chunkIndex++;
        return placed;
    }
}
'''
save(p, s, crlf)

# ---------------------------------------------------------------- DepthAnything: N-view sliding window
p = base + r"\Symbols\Lib\io\video\depthanything\DepthAnything.cs"
s, crlf = load(p)

s = rep(s, "        Da3BaseTracked\n    }", "        Da3BaseTracked,\n        Da3BaseStream\n    }")
s = rep(s, '            ModelSize.Da3BaseTracked => ["da3-base-mv2.onnx"],', '            ModelSize.Da3BaseTracked => ["da3-base-mv2.onnx"],\n            ModelSize.Da3BaseStream => ["da3-base-mv6-392.onnx"],')
s = rep(s, '        ("V3 Base (camera tracking)", ModelSize.Da3BaseTracked),', '        ("V3 Base (camera tracking)", ModelSize.Da3BaseTracked),\n        ("V3 Base (6-view stream)", ModelSize.Da3BaseStream),')
s = rep(s, "                       ViewCount = isFloatPair ? 2 : 1", "                       ViewCount = isFloatPair ? (dimensions[1] > 1 ? (int)dimensions[1] : 2) : 1")

# warm-up tensor
s = rep(s, """            using var inputValue = modelInfo.ViewCount == 2
                                       ? OrtValue.CreateTensorValueFromMemory(new float[2 * 3 * width * height], [1, 2, 3, height, width])""",
        """            using var inputValue = modelInfo.ViewCount >= 2
                                       ? OrtValue.CreateTensorValueFromMemory(new float[modelInfo.ViewCount * 3 * width * height], [1, modelInfo.ViewCount, 3, height, width])""")

# process frame input
s = rep(s, """            using var inputValue = modelInfo.ViewCount == 2
                                       ? CreatePairInput(workerContext, request)
                                       : request.InputBytes != null""",
        """            using var inputValue = modelInfo.ViewCount == 2
                                       ? CreatePairInput(workerContext, request)
                                       : modelInfo.ViewCount > 2
                                           ? CreateChunkInput(workerContext, request, modelInfo.ViewCount)
                                           : request.InputBytes != null""")

# pose accumulate
s = rep(s, """            if (modelInfo.ViewCount == 2)
            {
                // Chain the pair's relative pose onto the world pose and bring this frame's depth into the same units""",
        """            if (modelInfo.ViewCount > 2)
            {
                // The window's views share one depth scale and consistent poses; chain the window onto the previous one
                AccumulateChunkPose(workerContext, outputs, request, modelInfo.ViewCount);
                if (workerContext.WorldScale != 1f)
                {
                    for (int i = 0; i < count; i++)
                    {
                        resultDepth[i] *= workerContext.WorldScale;
                    }
                }
            }
            else if (modelInfo.ViewCount == 2)
            {
                // Chain the pair's relative pose onto the world pose and bring this frame's depth into the same units""")

# worker context fields
s = rep(s, "        public readonly float[] KeyRotation = [1, 0, 0, 0, 1, 0, 0, 0, 1];",
        """        // N-view window: the previous keyframes, the reusable window tensor and the chain that places each window
        public float[][]? Ring;
        public float[]? WindowInput;
        public int RingViewLength;
        public int ChunkShift;
        public DepthChunkChain? Chain;
        public float[]? ChunkDepth;
        public float[]? ChunkConfidence;
        public float[]? ChunkExtrinsics;
        public float[]? ChunkIntrinsics;

        public readonly float[] KeyRotation = [1, 0, 0, 0, 1, 0, 0, 0, 1];""")

# new methods before CreatePairInput's doc comment
marker = "    /** Keyframe and current frame as one [1,2,3,H,W] tensor;"
assert s.count(marker) == 1
methods = '''    /**
     * The last views-1 keyframes plus the current frame as one [1,N,3,H,W] window. The ring starts as copies of the first
     * frame, so the first windows are identical views (identity poses) and the real ones follow as the camera moves. A frame
     * that barely differs from the newest keyframe does not enter the ring (shift 0).
     */
    private static OrtValue CreateChunkInput(WorkerContext workerContext, DepthEstimationRequest request, int views)
    {
        var viewLength = 3 * request.Height * request.Width;
        var current = request.InputData!;
        if (workerContext.WindowInput == null || workerContext.WindowInput.Length != views * viewLength || workerContext.RingViewLength != viewLength)
        {
            workerContext.WindowInput = new float[views * viewLength];
            workerContext.Ring = new float[views - 1][];
            for (var i = 0; i < views - 1; i++)
            {
                workerContext.Ring[i] = new float[viewLength];
                Array.Copy(current, workerContext.Ring[i], viewLength);
            }

            workerContext.RingViewLength = viewLength;
            workerContext.Chain = null;
        }

        var ring = workerContext.Ring!;
        workerContext.ChunkShift = MeanAbsoluteDifference(ring[views - 2], current, viewLength) >= KeyframeMinFrameDifference ? 1 : 0;
        for (var i = 0; i < views - 1; i++)
            Array.Copy(ring[i], 0, workerContext.WindowInput, i * viewLength, viewLength);

        Array.Copy(current, 0, workerContext.WindowInput, (views - 1) * viewLength, viewLength);
        return OrtValue.CreateTensorValueFromMemory(workerContext.WindowInput, [1, views, 3, request.Height, request.Width]);
    }

    /** Places the window in the world frame and publishes the newest frame's pose and depth scale. */
    private static void AccumulateChunkPose(WorkerContext workerContext, IReadOnlyList<OrtValue> outputs, DepthEstimationRequest request, int views)
    {
        if (workerContext.ExtrinsicsIndex < 0 || workerContext.IntrinsicsIndex < 0 || workerContext.Ring == null || request.Width != request.Height)
            return;

        var size = request.Height;
        var plane = size * size;
        var depth = outputs[workerContext.DepthIndex].GetTensorDataAsSpan<float>();
        var extrinsics = outputs[workerContext.ExtrinsicsIndex].GetTensorDataAsSpan<float>();
        var intrinsics = outputs[workerContext.IntrinsicsIndex].GetTensorDataAsSpan<float>();
        if (depth.Length < views * plane || extrinsics.Length < views * 12 || intrinsics.Length < views * 9)
            return;

        if (workerContext.ChunkDepth == null || workerContext.ChunkDepth.Length != views * plane)
        {
            workerContext.ChunkDepth = new float[views * plane];
            workerContext.ChunkConfidence = new float[views * plane];
            workerContext.ChunkExtrinsics = new float[views * 12];
            workerContext.ChunkIntrinsics = new float[views * 9];
        }

        depth.Slice(0, views * plane).CopyTo(workerContext.ChunkDepth);
        extrinsics.Slice(0, views * 12).CopyTo(workerContext.ChunkExtrinsics!);
        intrinsics.Slice(0, views * 9).CopyTo(workerContext.ChunkIntrinsics!);
        if (workerContext.ConfidenceIndex >= 0 && outputs[workerContext.ConfidenceIndex].GetTensorDataAsSpan<float>() is { Length: > 0 } confidence && confidence.Length >= views * plane)
            confidence.Slice(0, views * plane).CopyTo(workerContext.ChunkConfidence!);
        else
            Array.Fill(workerContext.ChunkConfidence!, 1f);

        var chunk = new DepthChunk(views, size, workerContext.ChunkDepth, workerContext.ChunkConfidence!, workerContext.ChunkExtrinsics!, workerContext.ChunkIntrinsics!);
        workerContext.Chain ??= new DepthChunkChain();
        var placed = workerContext.Chain.Add(chunk, workerContext.ChunkShift);
        if (placed == null || placed.Count == 0)
        {
            // The shared frames could not be fitted: start a fresh world instead of publishing a guess
            workerContext.Chain = null;
            return;
        }

        var frame = placed[^1];
        var m = frame.Rotation;
        var world = workerContext.WorldRotation;
        world[0] = m.M11; world[1] = m.M21; world[2] = m.M31;
        world[3] = m.M12; world[4] = m.M22; world[5] = m.M32;
        world[6] = m.M13; world[7] = m.M23; world[8] = m.M33;
        workerContext.WorldPosition = frame.Position;
        workerContext.WorldScale = frame.DepthScale;

        if (workerContext.ChunkShift == 1)
        {
            var ring = workerContext.Ring;
            var recycled = ring[0];
            for (var i = 0; i < ring.Length - 1; i++)
                ring[i] = ring[i + 1];

            Array.Copy(request.InputData!, recycled, workerContext.RingViewLength);
            ring[^1] = recycled;
        }
    }

'''
s = s.replace(marker, methods + marker)
save(p, s, crlf)
print("ok")
