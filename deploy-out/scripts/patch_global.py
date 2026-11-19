base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"
GUID = "d7a3f5c1-2b84-4e96-9c1d-6f0a8b3e5d72"


def load(n):
    b = open(base + "\\" + n, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(n, s, crlf):
    open(base + "\\" + n, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c, count=1):
    assert s.count(a) == count, (s.count(a), a[:90])
    return s.replace(a, c)


s, crlf = load("DepthScan3D.cs")

# ---- input + pending flag
s = rep(s, f"    [Input(Guid = \"b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40\")]\n    public readonly InputSlot<bool> RegularizeRoom = new(false);\n",
        f"    [Input(Guid = \"b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40\")]\n    public readonly InputSlot<bool> RegularizeRoom = new(false);\n\n    [Input(Guid = \"{GUID}\")]\n    public readonly InputSlot<bool> GlobalAlign = new(true);\n")
s = rep(s, "        _pendingRegularize = RegularizeRoom.GetValue(context);\n", "        _pendingRegularize = RegularizeRoom.GetValue(context);\n        _pendingGlobalAlign = GlobalAlign.GetValue(context);\n")
s = rep(s, "    private bool _pendingRegularize;", "    private bool _pendingRegularize;\n    private bool _pendingGlobalAlign;")

# ---- tile storage
s = rep(s, "    private const float MeshScanJumpRatio = 1.25f;",
        "    private const float MeshScanJumpRatio = 1.25f;\n    private const int TileStride = 4;\n    private const int MaxTiles = 4000;\n    private const int MinTilesForAlignment = 8;")
s = rep(s, "    private volatile bool _meshBusy;", """    /** One capture's raw samples on the spherical grid: the unit of the global scale alignment. */
    private sealed class ScanTile
    {
        public int[] Cell = [];
        public float[] LogRange = [];
        public uint[] Color = [];
        public float[] Weight = [];
    }

    private readonly List<ScanTile> _tiles = [];
    private readonly List<int> _tileCells = [];
    private readonly List<float> _tileLogRanges = [];
    private readonly List<uint> _tileColors = [];
    private readonly List<float> _tileWeights = [];
    private volatile bool _meshBusy;""")

# record samples while merging
old = """                var inserted = _pendingSpherical ? InsertSpherical(position, r, g, b, now, weight) : InsertPoint(position, r, g, b, now);"""
new = """                // Raw (unanchored) samples are kept so every capture's depth scale can be solved jointly afterwards
                if (_pendingSpherical && _tiles.Count < MaxTiles && x % TileStride == 0 && y % TileStride == 0)
                {
                    var rawCell = SphericalCellIndex(UnprojectPoint(x, y, width, height, z * _pendingScale), out var rawRange);
                    if (rawCell >= 0)
                    {
                        _tileCells.Add(rawCell);
                        _tileLogRanges.Add(MathF.Log(rawRange));
                        _tileColors.Add(PackColor(r, g, b));
                        _tileWeights.Add(weight);
                    }
                }

                var inserted = _pendingSpherical ? InsertSpherical(position, r, g, b, now, weight) : InsertPoint(position, r, g, b, now);"""
s = rep(s, old, new)

old = "        PointsAdded.Value = pointsAdded;\n        _captures++;"
new = """        if (_tileCells.Count > 0)
        {
            _tiles.Add(new ScanTile { Cell = _tileCells.ToArray(), LogRange = _tileLogRanges.ToArray(), Color = _tileColors.ToArray(), Weight = _tileWeights.ToArray() });
            _tileCells.Clear();
            _tileLogRanges.Clear();
            _tileColors.Clear();
            _tileWeights.Clear();
        }

        PointsAdded.Value = pointsAdded;
        _captures++;"""
s = rep(s, old, new)

# reset
s = rep(s, "    private void ResetScan()\n    {\n", "    private void ResetScan()\n    {\n        _tiles.Clear();\n        _tileCells.Clear();\n        _tileLogRanges.Clear();\n        _tileColors.Clear();\n        _tileWeights.Clear();\n")

# ---- snapshot selection (live shell + mesh)
s = rep(s, "        var snapshot = _points.ToArray();\n        var regularize = _pendingRegularize;\n        var generation = _shellGeneration;",
        "        var tiles = _pendingGlobalAlign && _tiles.Count >= MinTilesForAlignment ? _tiles.ToArray() : null;\n        var snapshot = tiles == null ? _points.ToArray() : null;\n        var regularize = _pendingRegularize;\n        var generation = _shellGeneration;")
s = rep(s, "BuildRoomShell(snapshot, regularize, out _, out _, out _, out var positions, out var colors, out _);",
        "BuildRoomShell(snapshot ?? AlignTiles(tiles!), regularize, out _, out _, out _, out var positions, out var colors, out _);")
s = rep(s, "                var snapshot = _points.ToArray();\n                var regularize = _pendingRegularize;\n                _meshBusy = true;",
        "                var meshTiles = _pendingGlobalAlign && _tiles.Count >= MinTilesForAlignment ? _tiles.ToArray() : null;\n                var snapshot = meshTiles == null ? _points.ToArray() : null;\n                var regularize = _pendingRegularize;\n                _meshBusy = true;")
s = rep(s, "WriteSphericalMesh(absolutePath, snapshot, regularize);", "WriteSphericalMesh(absolutePath, snapshot ?? AlignTiles(meshTiles!), regularize);")
s = rep(s, "SetStatus($\"Building a mesh from {snapshot.Length} points...\", IStatusProvider.StatusLevel.Notice);",
        "SetStatus($\"Building a mesh from {snapshot?.Length ?? meshTiles!.Length} {(snapshot != null ? \"points\" : \"captures\")}...\", IStatusProvider.StatusLevel.Notice);")

# ---- the solver
solver = r'''    private static readonly int GridWidth = (int)MathF.Ceiling(2f * MathF.PI / SphericalCellRadians);
    private static readonly int GridHeight = (int)MathF.Ceiling(MathF.PI / SphericalCellRadians) + 1;

    private static uint PackColor(float r, float g, float b) =>
        ((uint)Math.Clamp((int)(r * 255f + 0.5f), 0, 255) << 16) | ((uint)Math.Clamp((int)(g * 255f + 0.5f), 0, 255) << 8) | (uint)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);

    /** Index of the 0.25 degree direction cell of a position seen from the origin; -1 when the position is the origin. */
    private static int SphericalCellIndex(System.Numerics.Vector3 position, out float range)
    {
        range = position.Length();
        if (range < 1e-5f)
            return -1;

        var elevation = MathF.Asin(Math.Clamp(position.Y / range, -1f, 1f));
        var azimuth = MathF.Atan2(position.X, -position.Z);
        var ix = Math.Clamp((int)MathF.Floor((azimuth + MathF.PI) / SphericalCellRadians), 0, GridWidth - 1);
        var iy = Math.Clamp((int)MathF.Floor((0.5f * MathF.PI - elevation) / SphericalCellRadians), 0, GridHeight - 1);
        return iy * GridWidth + ix;
    }

    /**
     * Solves one depth scale per capture from everywhere the captures overlap: every direction cell seen by several captures
     * must have one range, so log s_tile + log range is pulled to the cell's consensus (alternating least squares with
     * Huber weights, a weak prior toward the captured scale). Returns the map with every sample rescaled and averaged.
     * Relative depth has no scale of its own, so the sequential per-capture anchor alone chains errors; this uses all overlaps at once.
     */
    private static ScanPoint[] AlignTiles(ScanTile[] tiles)
    {
        const int iterations = 25;
        const float huber = 0.08f;
        const float prior = 4f;

        var total = 0;
        foreach (var tile in tiles)
            total += tile.Cell.Length;

        // Group samples by cell
        var cells = new int[total];
        var tileOf = new int[total];
        var logRange = new float[total];
        var weight = new float[total];
        var colorOf = new uint[total];
        var n = 0;
        for (var t = 0; t < tiles.Length; t++)
        {
            var tile = tiles[t];
            for (var i = 0; i < tile.Cell.Length; i++, n++)
            {
                cells[n] = tile.Cell[i];
                tileOf[n] = t;
                logRange[n] = tile.LogRange[i];
                weight[n] = tile.Weight[i];
                colorOf[n] = tile.Color[i];
            }
        }

        var order = new int[total];
        for (var i = 0; i < total; i++)
            order[i] = i;

        Array.Sort(cells.AsSpan().ToArray(), order);
        var sortedCells = new int[total];
        for (var i = 0; i < total; i++)
            sortedCells[i] = cells[order[i]];

        // Cell group boundaries; only cells seen by more than one sample constrain the scales
        var groupStart = new List<int>();
        for (var i = 0; i < total; i++)
        {
            if (i == 0 || sortedCells[i] != sortedCells[i - 1])
                groupStart.Add(i);
        }

        groupStart.Add(total);
        var groups = groupStart.Count - 1;

        var x = new float[tiles.Length];
        var robust = new float[total];
        for (var i = 0; i < total; i++)
            robust[i] = 1f;

        var tileNumerator = new double[tiles.Length];
        var tileDenominator = new double[tiles.Length];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(tileNumerator);
            Array.Clear(tileDenominator);
            for (var g = 0; g < groups; g++)
            {
                var a = groupStart[g];
                var b = groupStart[g + 1];
                if (b - a < 2)
                    continue;

                double sum = 0, sumW = 0;
                for (var k = a; k < b; k++)
                {
                    var o = order[k];
                    var w = weight[o] * robust[o];
                    sum += w * (x[tileOf[o]] + logRange[o]);
                    sumW += w;
                }

                if (sumW < 1e-9)
                    continue;

                var consensus = sum / sumW;
                for (var k = a; k < b; k++)
                {
                    var o = order[k];
                    var w = weight[o] * robust[o];
                    tileNumerator[tileOf[o]] += w * (consensus - logRange[o]);
                    tileDenominator[tileOf[o]] += w;
                    var residual = MathF.Abs((float)(x[tileOf[o]] + logRange[o] - consensus));
                    robust[o] = residual <= huber ? 1f : huber / residual;
                }
            }

            for (var t = 0; t < tiles.Length; t++)
                x[t] = (float)(tileNumerator[t] / (tileDenominator[t] + prior));
        }

        // The aligned map: per cell the robust mean of the rescaled samples
        var points = new List<ScanPoint>();
        for (var g = 0; g < groups; g++)
        {
            var a = groupStart[g];
            var b = groupStart[g + 1];
            double sum = 0, sumW = 0, cr = 0, cg = 0, cb = 0;
            for (var k = a; k < b; k++)
            {
                var o = order[k];
                var w = weight[o] * robust[o];
                sum += w * (x[tileOf[o]] + logRange[o]);
                sumW += w;
                cr += w * ((colorOf[o] >> 16) & 255);
                cg += w * ((colorOf[o] >> 8) & 255);
                cb += w * (colorOf[o] & 255);
            }

            if (sumW < 1e-9)
                continue;

            var cell = sortedCells[a];
            var cellX = cell % GridWidth;
            var cellY = cell / GridWidth;
            var elevation = 0.5f * MathF.PI - (cellY + 0.5f) * SphericalCellRadians;
            var azimuth = (cellX + 0.5f) * SphericalCellRadians - MathF.PI;
            var range = (float)Math.Exp(sum / sumW);
            var cosE = MathF.Cos(elevation);
            points.Add(new ScanPoint
            {
                Position = new System.Numerics.Vector3(range * cosE * MathF.Sin(azimuth), range * MathF.Sin(elevation), -range * cosE * MathF.Cos(azimuth)),
                Color = new System.Numerics.Vector3((float)(cr / sumW) / 255f, (float)(cg / sumW) / 255f, (float)(cb / sumW) / 255f),
                Count = b - a,
                Weight = (float)sumW,
            });
        }

        return points.ToArray();
    }

'''
marker = "    /**\n     * Turns the spherical range map into a closed triangle mesh"
assert s.count(marker) == 1
s = s.replace(marker, solver + marker)
save("DepthScan3D.cs", s, crlf)

t3, c3 = load("DepthScan3D.t3")
t3 = rep(t3, '    {\n      "Id": "b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40"/*RegularizeRoom*/,\n      "DefaultValue": false\n    },',
         '    {\n      "Id": "b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40"/*RegularizeRoom*/,\n      "DefaultValue": false\n    },\n'
         f'    {{\n      "Id": "{GUID}"/*GlobalAlign*/,\n      "DefaultValue": true\n    }},')
save("DepthScan3D.t3", t3, c3)

ui, cu = load("DepthScan3D.t3ui")
marker = '      "InputId": "b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40"/*RegularizeRoom*/,'
assert ui.count(marker) == 1
# append after the RegularizeRoom entry (which closes with "    }\n  ],")
tail = 'Only suits rectangular rooms; leave off for objects and irregular spaces."\n    }\n  ],'
assert ui.count(tail) == 1
ui = ui.replace(tail, 'Only suits rectangular rooms; leave off for objects and irregular spaces."\n    },\n'
                f'    {{\n      "InputId": "{GUID}"/*GlobalAlign*/,\n      "Position": {{\n        "X": -250.0,\n        "Y": 360.0\n      }},\n'
                '      "Description": "For a camera that only rotates: solves every capture\'s depth scale jointly from all the places the captures overlap, instead of chaining them one after another. Removes the stepped walls relative depth models produce between captures. Needs about eight captures."\n    }\n  ],')
save("DepthScan3D.t3ui", ui, cu)
print("ok")
