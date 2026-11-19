import re
base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"


def load(name):
    b = open(base + "\\" + name, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(name, s, crlf):
    open(base + "\\" + name, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c):
    assert s.count(a) == 1, (s.count(a), a[:70])
    return s.replace(a, c)


s, crlf = load("DepthScan3D.cs")

# diagnostics out
n = re.sub(r"        if \(_captures % 50 == 0 \|\| _loggedSpherical != _pendingSpherical\)\n        \{\n.*?\n        \}\n", "", s, flags=re.S, count=1)
assert n != s
s = n.replace("    private bool _loggedSpherical;\n", "")

# wider anchor clamp
s = rep(s, "anchor = float.IsFinite(estimate) ? Math.Clamp(estimate, 0.5f, 2f) : _anchorScale;",
        "anchor = float.IsFinite(estimate) ? Math.Clamp(estimate, 0.25f, 4f) : _anchorScale;")
s = rep(s, "if (slope is > 0.5 and < 2.0 && Math.Abs(offset) < 0.4 * meanY)", "if (slope is > 0.25 and < 4.0 && Math.Abs(offset) < 0.4 * meanY)")

# create save folder
s = rep(s, "            if (format == PlyFormat.Mesh)\n            {",
        "            var directory = Path.GetDirectoryName(absolutePath);\n            if (!string.IsNullOrEmpty(directory))\n                Directory.CreateDirectory(directory);\n\n            if (format == PlyFormat.Mesh)\n            {")

# mesh: gentler jump gate and boundary loop closing
s = rep(s, "MeshDepthJumpRatio = 1.12f;", "MeshDepthJumpRatio = 1.25f;\n    private const int MeshMaxHoleEdges = 400;")
s = rep(s, "        // Close the rim at the lowest elevation seen in each column with a fan to a point on the floor\n",
        "        CloseSmallHoles(positions, colors, faces, AddFace);\n\n        // Close the rim at the lowest elevation seen in each column with a fan to a point on the floor\n")

closer = r'''    /** Fans over every boundary loop of at most MeshMaxHoleEdges edges, so gaps left by depth jumps and missing cells are solid. */
    private static void CloseSmallHoles(List<System.Numerics.Vector3> positions, List<System.Numerics.Vector3> colors, List<int> faces,
                                        Action<int, int, int> addFace)
    {
        var directed = new HashSet<long>();
        for (var i = 0; i < faces.Count; i += 3)
        {
            for (var k = 0; k < 3; k++)
                directed.Add(((long)faces[i + k] << 32) | (uint)faces[i + (k + 1) % 3]);
        }

        var next = new Dictionary<int, int>();
        foreach (var edge in directed)
        {
            var from = (int)(edge >> 32);
            var to = (int)(edge & 0xFFFFFFFF);
            if (!directed.Contains(((long)to << 32) | (uint)from))
                next[to] = from;
        }

        var visited = new HashSet<int>();
        var loop = new List<int>();
        foreach (var start in next.Keys)
        {
            if (visited.Contains(start))
                continue;

            loop.Clear();
            var current = start;
            var closed = false;
            while (loop.Count <= MeshMaxHoleEdges && next.TryGetValue(current, out var following))
            {
                if (!visited.Add(current))
                    break;

                loop.Add(current);
                current = following;
                if (current == start)
                {
                    closed = true;
                    break;
                }
            }

            if (!closed || loop.Count < 3)
                continue;

            var center = System.Numerics.Vector3.Zero;
            var color = System.Numerics.Vector3.Zero;
            foreach (var vertex in loop)
            {
                center += positions[vertex];
                color += colors[vertex];
            }

            var centerIndex = positions.Count;
            positions.Add(center / loop.Count);
            colors.Add(color / loop.Count);
            for (var i = 0; i < loop.Count; i++)
                addFace(loop[i], loop[(i + 1) % loop.Count], centerIndex);
        }
    }

'''
s = rep(s, "    /** 3x3 median of the range grid", closer + "    /** 3x3 median of the range grid")
save("DepthScan3D.cs", s, crlf)

# PtzScanPath: capture window after settling in stop mode
s, crlf = load("PtzScanPath.cs")
s = rep(s, """                case Phase.Settling:
                    // Depth smoothing and the video delay both trail the arrival
                    if (now - _phaseStartTime >= settleSec)
                    {
                        capture = true;
                        _phase = Phase.Captured;
                        _phaseStartTime = now;
                    }

                    break;
""", """                case Phase.Settling:
                    // Depth smoothing and the video delay both trail the arrival
                    if (now - _phaseStartTime >= settleSec)
                    {
                        _phase = Phase.Capturing;
                        _phaseStartTime = now;
                    }

                    break;

                case Phase.Capturing:
                    // Scanning stays on for a short window of still frames that merge into one sharp capture
                    scanning = true;
                    if (now - _phaseStartTime >= CaptureWindowSec)
                    {
                        capture = true;
                        _phase = Phase.Captured;
                        _phaseStartTime = now;
                    }

                    break;
""")
s = rep(s, "        Settling,\n        Captured,", "        Settling,\n        Capturing,\n        Captured,")
s = rep(s, "    private readonly List<(float Pan, float Tilt)> _stops = new();", "    private const float CaptureWindowSec = 0.6f;\n\n    private readonly List<(float Pan, float Tilt)> _stops = new();")
save("PtzScanPath.cs", s, crlf)
print("ok")
