using System;
using System.Numerics;
using T3.Core.DataTypes;

namespace Lib.physics;

/// <summary>
/// Turns a <see cref="MeshGeometry"/> into the collision data a <see cref="PhysicsBody"/> carries: convex
/// pieces for hull bodies and a triangle list for mesh colliders. Kept free of operator and GPU types so it
/// can be tested next to <see cref="PhysicsSimulation"/>.
/// </summary>
internal static class PhysicsGeometry
{
    /// <summary>Most points one hull is built from. Bepu's hull builder is far from linear, and a collision hull
    /// gains nothing from thousands of surface points.</summary>
    public const int MaxHullPoints = 2048;

    /// <summary>
    /// Fingerprint of everything the collision data depends on. Geometry instances are reused and rebuilt in
    /// place by their producers, so neither the reference nor its version counter says whether the points moved.
    /// </summary>
    public static int Stamp(MeshGeometry geometry)
    {
        var hash = new HashCode();
        hash.Add(geometry.PointCount);
        hash.Add(geometry.FaceCount);
        hash.Add(geometry.CornerCount);
        hash.Add(geometry.Parts.Length);
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(geometry.Positions.AsSpan()));
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(geometry.CornerPointIndices.AsSpan()));
        foreach (var part in geometry.Parts)
        {
            hash.Add(part.FaceStart);
            hash.Add(part.FaceCount);
            hash.Add(part.Pivot);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// One convex piece per geometry part, with its points relative to the part's pivot. Geometry without parts,
    /// or <paramref name="merge"/>, gives a single piece around the origin that wraps everything.
    /// </summary>
    public static PhysicsHull[] BuildHulls(MeshGeometry geometry, bool merge)
    {
        var parts = geometry.Parts;
        if (merge || parts.Length == 0)
            return [new PhysicsHull { Points = CollectPoints(geometry, 0, geometry.FaceCount, Vector3.Zero, new int[geometry.PointCount], 1) }];

        var hulls = new PhysicsHull[parts.Length];
        var marks = new int[geometry.PointCount];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            hulls[index] = new PhysicsHull
                               {
                                   Points = CollectPoints(geometry, part.FaceStart, part.FaceCount, part.Pivot, marks, index + 1),
                                   Pivot = part.Pivot,
                               };
        }

        return hulls;
    }

    /// <summary>All faces as a triangle list, three points per triangle; faces with more corners are fanned.</summary>
    public static Vector3[] BuildTriangles(MeshGeometry geometry)
    {
        var positions = geometry.Positions;
        var corners = geometry.CornerPointIndices;
        var offsets = geometry.FaceCornerOffsets;
        var triangles = new Vector3[geometry.GetTriangleCount() * 3];
        var write = 0;

        for (var face = 0; face < geometry.FaceCount; face++)
        {
            var start = offsets[face];
            var count = offsets[face + 1] - start;
            if (count < 3 || start < 0 || start + count > corners.Length)
                continue;

            for (var corner = 1; corner + 1 < count; corner++)
            {
                var a = corners[start];
                var b = corners[start + corner];
                var c = corners[start + corner + 1];
                if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length)
                    continue;

                triangles[write++] = positions[a];
                triangles[write++] = positions[b];
                triangles[write++] = positions[c];
            }
        }

        if (write != triangles.Length)
            Array.Resize(ref triangles, write);

        return triangles;
    }

    /// <summary>The distinct points used by a face range, relative to a pivot. <paramref name="marks"/> remembers
    /// which piece last took each point, so one array serves every part without clearing.</summary>
    private static Vector3[] CollectPoints(MeshGeometry geometry, int faceStart, int faceCount, Vector3 pivot, int[] marks, int mark)
    {
        var positions = geometry.Positions;
        var corners = geometry.CornerPointIndices;
        var offsets = geometry.FaceCornerOffsets;
        faceStart = Math.Clamp(faceStart, 0, geometry.FaceCount);
        var faceEnd = Math.Clamp(faceStart + Math.Max(0, faceCount), faceStart, geometry.FaceCount);
        var cornerStart = Math.Clamp(offsets[faceStart], 0, corners.Length);
        var cornerEnd = Math.Clamp(offsets[faceEnd], cornerStart, corners.Length);

        var count = 0;
        for (var corner = cornerStart; corner < cornerEnd; corner++)
        {
            var point = corners[corner];
            if ((uint)point >= (uint)positions.Length || marks[point] == mark)
                continue;

            marks[point] = mark;
            count++;
        }

        // Thin out evenly when there are too many; the stride keeps points from all over the piece.
        var stride = count > MaxHullPoints ? (count + MaxHullPoints - 1) / MaxHullPoints : 1;
        var points = new Vector3[(count + stride - 1) / stride];
        var write = 0;
        var seen = 0;
        for (var corner = cornerStart; corner < cornerEnd && write < points.Length; corner++)
        {
            var point = corners[corner];
            if ((uint)point >= (uint)positions.Length || marks[point] != mark)
                continue;

            // Negating the mark visits each point once in this second pass and leaves it unequal to any later mark.
            marks[point] = -mark;
            if (seen++ % stride == 0)
                points[write++] = positions[point] - pivot;
        }

        if (write != points.Length)
            Array.Resize(ref points, write);

        return points;
    }
}
