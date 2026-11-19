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
s = rep(s, "    private const int TileStride = 4;", "    private const int TileStride = 3;")

# coarse (0.5 degree) tile cells
old_start = s.index("    private static readonly int GridWidth = ")
old_end = s.index("    /**\n     * Solves one depth scale per capture")
new_helpers = '''    private static readonly int GridWidth = (int)MathF.Ceiling(2f * MathF.PI / SphericalCellRadians);
    private static readonly int GridHeight = (int)MathF.Ceiling(MathF.PI / SphericalCellRadians) + 1;

    /** Alignment works on 0.5 degree cells, the resolution of the shell, so a few samples per capture reach every cell. */
    private static readonly int CoarseWidth = GridWidth / 2;
    private static readonly int CoarseHeight = (GridHeight + 1) / 2;

    private static uint PackColor(float r, float g, float b) =>
        ((uint)Math.Clamp((int)(r * 255f + 0.5f), 0, 255) << 16) | ((uint)Math.Clamp((int)(g * 255f + 0.5f), 0, 255) << 8) | (uint)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);

    /** Index of the 0.5 degree direction cell of a position seen from the origin; -1 when the position is the origin. */
    private static int SphericalCellIndex(System.Numerics.Vector3 position, out float range)
    {
        range = position.Length();
        if (range < 1e-5f)
            return -1;

        var elevation = MathF.Asin(Math.Clamp(position.Y / range, -1f, 1f));
        var azimuth = MathF.Atan2(position.X, -position.Z);
        var cell = 2f * SphericalCellRadians;
        var ix = Math.Clamp((int)MathF.Floor((azimuth + MathF.PI) / cell), 0, CoarseWidth - 1);
        var iy = Math.Clamp((int)MathF.Floor((0.5f * MathF.PI - elevation) / cell), 0, CoarseHeight - 1);
        return iy * CoarseWidth + ix;
    }

'''
s = s[:old_start] + new_helpers + s[old_end:]

# emission: four fine cells per aligned coarse cell, so the shell's fine grid is filled
old = s[s.index("            var cell = sortedCells[a];\n            var cellX = cell % GridWidth;"):s.index("        return points.ToArray();\n    }\n\n    /**\n     * Turns the spherical range map")]
new = '''            var coarse = sortedCells[a];
            var range = (float)Math.Exp(sum / sumW);
            var color = new System.Numerics.Vector3((float)(cr / sumW) / 255f, (float)(cg / sumW) / 255f, (float)(cb / sumW) / 255f);
            for (var sy = 0; sy < 2; sy++)
            {
                for (var sx = 0; sx < 2; sx++)
                {
                    var fineX = (coarse % CoarseWidth) * 2 + sx;
                    var fineY = Math.Min((coarse / CoarseWidth) * 2 + sy, GridHeight - 1);
                    var elevation = 0.5f * MathF.PI - (fineY + 0.5f) * SphericalCellRadians;
                    var azimuth = (fineX + 0.5f) * SphericalCellRadians - MathF.PI;
                    var cosE = MathF.Cos(elevation);
                    points.Add(new ScanPoint
                    {
                        Position = new System.Numerics.Vector3(range * cosE * MathF.Sin(azimuth), range * MathF.Sin(elevation), -range * cosE * MathF.Cos(azimuth)),
                        Color = color,
                        Count = b - a,
                        Weight = (float)sumW,
                    });
                }
            }
        }

'''
s = s.replace(old, new)

# rebuild when the alignment setting flips
s = rep(s, "        if (_shellBusy || _points.Count < 3000 || _captures == _shellCaptures)\n            return;",
        "        var aligned = _pendingGlobalAlign && _tiles.Count >= MinTilesForAlignment;\n        if (_shellBusy || _points.Count < 3000 || (_captures == _shellCaptures && aligned == _shellAligned && regularizeUnchanged()))\n            return;")
s = rep(s, "        _shellBusy = true;\n        _shellCaptures = _captures;", "        _shellBusy = true;\n        _shellCaptures = _captures;\n        _shellAligned = aligned;\n        _shellRegularize = _pendingRegularize;")
s = rep(s, "    private int _shellCaptures;", "    private int _shellCaptures;\n    private bool _shellAligned;\n    private bool _shellRegularize;\n\n    private bool regularizeUnchanged() => _shellRegularize == _pendingRegularize;")
save("DepthScan3D.cs", s, crlf)
print("ok")
