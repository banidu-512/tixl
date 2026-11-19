p = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.cs"
b = open(p, 'rb').read()
crlf = b'\r\n' in b
s = b.decode().replace('\r\n', '\n')


def rep(a, c):
    global s
    assert s.count(a) == 1, (s.count(a), a[:70])
    s = s.replace(a, c)


# 1) extract the shell builder out of WriteSphericalMesh
head = "    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints)\n    {\n"
i = s.index(head) + len(head)
j = s.index("        var faces = new List<int>();\n", i)
body = s[i:j]
body = (body.replace("var w2 = ", "w2 = ").replace("var h2 = ", "h2 = ").replace("var vertexIndex = ", "vertexIndex = ")
        .replace("var gridRange = ", "gridRange = ").replace("var positions = ", "positions = ").replace("var colors = ", "colors = "))
shell_fn = ('''    /** Bins the scan into a range map, fits the room box and returns the box-snapped shell as a half-degree vertex grid. */
    private static void BuildRoomShell(ScanPoint[] scanPoints, out int w2, out int h2, out float[] gridRange,
                                       out List<System.Numerics.Vector3> positions, out List<System.Numerics.Vector3> colors,
                                       out int[] vertexIndex)
    {
''' + body + "    }\n\n")
s = s[:i] + ("        BuildRoomShell(scanPoints, out var w2, out var h2, out var gridRange, out var positions, out var colors, out var vertexIndex);\n\n") + s[j:]
k = s.index("    /**\n     * Turns the spherical range map into a closed triangle mesh")
s = s[:k] + shell_fn + s[k:]

# 2) live output: shell points built in the background
live = '''    /** The live cloud of a rotation-only scan is the room shell, rebuilt off-thread whenever new captures arrived. */
    private void MaybeStartShell()
    {
        if (_shellBusy || _points.Count < 3000 || _captures == _shellCaptures)
            return;

        _shellBusy = true;
        _shellCaptures = _captures;
        var snapshot = _points.ToArray();
        var generation = _shellGeneration;
        System.Threading.Tasks.Task.Run(() =>
                                        {
                                            try
                                            {
                                                BuildRoomShell(snapshot, out _, out _, out _, out var positions, out var colors, out _);
                                                var shell = new Point[positions.Count];
                                                for (var i = 0; i < shell.Length; i++)
                                                {
                                                    shell[i] = new Point
                                                    {
                                                        Position = positions[i],
                                                        Color = new System.Numerics.Vector4(colors[i], 1f),
                                                        Orientation = System.Numerics.Quaternion.Identity,
                                                        Scale = System.Numerics.Vector3.One,
                                                        F1 = 1,
                                                        F2 = 1,
                                                    };
                                                }

                                                if (generation == _shellGeneration)
                                                {
                                                    _shellPoints = shell;
                                                    _shellReady = true;
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                Log.Warning($"[DepthScan3D] Room shell failed: {e.Message}");
                                            }
                                            finally
                                            {
                                                _shellBusy = false;
                                            }
                                        });
    }

'''
rep("    private void RebuildOutput()\n    {\n        var count = _points.Count;\n        if (count == 0)\n        {\n            Points.Value = null;\n            PointCount.Value = 0;\n            return;\n        }\n",
    live + """    private void RebuildOutput()
    {
        var count = _points.Count;
        if (count == 0)
        {
            Points.Value = null;
            PointCount.Value = 0;
            return;
        }

        if (_pendingSpherical)
        {
            MaybeStartShell();
            if (_shellPoints != null)
            {
                if (_shellReady)
                {
                    _shellReady = false;
                    EnsurePointBuffer(_shellPoints, _shellPoints.Length);
                    Points.Value = _pointBuffer;
                }

                PointCount.Value = count;
                return;
            }
        }
""")
rep("    private volatile bool _meshBusy;", "    private volatile bool _meshBusy;\n    private volatile bool _shellBusy;\n    private volatile bool _shellReady;\n    private volatile int _shellGeneration;\n    private volatile Point[]? _shellPoints;\n    private int _shellCaptures;")
rep("    private void ResetScan()\n    {\n", "    private void ResetScan()\n    {\n        _shellGeneration++;\n        _shellPoints = null;\n        _shellReady = false;\n        _shellCaptures = 0;\n")

# 3) keep the shell fresh after the last merge too
rep("        HandleSave(context);\n", "        if (_pendingSpherical && _points.Count > 0)\n            MaybeStartShell();\n\n        HandleSave(context);\n")
open(p, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode())
print("ok")
