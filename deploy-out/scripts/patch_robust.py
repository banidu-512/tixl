base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"


def load(name):
    b = open(base + "\\" + name, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(name, s, crlf):
    open(base + "\\" + name, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c):
    assert s.count(a) == 1, (s.count(a), a[:70])
    return s.replace(a, c)


# ---- DepthAnything: reject single-frame pose jumps, raise the still-frame gate
s, crlf = load("DepthAnything.cs")
s = rep(s, "        public float KeyWorldDepth;\n",
        "        public float KeyWorldDepth;\n        public readonly float[] LastRotation = [1, 0, 0, 0, 1, 0, 0, 0, 1];\n"
        "        public Vector3 LastPosition;\n        public int JumpCount;\n")
s = rep(s, "KeyframeMinFrameDifference = 0.03f;", "KeyframeMinFrameDifference = 0.045f;\n    private const float PoseJumpDegrees = 7f;\n    private const int PoseJumpPatience = 3;")
s = rep(s, """                Array.Copy(workerContext.KeyRotation, workerContext.WorldRotation, 9);
                workerContext.WorldPosition = workerContext.KeyPosition;
                return false;""", """                Array.Copy(workerContext.KeyRotation, workerContext.WorldRotation, 9);
                workerContext.WorldPosition = workerContext.KeyPosition;
                Array.Copy(workerContext.WorldRotation, workerContext.LastRotation, 9);
                workerContext.LastPosition = workerContext.WorldPosition;
                workerContext.JumpCount = 0;
                return false;""")
old = """        // world = keyframe * R
        var world = workerContext.WorldRotation;
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                world[row * 3 + column] = key[row * 3] * r[column] + key[row * 3 + 1] * r[3 + column] + key[row * 3 + 2] * r[6 + column];
        }
"""
new = """        // world = keyframe * R
        var world = workerContext.WorldRotation;
        Span<float> nextWorld = stackalloc float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                nextWorld[row * 3 + column] = key[row * 3] * r[column] + key[row * 3 + 1] * r[3 + column] + key[row * 3 + 2] * r[6 + column];
        }

        // One noisy pair must not throw the pose across the room: a jump is held back unless it persists
        var last = workerContext.LastRotation;
        var jumpTrace = 0f;
        for (int i = 0; i < 9; i++)
            jumpTrace += last[i] * nextWorld[i];

        var jumpDegrees = MathF.Acos(Math.Clamp((jumpTrace - 1f) * 0.5f, -1f, 1f)) * 180f / MathF.PI;
        if (jumpDegrees > PoseJumpDegrees && workerContext.HasPreviousView && workerContext.JumpCount < PoseJumpPatience)
        {
            workerContext.JumpCount++;
            Array.Copy(last, world, 9);
            workerContext.WorldPosition = workerContext.LastPosition;
            return false;
        }

        workerContext.JumpCount = 0;
        nextWorld.CopyTo(world);
        Array.Copy(world, last, 9);
        workerContext.LastPosition = workerContext.WorldPosition;
"""
s = rep(s, old, new)
save("DepthAnything.cs", s, crlf)

# ---- DepthScan3D: temporary diagnostics for the point count
s, crlf = load("DepthScan3D.cs")
s = rep(s, "        PointsAdded.Value = pointsAdded;\n        _captures++;",
        "        PointsAdded.Value = pointsAdded;\n        _captures++;\n        if (_captures % 50 == 0 || _loggedSpherical != _pendingSpherical)\n"
        "        {\n            _loggedSpherical = _pendingSpherical;\n            Log.Debug($\"[DepthScan3D] DIAG captures={_captures} points={_points.Count} spherical={_pendingSpherical} anchor={_anchorScale:F3}\");\n        }\n")
s = rep(s, "    private bool _pendingSpherical;", "    private bool _pendingSpherical;\n    private bool _loggedSpherical;")
save("DepthScan3D.cs", s, crlf)
print("ok")
