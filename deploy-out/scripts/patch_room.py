p = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.cs"
b = open(p, 'rb').read()
crlf = b'\r\n' in b
s = b.decode().replace('\r\n', '\n')

# replace the vertex/face building region inside WriteSphericalMesh
i = s.index("        var vertexIndex = new int[width * height];")
j = s.index("        CloseHoles(positions, colors, faces, AddFace);\n")
new_region = r'''        // The depth of each capture carries its own scale, so surfaces wobble. A room is a box: fit one and snap the walls to it
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

'''
s = s[:i] + new_region + s[j:]

# drop the old positions/colors/faces declarations that preceded (they were inside the replaced region) -- check no duplicates
assert s.count("var positions = new List<System.Numerics.Vector3>();") == 1, s.count("var positions = new List<System.Numerics.Vector3>();")

# constants
s = s.replace("    private const float MeshDepthJumpRatio = 1.25f;", "    private const float MeshDepthJumpRatio = 1.6f;\n    private const float RoomObjectRatio = 0.75f;")

room_code = r'''    /** A room as a box rotated about the vertical axis; the camera sits inside it at the origin. */
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

'''
k = s.index("    /** 3x3 median of the range grid")
s = s[:k] + room_code + s[k:]
open(p, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode())
print("ok")
