p = r"D:\CrossTixlClaude\Operators\DepthAnything\Symbols\Lib\io\video\depthanything\DepthScan3D.cs"
b = open(p, 'rb').read()
crlf = b'\r\n' in b
s = b.decode().replace('\r\n', '\n')

# 1) swap the closing function
i = s.index("    /** Fans over every boundary loop of at most MeshMaxHoleEdges")
j = s.index("    /** 3x3 median of the range grid")
new_fn = r'''    /**
     * Fans over every boundary loop of the mesh so the shell is solid: gaps left by depth jumps and missing cells close on
     * their centroid, and the large outer rim (the lowest elevation the camera could see) closes on a point at floor level.
     */
    private static void CloseHoles(List<System.Numerics.Vector3> positions, List<System.Numerics.Vector3> colors, List<int> faces,
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

'''
s = s[:i] + new_fn + s[j:]

# 2) drop the column rim and call CloseHoles
a = s.index("        CloseSmallHoles(positions, colors, faces, AddFace);\n")
bnd = s.index("        using (var file = File.Create(absolutePath))")
s = s[:a] + "        CloseHoles(positions, colors, faces, AddFace);\n\n" + s[bnd:]
s = s.replace("    private const int MeshMaxHoleEdges = 400;", "    private const int MeshRimLoopEdges = 2000;")
open(p, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode())
print("ok")
