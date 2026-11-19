base = r"D:\CrossTixlClaude\Operators\DepthAnything"


def load(path):
    b = open(path, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(path, s, crlf):
    open(path, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


p = base + r"\Utils\DepthChunkAlignment.cs"
s, crlf = load(p)
i = s.index("/// <summary>\n/// Chains overlapping windows into one world frame")
s = s[:i] + '''/// <summary>
/// Chains overlapping windows into one world frame the way DA3-Streaming does: the frames two windows share are
/// unprojected in both, and a similarity transform fitted on those point pairs carries the new window onto the one before.
/// Rotation comes from the shared frames' camera poses (point clouds barely constrain it for a rotating camera); scale and
/// translation come from the points.
/// <para>New windows are always placed on the last <i>key</i> window, the one that advanced the frame ring, never on the
/// previous window: while the camera holds still the same ring is inferred again and again, and re-chaining each of those
/// windows onto the one before would compound a small error every time.</para>
/// </summary>
internal sealed class DepthChunkChain
{
    private const int SampleStride = 8;
    private const float MinConfidence = 0.2f;

    private int _chunkIndex;
    private bool _hasKey;
    private Sim3 _keyToWorld = Sim3.Identity;
    private List<Vector3>[]? _keyPoints;
    private List<float>[]? _keyWeights;
    private Quaternion[]? _keyRotations;

    public Sim3 LastChunkToWorld { get; private set; } = Sim3.Identity;
    public int LastPairCount { get; private set; }
    public float LastResidualBefore { get; private set; }
    public float LastResidualAfter { get; private set; }

    /** How far a pure point-based rotation fit would have turned from the pose-based rotation, for diagnostics. */
    public float LastPointRotationDegrees { get; private set; }

    private static float QuaternionAngle(Quaternion q) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;

    /**
     * Places a window whose view j is view j + <paramref name="keyOffset"/> of the key window (1 for a window that slides
     * one frame at a time). With <paramref name="advance"/> the window becomes the new key window. Returns the frames to use:
     * all of the first window, afterwards the views that are new. Null when the shared frames cannot be fitted.
     */
    public List<PlacedFrame>? Add(DepthChunk chunk, int keyOffset, bool advance)
    {
        var views = chunk.Views;
        var shared = views - keyOffset;
        var chunkToWorld = Sim3.Identity;
        if (_hasKey)
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
                var keyIndex = j + keyOffset;
                for (var i = 0; i < points.Count; i++)
                {
                    var w = MathF.Min(weights[i], _keyWeights![keyIndex][i]);
                    if (w <= 0f)
                        continue;

                    src.Add(points[i]);
                    dst.Add(_keyPoints![keyIndex][i]);
                    weight.Add(w);
                }

                // p_key = Q * p_new, so Q = Q_key * Q_new^-1 for this shared frame
                chunk.CameraToWorld(j, out var newMatrix, out _);
                var q = _keyRotations![keyIndex] * Quaternion.Inverse(Quaternion.CreateFromRotationMatrix(newMatrix));
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

            if (!Sim3Fit.FitWithRotation(src, dst, weight, poseRotation, out var newToKey))
                return null;

            var before = 0f;
            var after = 0f;
            for (var i = 0; i < src.Count; i++)
            {
                before += Vector3.Distance(src[i], dst[i]);
                after += Vector3.Distance(newToKey.Apply(src[i]), dst[i]);
            }

            LastResidualBefore = before / src.Count;
            LastResidualAfter = after / src.Count;
            chunkToWorld = _keyToWorld.Compose(newToKey);
        }

        LastChunkToWorld = chunkToWorld;
        var placed = new List<PlacedFrame>();
        for (var view = _hasKey ? shared : 0; view < views; view++)
        {
            chunk.CameraToWorld(view, out var rotation, out var position);
            var worldPosition = chunkToWorld.Apply(position);
            var worldRotation = rotation * Matrix4x4.CreateFromQuaternion(chunkToWorld.Rotation);
            placed.Add(new PlacedFrame(_chunkIndex, view, worldRotation, worldPosition, chunkToWorld.Scale));
        }

        if (!_hasKey || advance)
        {
            _keyToWorld = chunkToWorld;
            _keyPoints = new List<Vector3>[views];
            _keyWeights = new List<float>[views];
            _keyRotations = new Quaternion[views];
            for (var view = 0; view < views; view++)
            {
                _keyPoints[view] = new List<Vector3>();
                _keyWeights[view] = new List<float>();
                chunk.SamplePoints(view, SampleStride, MinConfidence, _keyPoints[view], _keyWeights[view]);
                chunk.CameraToWorld(view, out var matrix, out _);
                _keyRotations[view] = Quaternion.CreateFromRotationMatrix(matrix);
            }

            _hasKey = true;
        }

        _chunkIndex++;
        return placed;
    }
}
'''
save(p, s, crlf)

p = base + r"\Symbols\Lib\io\video\depthanything\DepthAnything.cs"
s, crlf = load(p)
a = "var placed = workerContext.Chain.Add(chunk, workerContext.ChunkShift);"
assert s.count(a) == 1
s = s.replace(a, "// The ring is the key window's last views-1 frames, so a window is always placed one frame past the key window\n        var placed = workerContext.Chain.Add(chunk, 1, workerContext.ChunkShift == 1);")
save(p, s, crlf)
print("ok")
