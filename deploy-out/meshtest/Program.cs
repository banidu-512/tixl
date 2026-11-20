using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

public struct ScanPoint { public System.Numerics.Vector3 Position; public System.Numerics.Vector3 Color; public int Count; public float Weight; public float LastSeen; }

public static class MeshCode
{
    const float SphericalCellRadians = 0.25f * MathF.PI / 180f;
    const int SphericalMinObservations = 2;
    const float MeshDepthJumpRatio = 1.6f;
    const float RoomObjectRatio = 0.75f;
    const int MeshRimLoopEdges = 2000;
    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    public static Stopwatch W = Stopwatch.StartNew();
    public static void T(string s) { Console.WriteLine($"{W.Elapsed.TotalSeconds:F2}s {s}"); }
    /**
     * Turns the spherical range map into a closed triangle mesh: sparse cells are dropped, the range grid is median
     * filtered and small holes are filled, neighbouring cells become triangles unless a depth jump separates them
     * (so surfaces never stretch across an edge), and the open rim at the lowest seen elevation is closed with a
     * floor fan so the shell is solid. Faces point toward the camera.
     */
    public static (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints)
    {
        var width = (int)MathF.Ceiling(2f * MathF.PI / SphericalCellRadians);
        var height = (int)MathF.Ceiling(MathF.PI / SphericalCellRadians) + 1;
        var range = new float[width * height];
        var color = new System.Numerics.Vector3[width * height];
        Array.Fill(range, float.NaN);
        foreach (var sp in scanPoints)
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

        // The depth of each capture carries its own scale, so surfaces wobble. A room is a box: fit one and snap the walls to it
        var box = FitRoomBox(range, width, height);

        // Output grid at half the resolution; walls are flat, so the extra detail only matters for objects
        const int downsample = 2;
        var w2 = width / downsample;
        var h2 = height / downsample;
        var vertexIndex = new int[w2 * h2];
        var gridRange = new float[w2 * h2];
        var positions = new List<System.Numerics.Vector3>();
        var colors = new List<System.Numerics.Vector3>();
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
                var boxRange = box.RangeAlong(dx, sinE, dz);

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

                // Walls and anything not clearly in front of them take the box; objects in front keep their own range
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
                positions.Add(new System.Numerics.Vector3(r * dx, r * sinE, r * dz));
                colors.Add(cellColor);
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

        for (var y = 0; y < h2 - 1; y++)
        {
            for (var x = 0; x < w2; x++)
            {
                var x1 = (x + 1) % w2;
                var i00 = vertexIndex[y * w2 + x];
                var i10 = vertexIndex[y * w2 + x1];
                var i01 = vertexIndex[(y + 1) * w2 + x];
                var i11 = vertexIndex[(y + 1) * w2 + x1];
                var r00 = gridRange[y * w2 + x];
                var r10 = gridRange[y * w2 + x1];
                var r01 = gridRange[(y + 1) * w2 + x];
                var r11 = gridRange[(y + 1) * w2 + x1];
                var lo = MathF.Min(MathF.Min(r00, r10), MathF.Min(r01, r11));
                var hi = MathF.Max(MathF.Max(r00, r10), MathF.Max(r01, r11));
                if (hi > lo * MeshDepthJumpRatio)
                    continue;

                AddFace(i00, i10, i01);
                AddFace(i10, i11, i01);
            }
        }

        CloseHoles(positions, colors, faces, AddFace);

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

    /**
     * Fans over every boundary loop of the mesh so the shell is solid: gaps left by depth jumps and missing cells close on
     * their centroid, and the large outer rim (the lowest elevation the camera could see) closes on a point at floor level.
     */
    public static void CloseHoles(List<System.Numerics.Vector3> positions, List<System.Numerics.Vector3> colors, List<int> faces,
                                   Action<int, int, int> addFace)
    {
        // Tuple keys: a packed 64-bit key hashes to a ^ b, which piles neighbouring edges into a few buckets
        var directed = new HashSet<(int, int)>();
        for (var i = 0; i < faces.Count; i += 3)
        {
            for (var k = 0; k < 3; k++)
                directed.Add((faces[i + k], faces[i + (k + 1) % 3]));
        }

        // Boundary edges, reversed so that walking them follows the open side
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (from, to) in directed)
        {
            if (directed.Contains((to, from)))
                continue;

            if (!outgoing.TryGetValue(to, out var list))
                outgoing[to] = list = new List<int>(2);

            list.Add(from);
        }

        var loop = new List<int>();
        foreach (var start in new List<int>(outgoing.Keys))
        {
            while (outgoing[start].Count > 0)
            {
                loop.Clear();
                var current = start;
                var closed = false;
                while (true)
                {
                    loop.Add(current);
                    if (!outgoing.TryGetValue(current, out var options) || options.Count == 0)
                        break;

                    var following = options[^1];
                    options.RemoveAt(options.Count - 1);
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
                var lowest = float.MaxValue;
                foreach (var vertex in loop)
                {
                    center += positions[vertex];
                    color += colors[vertex];
                    lowest = MathF.Min(lowest, positions[vertex].Y);
                }

                center /= loop.Count;
                if (loop.Count > MeshRimLoopEdges)
                {
                    center.X = 0f;
                    center.Z = 0f;
                    center.Y = lowest;
                    color = new System.Numerics.Vector3(0.5f, 0.5f, 0.5f) * loop.Count;
                }

                var centerIndex = positions.Count;
                positions.Add(center);
                colors.Add(color / loop.Count);
                for (var i = 0; i < loop.Count; i++)
                    addFace(loop[i], loop[(i + 1) % loop.Count], centerIndex);
            }
        }
    }

    /** A room as a box rotated about the vertical axis; the camera sits inside it at the origin. */
    private readonly struct RoomBox
    {
        public RoomBox(float yaw, float xp, float xn, float zp, float zn, float yp, float yn)
        {
            Yaw = yaw;
            XP = xp;
            XN = xn;
            ZP = zp;
            ZN = zn;
            YP = yp;
            YN = yn;
            Cos = MathF.Cos(-yaw);
            Sin = MathF.Sin(-yaw);
        }

        public readonly float Yaw, XP, XN, ZP, ZN, YP, YN, Cos, Sin;

        public float RangeAlong(float dx, float dy, float dz)
        {
            var x = Cos * dx + Sin * dz;
            var z = -Sin * dx + Cos * dz;
            var best = float.MaxValue;
            if (x > 1e-6f) best = MathF.Min(best, XP / x); else if (x < -1e-6f) best = MathF.Min(best, XN / -x);
            if (z > 1e-6f) best = MathF.Min(best, ZP / z); else if (z < -1e-6f) best = MathF.Min(best, ZN / -z);
            if (dy > 1e-6f) best = MathF.Min(best, YP / dy); else if (dy < -1e-6f) best = MathF.Min(best, YN / -dy);
            return best;
        }
    }

    /**
     * Robust fit of a room box to the range map: a truncated log-range error, so furniture and the depth wobble between
     * captures cannot pull the walls. Pattern search from several yaw and size starts.
     */
    private static RoomBox FitRoomBox(float[] range, int width, int height)
    {
        const int maxSamples = 6000;
        var valid = 0;
        for (var i = 0; i < range.Length; i++)
        {
            if (!float.IsNaN(range[i]))
                valid++;
        }

        var step = Math.Max(1, valid / maxSamples);
        var dirX = new List<float>();
        var dirY = new List<float>();
        var dirZ = new List<float>();
        var logRange = new List<float>();
        var seen = 0;
        var rangeSum = 0f;
        for (var y = 0; y < height; y++)
        {
            var elevation = 0.5f * MathF.PI - (y + 0.5f) * SphericalCellRadians;
            for (var x = 0; x < width; x++)
            {
                var r = range[y * width + x];
                if (float.IsNaN(r) || seen++ % step != 0)
                    continue;

                var azimuth = (x + 0.5f) * SphericalCellRadians - MathF.PI;
                dirX.Add(MathF.Cos(elevation) * MathF.Sin(azimuth));
                dirY.Add(MathF.Sin(elevation));
                dirZ.Add(-MathF.Cos(elevation) * MathF.Cos(azimuth));
                logRange.Add(MathF.Log(r));
                rangeSum += r;
            }
        }

        var count = logRange.Count;
        if (count == 0)
            return new RoomBox(0f, 2f, 2f, 2f, 2f, 1.3f, 1.3f);

        var mean = rangeSum / count;
        float Cost(float[] prm)
        {
            for (var k = 1; k < 7; k++)
            {
                if (prm[k] < 0.05f)
                    return float.MaxValue;
            }

            var box = new RoomBox(prm[0], prm[1], prm[2], prm[3], prm[4], prm[5], prm[6]);
            var total = 0f;
            for (var i = 0; i < count; i++)
            {
                var e = MathF.Abs(logRange[i] - MathF.Log(box.RangeAlong(dirX[i], dirY[i], dirZ[i])));
                total += MathF.Min(e, 0.3f);
            }

            return total / count;
        }

        var random = new Random(7);
        var bestParams = new float[7];
        var bestCost = float.MaxValue;
        var current = new float[7];
        var trial = new float[7];
        for (var start = 0; start < 18; start++)
        {
            current[0] = (start % 6) * MathF.PI / 12f;
            for (var k = 1; k < 7; k++)
                current[k] = mean * (0.6f + 1.0f * (float)random.NextDouble());

            var cost = Cost(current);
            var stepSize = new float[7] { 0.15f, 0.25f * mean, 0.25f * mean, 0.25f * mean, 0.25f * mean, 0.25f * mean, 0.25f * mean };
            for (var iteration = 0; iteration < 120; iteration++)
            {
                var improved = false;
                for (var k = 0; k < 7; k++)
                {
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        Array.Copy(current, trial, 7);
                        trial[k] += sign * stepSize[k];
                        var c = Cost(trial);
                        if (c < cost)
                        {
                            cost = c;
                            Array.Copy(trial, current, 7);
                            improved = true;
                        }
                    }
                }

                if (!improved)
                {
                    var small = true;
                    for (var k = 0; k < 7; k++)
                    {
                        stepSize[k] *= 0.5f;
                        small &= stepSize[k] < (k == 0 ? 0.002f : 0.002f * mean);
                    }

                    if (small)
                        break;
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                Array.Copy(current, bestParams, 7);
            }
        }

        return new RoomBox(bestParams[0], bestParams[1], bestParams[2], bestParams[3], bestParams[4], bestParams[5], bestParams[6]);
    }

    /** 3x3 median of the range grid (azimuth wraps); cells with fewer than four filled neighbours are removed as specks. */
    public static void MedianFilterRange(float[] range, int width, int height)
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
    public static void FillSmallHoles(float[] range, System.Numerics.Vector3[] color, int width, int height, int passes)
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


}

public static class Program
{
    public static void Main(string[] args)
    {
        var raw = File.ReadAllBytes(args[0]);
        var n = raw.Length / 24;
        var pts = new ScanPoint[n];
        for (int i = 0; i < n; i++)
        {
            var f = new float[6];
            Buffer.BlockCopy(raw, i * 24, f, 0, 24);
            pts[i] = new ScanPoint { Position = new(f[0], f[1], f[2]), Color = new(f[3], f[4], f[5]), Count = 2, Weight = 1 };
        }
        MeshCode.T("loaded " + n);
        var r = MeshCode.WriteSphericalMesh(args[1], pts);
        MeshCode.T($"done {r.Vertices} {r.Triangles}");
    }
}
