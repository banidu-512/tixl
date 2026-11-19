base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"
GUID = "e9b4c7a3-6d15-4b82-a3f0-1c8d5e7b2a96"


def load(n):
    b = open(base + "\\" + n, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(n, s, crlf):
    open(base + "\\" + n, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c, count=1):
    assert s.count(a) == count, (s.count(a), a[:90])
    return s.replace(a, c)


s, crlf = load("DepthScan3D.cs")

# ---- inputs and flags
s = rep(s, "    public readonly InputSlot<bool> GlobalAlign = new(true);",
        f"    public readonly InputSlot<bool> GlobalAlign = new(false);\n\n    [Input(Guid = \"{GUID}\")]\n    public readonly InputSlot<bool> FlatSurfaces = new(true);")
s = rep(s, "        _pendingGlobalAlign = GlobalAlign.GetValue(context);\n", "        _pendingGlobalAlign = GlobalAlign.GetValue(context);\n        _pendingFlat = FlatSurfaces.GetValue(context);\n", 2)
s = rep(s, "    private bool _pendingGlobalAlign;", "    private bool _pendingGlobalAlign;\n    private bool _pendingFlat;")

# ---- shell rebuild triggers
s = rep(s, "(_captures == _shellCaptures && aligned == _shellAligned && RegularizeUnchanged())", "(_captures == _shellCaptures && aligned == _shellAligned && RegularizeUnchanged() && _shellFlat == _pendingFlat)")
s = rep(s, "        _shellRegularize = _pendingRegularize;", "        _shellRegularize = _pendingRegularize;\n        _shellFlat = _pendingFlat;")
s = rep(s, "    private bool _shellRegularize;", "    private bool _shellRegularize;\n    private bool _shellFlat;")

# ---- callers pass the flag
s = rep(s, "BuildRoomShell(snapshot ?? AlignTiles(tiles!), regularize, out _,", "BuildRoomShell(snapshot ?? AlignTiles(tiles!), regularize, flat, out _,")
s = rep(s, "        var regularize = _pendingRegularize;\n        var generation = _shellGeneration;", "        var regularize = _pendingRegularize;\n        var flat = _pendingFlat;\n        var generation = _shellGeneration;")
s = rep(s, "WriteSphericalMesh(absolutePath, snapshot ?? AlignTiles(meshTiles!), regularize);", "WriteSphericalMesh(absolutePath, snapshot ?? AlignTiles(meshTiles!), regularize, flat);")
s = rep(s, "                var regularize = _pendingRegularize;\n                _meshBusy = true;", "                var regularize = _pendingRegularize;\n                var flat = _pendingFlat;\n                _meshBusy = true;")
s = rep(s, "private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints, bool regularize)\n    {\n        BuildRoomShell(scanPoints, regularize, out var w2,",
        "private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints, bool regularize, bool flat)\n    {\n        BuildRoomShell(scanPoints, regularize, flat, out var w2,")
s = rep(s, "private static void BuildRoomShell(ScanPoint[] scanPoints, bool regularize, out int w2,", "private static void BuildRoomShell(ScanPoint[] scanPoints, bool regularize, bool flat, out int w2,")

# ---- shell builder: observe first, flatten, then emit
i = s.index("        for (var y = 0; y < h2; y++)\n        {\n            var elevation = 0.5f * MathF.PI - (y + 0.5f) * SphericalCellRadians * downsample;")
j = s.index("    private static readonly int GridWidth = ")
new_emit = '''        // First pass: the observed range and colour of every output cell
        var observedRange = new float[w2 * h2];
        var observedColor = new System.Numerics.Vector3[w2 * h2];
        for (var y = 0; y < h2; y++)
        {
            for (var x = 0; x < w2; x++)
            {
                var sum = 0f;
                var colorSum = System.Numerics.Vector3.Zero;
                var n = 0;
                for (var by = 0; by < downsample; by++)
                {
                    for (var bx = 0; bx < downsample; bx++)
                    {
                        var index = (y * downsample + by) * width + x * downsample + bx;
                        if (float.IsNaN(range[index]))
                            continue;

                        sum += range[index];
                        colorSum += color[index];
                        n++;
                    }
                }

                observedRange[y * w2 + x] = n > 0 ? sum / n : float.NaN;
                observedColor[y * w2 + x] = n > 0 ? colorSum / n : default;
            }
        }

        if (flat && !regularize)
            FlattenPlanes(observedRange, w2, h2);

        for (var y = 0; y < h2; y++)
        {
            var elevation = 0.5f * MathF.PI - (y + 0.5f) * SphericalCellRadians * downsample;
            var cosE = MathF.Cos(elevation);
            var sinE = MathF.Sin(elevation);
            for (var x = 0; x < w2; x++)
            {
                var azimuth = (x + 0.5f) * SphericalCellRadians * downsample - MathF.PI;
                var dx = cosE * MathF.Sin(azimuth);
                var dz = -cosE * MathF.Cos(azimuth);
                var boxRange = regularize ? box.RangeAlong(dx, sinE, dz) : float.MaxValue;
                var observed = observedRange[y * w2 + x];

                // Without a box only what was seen becomes geometry; with one, walls and anything not clearly in
                // front of them take the box, and unseen directions are filled from it
                var r = boxRange;
                var cellColor = new System.Numerics.Vector3(0.4f, 0.4f, 0.4f);
                if (!float.IsNaN(observed))
                {
                    cellColor = observedColor[y * w2 + x];
                    if (!regularize || observed < boxRange * RoomObjectRatio)
                        r = observed;
                }
                else if (!regularize)
                {
                    gridRange[y * w2 + x] = float.NaN;
                    vertexIndex[y * w2 + x] = -1;
                    continue;
                }

                gridRange[y * w2 + x] = r;
                vertexIndex[y * w2 + x] = positions.Count;
                positions.Add(new System.Numerics.Vector3(r * dx, r * sinE, r * dz));
                colors.Add(cellColor);
            }
        }
    }

'''
s = s[:i] + new_emit + s[j:]

planes = r'''    /**
     * Flattens the surfaces of a rotation-only scan onto planes found in the scan itself: no box and no right angles, so tilted
     * ceilings, alcoves and desks work as well as walls. Surface normals come from the range grid; planes are found by
     * RANSAC on (position, normal) pairs, largest first. Cells whose normal agrees with a plane and that lie within a generous
     * band of it (relative depth makes one wall appear at several distances) are moved onto it along their viewing ray.
     * Cells that fit no plane (clutter, edges) keep their range.
     */
    private static void FlattenPlanes(float[] range, int w, int h)
    {
        const float depthJump = 0.12f;
        const float normalCos = 0.9f;          // ~25 degrees
        const float ransacBand = 0.035f;       // fraction of range
        const float assignBand = 0.16f;
        const int candidates = 300;
        const int sampleSize = 5000;
        const int maxPlanes = 16;

        var count = w * h;
        var positions = new System.Numerics.Vector3[count];
        var directions = new System.Numerics.Vector3[count];
        for (var y = 0; y < h; y++)
        {
            var elevation = 0.5f * MathF.PI - (y + 0.5f) * 2f * SphericalCellRadians;
            var cosE = MathF.Cos(elevation);
            var sinE = MathF.Sin(elevation);
            for (var x = 0; x < w; x++)
            {
                var azimuth = (x + 0.5f) * 2f * SphericalCellRadians - MathF.PI;
                var d = new System.Numerics.Vector3(cosE * MathF.Sin(azimuth), sinE, -cosE * MathF.Cos(azimuth));
                directions[y * w + x] = d;
                if (!float.IsNaN(range[y * w + x]))
                    positions[y * w + x] = d * range[y * w + x];
            }
        }

        // Normals from central differences, facing the camera; edges (depth jumps) get none
        var normals = new System.Numerics.Vector3[count];
        var hasNormal = new bool[count];
        var valid = new List<int>();
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = y * w + x;
                var left = y * w + (x + w - 1) % w;
                var right = y * w + (x + 1) % w;
                var up = (y - 1) * w + x;
                var down = (y + 1) * w + x;
                var rc = range[c];
                if (float.IsNaN(rc) || float.IsNaN(range[left]) || float.IsNaN(range[right]) || float.IsNaN(range[up]) || float.IsNaN(range[down]))
                    continue;

                if (MathF.Abs(range[left] - rc) > depthJump * rc || MathF.Abs(range[right] - rc) > depthJump * rc
                    || MathF.Abs(range[up] - rc) > depthJump * rc || MathF.Abs(range[down] - rc) > depthJump * rc)
                    continue;

                var n = System.Numerics.Vector3.Cross(positions[right] - positions[left], positions[up] - positions[down]);
                var length = n.Length();
                if (length < 1e-9f)
                    continue;

                n /= length;
                if (System.Numerics.Vector3.Dot(n, positions[c]) > 0f)
                    n = -n;

                normals[c] = n;
                hasNormal[c] = true;
                valid.Add(c);
            }
        }

        if (valid.Count < 500)
            return;

        var random = new Random(11);
        var remaining = new List<int>(valid);
        var planeNormal = new List<System.Numerics.Vector3>();
        var planeOffset = new List<float>();
        var minSupport = Math.Max(150, valid.Count / 80);
        for (var plane = 0; plane < maxPlanes && remaining.Count >= minSupport; plane++)
        {
            var sample = new int[Math.Min(sampleSize, remaining.Count)];
            for (var i = 0; i < sample.Length; i++)
                sample[i] = remaining[random.Next(remaining.Count)];

            var bestScore = 0;
            var bestNormal = System.Numerics.Vector3.Zero;
            var bestOffset = 0f;
            for (var k = 0; k < candidates; k++)
            {
                var seed = remaining[random.Next(remaining.Count)];
                var n = normals[seed];
                var d = -System.Numerics.Vector3.Dot(n, positions[seed]);
                var score = 0;
                for (var i = 0; i < sample.Length; i++)
                {
                    var j = sample[i];
                    if (System.Numerics.Vector3.Dot(normals[j], n) < normalCos)
                        continue;

                    if (MathF.Abs(System.Numerics.Vector3.Dot(n, positions[j]) + d) < ransacBand * range[j])
                        score++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNormal = n;
                    bestOffset = d;
                }
            }

            if (bestScore * remaining.Count / sample.Length < minSupport)
                break;

            // Refine on every remaining cell that agrees, then drop them from the pool
            var sumNormal = System.Numerics.Vector3.Zero;
            var inliers = new List<int>();
            foreach (var j in remaining)
            {
                if (System.Numerics.Vector3.Dot(normals[j], bestNormal) >= normalCos
                    && MathF.Abs(System.Numerics.Vector3.Dot(bestNormal, positions[j]) + bestOffset) < 2f * ransacBand * range[j])
                {
                    inliers.Add(j);
                    sumNormal += normals[j];
                }
            }

            if (inliers.Count < minSupport || sumNormal.LengthSquared() < 1e-9f)
                break;

            var refined = System.Numerics.Vector3.Normalize(sumNormal);
            var offset = 0f;
            foreach (var j in inliers)
                offset -= System.Numerics.Vector3.Dot(refined, positions[j]);

            offset /= inliers.Count;
            planeNormal.Add(refined);
            planeOffset.Add(offset);
            var inlierSet = new HashSet<int>(inliers);
            remaining.RemoveAll(inlierSet.Contains);
        }

        // Every cell with a normal takes the largest plane it agrees with, within a generous band, along its own viewing ray
        var flattened = (float[])range.Clone();
        foreach (var c in valid)
        {
            for (var p = 0; p < planeNormal.Count; p++)
            {
                var n = planeNormal[p];
                if (System.Numerics.Vector3.Dot(normals[c], n) < normalCos)
                    continue;

                var facing = System.Numerics.Vector3.Dot(n, directions[c]);
                if (facing > -0.15f)
                    continue;

                var onPlane = -planeOffset[p] / facing;
                if (onPlane > range[c] * (1f - assignBand) && onPlane < range[c] * (1f + assignBand))
                {
                    flattened[c] = onPlane;
                    break;
                }
            }
        }

        Array.Copy(flattened, range, count);
    }

'''
marker = "    /** A room as a box rotated about the vertical axis"
assert s.count(marker) == 1
s = s.replace(marker, planes + marker)
save("DepthScan3D.cs", s, crlf)

# ---- symbol definition + UI
t3, c3 = load("DepthScan3D.t3")
t3 = rep(t3, '      "Id": "d7a3f5c1-2b84-4e96-9c1d-6f0a8b3e5d72"/*GlobalAlign*/,\n      "DefaultValue": true\n    },',
         '      "Id": "d7a3f5c1-2b84-4e96-9c1d-6f0a8b3e5d72"/*GlobalAlign*/,\n      "DefaultValue": false\n    },\n'
         f'    {{\n      "Id": "{GUID}"/*FlatSurfaces*/,\n      "DefaultValue": true\n    }},')
save("DepthScan3D.t3", t3, c3)

ui, cu = load("DepthScan3D.t3ui")
tail = 'Needs about eight captures."\n    }\n  ],'
assert ui.count(tail) == 1
ui = ui.replace(tail, 'Needs about eight captures. Experimental."\n    },\n'
                f'    {{\n      "InputId": "{GUID}"/*FlatSurfaces*/,\n      "Position": {{\n        "X": -250.0,\n        "Y": 400.0\n      }},\n'
                '      "Description": "For a camera that only rotates: finds the planes in the scan (walls, floor, ceiling, tables; any orientation, no box assumed) and moves the surfaces that lie on them onto flat planes. Clutter that fits no plane keeps its shape."\n    }\n  ],')
save("DepthScan3D.t3ui", ui, cu)
print("ok")
