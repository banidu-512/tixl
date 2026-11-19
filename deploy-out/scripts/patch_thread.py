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

s = rep(s, """                var (vertices, triangles) = WriteSphericalMesh(absolutePath);
                SavedPath.Value = relativePath;
                SetStatus($"Saved a mesh of {vertices} vertices and {triangles} triangles to {relativePath}", IStatusProvider.StatusLevel.Success);""",
        """                if (_meshBusy)
                {
                    SetStatus("A mesh is still being built.", IStatusProvider.StatusLevel.Warning);
                    return;
                }

                // The meshing walks millions of cells: it runs on a snapshot off the editor thread
                var snapshot = _points.ToArray();
                _meshBusy = true;
                SetStatus($"Building a mesh from {snapshot.Length} points...", IStatusProvider.StatusLevel.Notice);
                System.Threading.Tasks.Task.Run(() =>
                                                {
                                                    try
                                                    {
                                                        var watch = Stopwatch.StartNew();
                                                        var (vertices, triangles) = WriteSphericalMesh(absolutePath, snapshot);
                                                        _meshResultPath = relativePath;
                                                        _meshResultMessage = $"Saved a mesh of {vertices} vertices and {triangles} triangles to {relativePath} ({watch.Elapsed.TotalSeconds:F1} s)";
                                                    }
                                                    catch (Exception e)
                                                    {
                                                        _meshResultPath = string.Empty;
                                                        _meshResultMessage = $"Building the mesh failed: {e.Message}";
                                                    }
                                                    finally
                                                    {
                                                        _meshBusy = false;
                                                        _meshDone = true;
                                                    }
                                                });
                return;""")
s = rep(s, "    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath)\n    {",
        "    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints)\n    {")
s = rep(s, "        foreach (var sp in _points)\n        {\n            if (sp.Count < SphericalMinObservations)\n                continue;\n\n            var r = sp.Position.Length();",
        "        foreach (var sp in scanPoints)\n        {\n            if (sp.Count < SphericalMinObservations)\n                continue;\n\n            var r = sp.Position.Length();")
s = rep(s, "    private bool _pendingSpherical;", "    private bool _pendingSpherical;\n    private volatile bool _meshBusy;\n    private volatile bool _meshDone;\n    private volatile string _meshResultPath = string.Empty;\n    private volatile string _meshResultMessage = string.Empty;")
s = rep(s, "        HandleSave(context);\n", """        if (_meshDone)
        {
            _meshDone = false;
            SavedPath.Value = _meshResultPath;
            SetStatus(_meshResultMessage, _meshResultPath.Length > 0 ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning);
        }

        HandleSave(context);
""")
save("DepthScan3D.cs", s, crlf)
print("ok")
