base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"
GUID = "b4c8e1a2-5d37-4f90-8a6b-3e2f1c7d9a40"


def load(n):
    b = open(base + "\\" + n, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(n, s, crlf):
    open(base + "\\" + n, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c, count=1):
    assert s.count(a) == count, (s.count(a), a[:80])
    return s.replace(a, c)


s, crlf = load("DepthScan3D.cs")
s = rep(s, "    [Input(Guid = \"8a4f0d63-2e91-4b57-a3c8-6f5d1e2b4c70\")]\n    public readonly InputSlot<bool> AutoScale = new(true);\n",
        "    [Input(Guid = \"8a4f0d63-2e91-4b57-a3c8-6f5d1e2b4c70\")]\n    public readonly InputSlot<bool> AutoScale = new(true);\n\n"
        f"    [Input(Guid = \"{GUID}\")]\n    public readonly InputSlot<bool> RegularizeRoom = new(false);\n")
s = rep(s, "        _pendingAutoScale = AutoScale.GetValue(context);\n", "        _pendingAutoScale = AutoScale.GetValue(context);\n        _pendingRegularize = RegularizeRoom.GetValue(context);\n")
s = rep(s, "    private bool _pendingAutoScale;", "    private bool _pendingAutoScale;\n    private bool _pendingRegularize;")

# shell builder: optional box
s = rep(s, "    private static void BuildRoomShell(ScanPoint[] scanPoints, out int w2, out int h2, out float[] gridRange,",
        "    private static void BuildRoomShell(ScanPoint[] scanPoints, bool regularize, out int w2, out int h2, out float[] gridRange,")
s = rep(s, "        var box = FitRoomBox(range, width, height);\n", "        var box = regularize ? FitRoomBox(range, width, height) : default;\n")
s = rep(s, "                var boxRange = box.RangeAlong(dx, sinE, dz);\n", "                var boxRange = regularize ? box.RangeAlong(dx, sinE, dz) : float.MaxValue;\n")
old = """                // Walls and anything not clearly in front of them take the box; objects in front keep their own range
                var r = boxRange;
                var cellColor = new System.Numerics.Vector3(0.4f, 0.4f, 0.4f);
                if (n > 0)
                {
                    cellColor = colorSum / n;
                    var observed = sum / n;
                    if (observed < boxRange * RoomObjectRatio)
                        r = observed;
                }

                gridRange[y * w2 + x] = r;
                vertexIndex[y * w2 + x] = positions.Count;
"""
new = """                // Without a box only what was seen becomes geometry; with one, walls and anything not clearly in
                // front of them take the box, and unseen directions are filled from it
                var r = boxRange;
                var cellColor = new System.Numerics.Vector3(0.4f, 0.4f, 0.4f);
                if (n > 0)
                {
                    cellColor = colorSum / n;
                    var observed = sum / n;
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
"""
s = rep(s, old, new)
s = rep(s, """                var lo = MathF.Min(MathF.Min(r00, r10), MathF.Min(r01, r11));
                var hi = MathF.Max(MathF.Max(r00, r10), MathF.Max(r01, r11));
                if (hi > lo * MeshDepthJumpRatio)
                    continue;

                AddFace(i00, i10, i01);
                AddFace(i10, i11, i01);""", """                if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                    continue;

                var lo = MathF.Min(MathF.Min(r00, r10), MathF.Min(r01, r11));
                var hi = MathF.Max(MathF.Max(r00, r10), MathF.Max(r01, r11));
                if (hi > lo * (regularize ? MeshDepthJumpRatio : MeshScanJumpRatio))
                    continue;

                AddFace(i00, i10, i01);
                AddFace(i10, i11, i01);""")
s = rep(s, "    private const float MeshDepthJumpRatio = 1.6f;", "    private const float MeshDepthJumpRatio = 1.6f;\n    private const float MeshScanJumpRatio = 1.25f;\n    private const int MeshSmallHoleEdges = 400;")
s = rep(s, "    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints)\n    {",
        "    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints, bool regularize)\n    {")
s = rep(s, "        BuildRoomShell(scanPoints, out var w2,", "        BuildRoomShell(scanPoints, regularize, out var w2,")
s = rep(s, "        CloseHoles(positions, colors, faces, AddFace);\n", "        CloseHoles(positions, colors, faces, AddFace, regularize);\n")
s = rep(s, "                                   Action<int, int, int> addFace)\n    {\n        // Tuple keys", "                                   Action<int, int, int> addFace, bool closeLargeLoops)\n    {\n        // Tuple keys")
s = rep(s, "                if (!closed || loop.Count < 3)\n                    continue;\n\n                var center = System.Numerics.Vector3.Zero;",
        "                if (!closed || loop.Count < 3 || (!closeLargeLoops && loop.Count > MeshSmallHoleEdges))\n                    continue;\n\n                var center = System.Numerics.Vector3.Zero;")
# callers
s = rep(s, "var (vertices, triangles) = WriteSphericalMesh(absolutePath, snapshot);", "var (vertices, triangles) = WriteSphericalMesh(absolutePath, snapshot, regularize);")
s = rep(s, "                var snapshot = _points.ToArray();\n                _meshBusy = true;", "                var snapshot = _points.ToArray();\n                var regularize = _pendingRegularize;\n                _meshBusy = true;")
s = rep(s, "        var snapshot = _points.ToArray();\n        var generation = _shellGeneration;", "        var snapshot = _points.ToArray();\n        var regularize = _pendingRegularize;\n        var generation = _shellGeneration;")
s = rep(s, "BuildRoomShell(snapshot, out _, out _, out _, out var positions, out var colors, out _);", "BuildRoomShell(snapshot, regularize, out _, out _, out _, out var positions, out var colors, out _);")
s = s.replace('"PLY mesh (room box)"', '"PLY mesh"')
save("DepthScan3D.cs", s, crlf)

t3, c3 = load("DepthScan3D.t3")
t3 = rep(t3, '    {\n      "Id": "6c269444-aaec-4359-9708-4eb2cfdd0487"/*SaveTrigger*/,',
         f'    {{\n      "Id": "{GUID}"/*RegularizeRoom*/,\n      "DefaultValue": false\n    }},\n    {{\n      "Id": "6c269444-aaec-4359-9708-4eb2cfdd0487"/*SaveTrigger*/,')
save("DepthScan3D.t3", t3, c3)

ui, cu = load("DepthScan3D.t3ui")
ui = rep(ui, '      "Description": "Anchors each capture\'s depth scale against the points already merged, so relative depth wobble cannot stack repeated copies of the same surfaces at increasing range. Turn off for metric depth models."\n    }\n  ],',
         '      "Description": "Anchors each capture\'s depth scale against the points already merged, so relative depth wobble cannot stack repeated copies of the same surfaces at increasing range. Turn off for metric depth models."\n    },\n'
         '    {\n      "InputId": "%s"/*RegularizeRoom*/,\n      "Position": {\n        "X": -250.0,\n        "Y": 320.0\n      },\n      "Description": "For a camera that only rotates (a pan/tilt head): fit a box to the scan and snap walls, floor and ceiling to it. Only suits rectangular rooms; leave off for objects and irregular spaces."\n    }\n  ],' % GUID)
save("DepthScan3D.t3ui", ui, cu)
print("ok")
