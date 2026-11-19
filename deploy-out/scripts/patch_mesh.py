p = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.cs"
b = open(p, 'rb').read()
crlf = b'\r\n' in b
s = b.decode('utf8').replace('\r\n', '\n')


def rep(a, c):
    global s
    assert s.count(a) == 1, (s.count(a), a[:70])
    s = s.replace(a, c)


rep("        Ascii,\n        Binary,\n    }", "        Ascii,\n        Binary,\n        Mesh,\n    }")
rep('{ "PLY ASCII", "PLY binary" }', '{ "PLY ASCII", "PLY binary", "PLY mesh (closed)" }')
rep("var format = (PlyFormat)Math.Clamp(SaveFormatInput.GetValue(context), 0, 1);",
    "var format = (PlyFormat)Math.Clamp(SaveFormatInput.GetValue(context), 0, 2);")
rep("""            WritePly(absolutePath, format == PlyFormat.Ascii);
            SavedPath.Value = relativePath;
            SetStatus($"Saved {_points.Count} points ({_captures} captures) to {relativePath}", IStatusProvider.StatusLevel.Success);""",
    """            if (format == PlyFormat.Mesh)
            {
                if (!_pendingSpherical)
                {
                    SaveStatus(string.Empty, "Mesh export needs a camera that rotates about the origin (a PTZ scan).");
                    return;
                }

                var (vertices, triangles) = WriteSphericalMesh(absolutePath);
                SavedPath.Value = relativePath;
                SetStatus($"Saved a mesh of {vertices} vertices and {triangles} triangles to {relativePath}", IStatusProvider.StatusLevel.Success);
            }
            else
            {
                WritePly(absolutePath, format == PlyFormat.Ascii);
                SavedPath.Value = relativePath;
                SetStatus($"Saved {_points.Count} points ({_captures} captures) to {relativePath}", IStatusProvider.StatusLevel.Success);
            }""")
rep("    private const float FlyingPixelRatio = 0.08f;",
    "    private const float FlyingPixelRatio = 0.08f;\n    private const float MeshDepthJumpRatio = 1.12f;")

mesh = r'''    /**
     * Turns the spherical range map into a closed triangle mesh: sparse cells are dropped, the range grid is median
     * filtered and small holes are filled, neighbouring cells become triangles unless a depth jump separates them
     * (so surfaces never stretch across an edge), and the open rim at the lowest seen elevation is closed with a
     * floor fan so the shell is solid. Faces point toward the camera.
     */
    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath)
    {
        var width = (int)MathF.Ceiling(2f * MathF.PI / SphericalCellRadians);
        var height = (int)MathF.Ceiling(MathF.PI / SphericalCellRadians) + 1;
        var range = new float[width * height];
        var color = new System.Numerics.Vector3[width * height];
        Array.Fill(range, float.NaN);
        foreach (var sp in _points)
        {
            if (sp.Count < SphericalMinObservations)
                continue;

            var r = sp.Position.Length();
            var e = MathF.Asin(Math.Clamp(sp.Position.Y / r, -1f, 1f));
            var a = MathF.Atan2(sp.Position.X, -sp.Position.Z);
            var ix = Math.Clamp((int)MathF.Floor((a + MathF.PI) / SphericalCellRadians), 0, width - 1);
            var iy = Math.Clamp((int)MathF.Floor((0.5f * MathF.PI - e) / SphericalCellRadians), 0, height - 1);
            range[iy * width + ix] = r;
            color[iy * width + ix] = sp.Color;
        }

        MedianFilterRange(range, width, height);
        FillSmallHoles(range, color, width, height, 6);

        var vertexIndex = new int[width * height];
        Array.Fill(vertexIndex, -1);
        var positions = new List<System.Numerics.Vector3>();
        var colors = new List<System.Numerics.Vector3>();
        for (var y = 0; y < height; y++)
        {
            var elevation = 0.5f * MathF.PI - (y + 0.5f) * SphericalCellRadians;
            for (var x = 0; x < width; x++)
            {
                var r = range[y * width + x];
                if (float.IsNaN(r))
                    continue;

                var azimuth = (x + 0.5f) * SphericalCellRadians - MathF.PI;
                var cosE = MathF.Cos(elevation);
                vertexIndex[y * width + x] = positions.Count;
                positions.Add(new System.Numerics.Vector3(r * cosE * MathF.Sin(azimuth), r * MathF.Sin(elevation), -r * cosE * MathF.Cos(azimuth)));
                colors.Add(color[y * width + x]);
            }
        }

        var faces = new List<int>();
        void AddFace(int i0, int i1, int i2)
        {
            var normal = System.Numerics.Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            var centroid = positions[i0] + positions[i1] + positions[i2];
            if (System.Numerics.Vector3.Dot(normal, centroid) > 0f)
                (i1, i2) = (i2, i1);

            faces.Add(i0);
            faces.Add(i1);
            faces.Add(i2);
        }

        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var x1 = (x + 1) % width;
                var i00 = vertexIndex[y * width + x];
                var i10 = vertexIndex[y * width + x1];
                var i01 = vertexIndex[(y + 1) * width + x];
                var i11 = vertexIndex[(y + 1) * width + x1];
                if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                    continue;

                var r00 = range[y * width + x];
                var r10 = range[y * width + x1];
                var r01 = range[(y + 1) * width + x];
                var r11 = range[(y + 1) * width + x1];
                var lo = MathF.Min(MathF.Min(r00, r10), MathF.Min(r01, r11));
                var hi = MathF.Max(MathF.Max(r00, r10), MathF.Max(r01, r11));
                if (hi > lo * MeshDepthJumpRatio)
                    continue;

                AddFace(i00, i10, i01);
                AddFace(i10, i11, i01);
            }
        }

        // Close the rim at the lowest elevation seen in each column with a fan to a point on the floor
        var rim = new int[width];
        var floorY = float.MaxValue;
        for (var x = 0; x < width; x++)
        {
            rim[x] = -1;
            for (var y = height - 1; y >= 0; y--)
            {
                if (vertexIndex[y * width + x] < 0)
                    continue;

                rim[x] = vertexIndex[y * width + x];
                floorY = MathF.Min(floorY, positions[rim[x]].Y);
                break;
            }
        }

        if (floorY < float.MaxValue)
        {
            var center = positions.Count;
            positions.Add(new System.Numerics.Vector3(0f, floorY, 0f));
            colors.Add(new System.Numerics.Vector3(0.5f, 0.5f, 0.5f));
            for (var x = 0; x < width; x++)
            {
                var next = rim[(x + 1) % width];
                if (rim[x] >= 0 && next >= 0)
                    AddFace(rim[x], next, center);
            }
        }

        using (var file = File.Create(absolutePath))
        {
            var header = "ply\nformat binary_little_endian 1.0\ncomment TiXL DepthScan3D mesh\n"
                         + $"element vertex {positions.Count}\nproperty float x\nproperty float y\nproperty float z\n"
                         + "property uchar red\nproperty uchar green\nproperty uchar blue\n"
                         + $"element face {faces.Count / 3}\nproperty list uchar int vertex_indices\nend_header\n";
            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
            file.Write(headerBytes, 0, headerBytes.Length);
            using var writer = new BinaryWriter(file);
            for (var i = 0; i < positions.Count; i++)
            {
                writer.Write(positions[i].X);
                writer.Write(positions[i].Y);
                writer.Write(positions[i].Z);
                writer.Write(ToByte(colors[i].X));
                writer.Write(ToByte(colors[i].Y));
                writer.Write(ToByte(colors[i].Z));
            }

            for (var i = 0; i < faces.Count; i += 3)
            {
                writer.Write((byte)3);
                writer.Write(faces[i]);
                writer.Write(faces[i + 1]);
                writer.Write(faces[i + 2]);
            }
        }

        return (positions.Count, faces.Count / 3);
    }

    /** 3x3 median of the range grid (azimuth wraps); cells with fewer than four filled neighbours are removed as specks. */
    private static void MedianFilterRange(float[] range, int width, int height)
    {
        var result = new float[range.Length];
        Array.Fill(result, float.NaN);
        var window = new float[9];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (float.IsNaN(range[y * width + x]))
                    continue;

                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= height)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var value = range[yy * width + (x + dx + width) % width];
                        if (!float.IsNaN(value))
                            window[n++] = value;
                    }
                }

                if (n < 4)
                    continue;

                Array.Sort(window, 0, n);
                result[y * width + x] = window[n / 2];
            }
        }

        Array.Copy(result, range, range.Length);
    }

    /** Fills empty cells that have at least three filled 8-neighbours with their mean, a few passes deep. */
    private static void FillSmallHoles(float[] range, System.Numerics.Vector3[] color, int width, int height, int passes)
    {
        var add = new List<(int Index, float Range, System.Numerics.Vector3 Color)>();
        for (var pass = 0; pass < passes; pass++)
        {
            add.Clear();
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!float.IsNaN(range[y * width + x]))
                        continue;

                    var n = 0;
                    var sum = 0f;
                    var colorSum = System.Numerics.Vector3.Zero;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var index = (y + dy) * width + (x + dx + width) % width;
                            if (float.IsNaN(range[index]))
                                continue;

                            n++;
                            sum += range[index];
                            colorSum += color[index];
                        }
                    }

                    if (n >= 3)
                        add.Add((y * width + x, sum / n, colorSum / n));
                }
            }

            if (add.Count == 0)
                break;

            foreach (var item in add)
            {
                range[item.Index] = item.Range;
                color[item.Index] = item.Color;
            }
        }
    }

'''
rep("    private void WritePly(string absolutePath, bool ascii)\n", mesh + "    private void WritePly(string absolutePath, bool ascii)\n")
open(p, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))
print("patched")
