base = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything"


def load(n):
    b = open(base + "\\" + n, 'rb').read()
    return b.decode('utf8').replace('\r\n', '\n'), b'\r\n' in b


def save(n, s, crlf):
    open(base + "\\" + n, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf8'))


def rep(s, a, c, count=1):
    assert s.count(a) == count, (s.count(a), a[:90])
    return s.replace(a, c)


s, crlf = load("DepthScan3D.cs")

# ---------------------------------------------------------------- split FlattenPlanes into FindPlanes + flatten
i = s.index("    /**\n     * Flattens the surfaces of a rotation-only scan onto planes found in the scan itself")
j = s.index("    /** A room as a box rotated about the vertical axis")
new_planes = r'''    private static System.Numerics.Vector3 CoarseDirection(int x, int y)
    {
        var elevation = 0.5f * MathF.PI - (y + 0.5f) * 2f * SphericalCellRadians;
        var azimuth = (x + 0.5f) * 2f * SphericalCellRadians - MathF.PI;
        var cosE = MathF.Cos(elevation);
        return new System.Numerics.Vector3(cosE * MathF.Sin(azimuth), MathF.Sin(elevation), -cosE * MathF.Cos(azimuth));
    }

    /**
     * Finds the planes of a rotation-only scan from the scan itself: no box and no right angles, so tilted ceilings, alcoves and
     * desks work as well as walls. Surface normals come from the range grid; planes are found by RANSAC on (position, normal)
     * pairs, largest first. Every cell whose normal agrees with a plane and that lies within a generous band of it (relative
     * depth makes one wall appear at several distances) is assigned to the largest such plane; the result holds the plane index
     * per cell, -1 for clutter, edges and unseen cells. Null when there are too few cells with a surface normal.
     */
    private static int[]? FindPlanes(float[] range, int w, int h, out List<System.Numerics.Vector3> planeNormal, out List<float> planeOffset)
    {
        const float depthJump = 0.2f;
        const float normalCos = 0.8f;          // ~37 degrees
        const float ransacBand = 0.08f;        // fraction of range
        const float assignBand = 0.25f;
        const int candidates = 300;
        const int sampleSize = 5000;
        const int maxPlanes = 16;

        planeNormal = new List<System.Numerics.Vector3>();
        planeOffset = new List<float>();
        var count = w * h;
        var positions = new System.Numerics.Vector3[count];
        var directions = new System.Numerics.Vector3[count];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = CoarseDirection(x, y);
                directions[y * w + x] = d;
                if (!float.IsNaN(range[y * w + x]))
                    positions[y * w + x] = d * range[y * w + x];
            }
        }

        // Normals from central differences, facing the camera; edges (depth jumps) get none
        var normals = new System.Numerics.Vector3[count];
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
                valid.Add(c);
            }
        }

        if (valid.Count < 500)
            return null;

        var random = new Random(11);
        var remaining = new List<int>(valid);
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

        var assignment = new int[count];
        Array.Fill(assignment, -1);
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
                    assignment[c] = p;
                    break;
                }
            }
        }

        return assignment;
    }

    /** Moves every cell that lies on a detected plane onto it, along its viewing ray. */
    private static void FlattenPlanes(float[] range, int w, int h)
    {
        var assignment = FindPlanes(range, w, h, out var planeNormal, out var planeOffset);
        if (assignment == null)
            return;

        var flattened = (float[])range.Clone();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = y * w + x;
                if (assignment[c] < 0)
                    continue;

                var facing = System.Numerics.Vector3.Dot(planeNormal[assignment[c]], CoarseDirection(x, y));
                flattened[c] = -planeOffset[assignment[c]] / facing;
            }
        }

        Array.Copy(flattened, range, w * h);
    }

'''
s = s[:i] + new_planes + s[j:]

# ---------------------------------------------------------------- plane loop inside AlignTiles
old = "        // The aligned map: per cell the robust mean of the rescaled samples\n        var points = new List<ScanPoint>();"
new = '''        // Plane-aware correction: a wall must be one plane, so each capture's scale is pulled toward the value that puts
        // its samples on the planes found in the current map (this also ties captures that never overlap)
        const int planeIterations = 6;
        const float planeStep = 0.6f;
        const int minPlaneSamples = 25;
        var coarseCount = CoarseWidth * CoarseHeight;
        var grid = new float[coarseCount];
        var impliedSum = new double[tiles.Length];
        var impliedWeight = new double[tiles.Length];
        var impliedCount = new int[tiles.Length];
        for (var round = 0; round < planeIterations; round++)
        {
            Array.Fill(grid, float.NaN);
            for (var g = 0; g < groups; g++)
            {
                var a = groupStart[g];
                var b = groupStart[g + 1];
                double sum = 0, sumW = 0;
                for (var k = a; k < b; k++)
                {
                    var o = order[k];
                    var w = weight[o] * robust[o];
                    sum += w * (x[tileOf[o]] + logRange[o]);
                    sumW += w;
                }

                if (sumW > 1e-9)
                    grid[sortedCells[a]] = (float)Math.Exp(sum / sumW);
            }

            var assignment = FindPlanes(grid, CoarseWidth, CoarseHeight, out var planeNormal, out var planeOffset);
            if (assignment == null)
                break;

            Array.Clear(impliedSum);
            Array.Clear(impliedWeight);
            Array.Clear(impliedCount);
            for (var g = 0; g < groups; g++)
            {
                var cell = sortedCells[groupStart[g]];
                var plane = assignment[cell];
                if (plane < 0)
                    continue;

                var facing = System.Numerics.Vector3.Dot(planeNormal[plane], CoarseDirection(cell % CoarseWidth, cell / CoarseWidth));
                if (facing > -0.15f)
                    continue;

                var target = MathF.Log(-planeOffset[plane] / facing);
                for (var k = groupStart[g]; k < groupStart[g + 1]; k++)
                {
                    var o = order[k];
                    var w = weight[o];
                    impliedSum[tileOf[o]] += w * (target - logRange[o]);
                    impliedWeight[tileOf[o]] += w;
                    impliedCount[tileOf[o]]++;
                }
            }

            double meanShift = 0;
            var shifted = 0;
            for (var t = 0; t < tiles.Length; t++)
            {
                if (impliedCount[t] < minPlaneSamples || impliedWeight[t] < 1e-9)
                    continue;

                var implied = (float)(impliedSum[t] / impliedWeight[t]);
                x[t] += planeStep * Math.Clamp(implied - x[t], -0.3f, 0.3f);
                meanShift += x[t];
                shifted++;
            }

            // Only relative scales matter: keep the corrected captures' mean where it was
            if (shifted > 0)
            {
                var mean = (float)(meanShift / shifted);
                for (var t = 0; t < tiles.Length; t++)
                {
                    if (impliedCount[t] >= minPlaneSamples)
                        x[t] -= mean;
                }
            }
        }

        // The aligned map: per cell the robust mean of the rescaled samples
        var points = new List<ScanPoint>();'''
s = rep(s, old, new)
save("DepthScan3D.cs", s, crlf)
print("ok")
