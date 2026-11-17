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

    /// <summary>
    /// Writes the bodies of one description as geometry at their simulated poses: the way back from physics into the
    /// geometry operators. A body made from geometry returns that geometry - each part moved with its body, or one
    /// copy per body - and a primitive body returns a box, sphere, capsule or cylinder of its size. One part per body.
    /// Returns the number of bodies written; 0 leaves <paramref name="output"/> untouched.
    /// </summary>
    public static int BuildPosed(PhysicsBody body, MeshGeometry output, ref MeshGeometry primitiveCache, ref (PhysicsShapeType, Vector3, float, float) primitiveKey)
    {
        var poses = body.PublishedPoints;
        var count = body.PublishedCount;
        var start = body.PublishedStart;
        if (poses == null || count <= 0 || start < 0 || start + count > poses.Length)
            return 0;

        var source = body.SourceGeometry;
        var perPart = body.SourcePerPart;
        if (source == null || source.FaceCount == 0)
        {
            var key = (body.Shape, body.Size, body.Radius, body.Length);
            if (primitiveCache == null || key != primitiveKey)
            {
                primitiveCache = BuildPrimitive(body);
                primitiveKey = key;
            }

            source = primitiveCache;
            perPart = false;
        }

        return perPart
                   ? PoseParts(body, source, poses.AsSpan(start, count), output)
                   : PoseCopies(body, source, poses.AsSpan(start, count), output);
    }

    /// <summary>Per-body working data for <see cref="BuildPosed(System.Collections.Generic.IReadOnlyList{PhysicsBody}, MeshGeometry, System.Collections.Generic.List{PosedBody})"/>, kept by the caller between frames.</summary>
    public sealed class PosedBody
    {
        public readonly MeshGeometry Geometry = new();
        public MeshGeometry Primitive;
        public (PhysicsShapeType, Vector3, float, float) PrimitiveKey;
        public int Count;
    }

    /// <summary>
    /// Several descriptions as one geometry, in the order given. One body keeps all its attributes; when several are
    /// joined only what they all have survives: positions, faces, parts, and normals and part colors if every one has them.
    /// Returns the number of bodies written; 0 leaves <paramref name="output"/> untouched.
    /// </summary>
    public static int BuildPosed(System.Collections.Generic.IReadOnlyList<PhysicsBody> bodies, MeshGeometry output, System.Collections.Generic.List<PosedBody> scratch)
    {
        while (scratch.Count < bodies.Count)
            scratch.Add(new PosedBody());

        if (bodies.Count == 1)
            return BuildPosed(bodies[0], output, ref scratch[0].Primitive, ref scratch[0].PrimitiveKey);

        int total = 0, pointCount = 0, cornerCount = 0, faceCount = 0, partCount = 0;
        bool allNormals = true, allColors = true;
        for (var index = 0; index < bodies.Count; index++)
        {
            var entry = scratch[index];
            entry.Count = bodies[index] == null ? 0 : BuildPosed(bodies[index], entry.Geometry, ref entry.Primitive, ref entry.PrimitiveKey);
            if (entry.Count == 0)
                continue;

            total += entry.Count;
            pointCount += entry.Geometry.PointCount;
            cornerCount += entry.Geometry.CornerCount;
            faceCount += entry.Geometry.FaceCount;
            partCount += entry.Geometry.Parts.Length;
            allNormals &= entry.Geometry.Attributes.TryGet<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, out _);
            allColors &= entry.Geometry.Attributes.TryGet<Vector4>(GeometryAttributeNames.Color, AttributeDomain.Part, out _);
        }

        if (total == 0)
            return 0;

        if (output.Positions.Length != pointCount)
            output.Positions = new Vector3[pointCount];
        if (output.CornerPointIndices.Length != cornerCount)
            output.CornerPointIndices = new int[cornerCount];
        if (output.FaceCornerOffsets.Length != faceCount + 1)
            output.FaceCornerOffsets = new int[faceCount + 1];

        var parts = new GeometryPart[partCount];
        output.Attributes.Clear();
        var normals = allNormals ? output.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, cornerCount) : null;
        var colors = allColors ? output.Attributes.GetOrCreate<Vector4>(GeometryAttributeNames.Color, AttributeDomain.Part, partCount) : null;

        int pointBase = 0, cornerBase = 0, faceBase = 0, partBase = 0;
        for (var index = 0; index < bodies.Count; index++)
        {
            if (scratch[index].Count == 0)
                continue;

            var piece = scratch[index].Geometry;
            Array.Copy(piece.Positions, 0, output.Positions, pointBase, piece.PointCount);
            for (var corner = 0; corner < piece.CornerCount; corner++)
                output.CornerPointIndices[cornerBase + corner] = piece.CornerPointIndices[corner] + pointBase;

            for (var face = 0; face < piece.FaceCount; face++)
                output.FaceCornerOffsets[faceBase + face] = piece.FaceCornerOffsets[face] + cornerBase;

            for (var part = 0; part < piece.Parts.Length; part++)
                parts[partBase + part] = piece.Parts[part] with { FaceStart = piece.Parts[part].FaceStart + faceBase, Id = partBase + part };

            if (normals != null && piece.Attributes.TryGet<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, out var pieceNormals))
                Array.Copy(pieceNormals.Values, 0, normals.Values, cornerBase, Math.Min(pieceNormals.Values.Length, piece.CornerCount));

            if (colors != null && piece.Attributes.TryGet<Vector4>(GeometryAttributeNames.Color, AttributeDomain.Part, out var pieceColors))
                Array.Copy(pieceColors.Values, 0, colors.Values, partBase, Math.Min(pieceColors.Values.Length, piece.Parts.Length));

            pointBase += piece.PointCount;
            cornerBase += piece.CornerCount;
            faceBase += piece.FaceCount;
            partBase += piece.Parts.Length;
        }

        output.FaceCornerOffsets[faceCount] = cornerCount;
        output.Parts = parts;
        output.InvalidateTopologyCaches();
        return total;
    }

    /// <summary>Same topology as the source; every part is moved from its place in the source to the pose of its body.</summary>
    private static int PoseParts(PhysicsBody body, MeshGeometry source, ReadOnlySpan<Point> poses, MeshGeometry output)
    {
        var parts = source.Parts;
        var count = Math.Min(poses.Length, parts.Length);
        var sourcePositions = source.Positions;
        var corners = source.CornerPointIndices;
        var offsets = source.FaceCornerOffsets;

        output.FaceCornerOffsets = offsets;
        output.CornerPointIndices = corners;
        if (output.Positions.Length != sourcePositions.Length || ReferenceEquals(output.Positions, sourcePositions))
            output.Positions = new Vector3[sourcePositions.Length];

        var positions = output.Positions;
        Array.Copy(sourcePositions, positions, sourcePositions.Length);
        var posedParts = new GeometryPart[parts.Length];
        Array.Copy(parts, posedParts, parts.Length);

        output.Attributes.Clear();
        GeometryAttribute<Vector3> sourceNormals = null;
        GeometryAttribute<Vector3> normals = null;
        foreach (var attribute in source.Attributes)
        {
            if (attribute is GeometryAttribute<Vector3> vectors && attribute.Domain == AttributeDomain.Corner
                                                                && string.Equals(attribute.Name, GeometryAttributeNames.Normal, StringComparison.OrdinalIgnoreCase))
                sourceNormals = vectors;
        }

        if (sourceNormals != null)
        {
            normals = output.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, sourceNormals.Values.Length);
            Array.Copy(sourceNormals.Values, normals.Values, sourceNormals.Values.Length);
        }

        foreach (var attribute in source.Attributes)
        {
            if (!ReferenceEquals(attribute, sourceNormals))
                output.Attributes.Add(attribute);
        }

        // A point shared by two parts can only follow one of them; the first part to use it wins.
        var moved = new bool[sourcePositions.Length];
        for (var index = 0; index < count; index++)
        {
            ref readonly var pose = ref poses[index];
            var part = parts[index];
            var scale = ScaleOf(body, index);
            var faceStart = Math.Clamp(part.FaceStart, 0, source.FaceCount);
            var faceEnd = Math.Clamp(faceStart + Math.Max(0, part.FaceCount), faceStart, source.FaceCount);
            var cornerStart = Math.Clamp(offsets[faceStart], 0, corners.Length);
            var cornerEnd = Math.Clamp(offsets[faceEnd], cornerStart, corners.Length);

            for (var corner = cornerStart; corner < cornerEnd; corner++)
            {
                var point = corners[corner];
                if ((uint)point < (uint)positions.Length && !moved[point])
                {
                    moved[point] = true;
                    positions[point] = Vector3.Transform((sourcePositions[point] - part.Pivot) * scale, pose.Orientation) + pose.Position;
                }

                if (normals != null && corner < normals.Values.Length)
                    normals.Values[corner] = Vector3.Transform(sourceNormals.Values[corner], pose.Orientation);
            }

            posedParts[index] = part with { Pivot = pose.Position };
        }

        output.Parts = posedParts;
        output.InvalidateTopologyCaches();
        return count;
    }

    /// <summary>One copy of the whole source per body, each copy one part.</summary>
    private static int PoseCopies(PhysicsBody body, MeshGeometry source, ReadOnlySpan<Point> poses, MeshGeometry output)
    {
        var count = poses.Length;
        var sourcePositions = source.Positions;
        var sourceOffsets = source.FaceCornerOffsets;
        var sourceCorners = source.CornerPointIndices;
        var pointCount = sourcePositions.Length;
        var faceCount = source.FaceCount;
        var cornerCount = sourceCorners.Length;

        if (output.Positions.Length != pointCount * count || ReferenceEquals(output.Positions, sourcePositions))
            output.Positions = new Vector3[pointCount * count];
        if (output.FaceCornerOffsets.Length != faceCount * count + 1 || ReferenceEquals(output.FaceCornerOffsets, sourceOffsets))
            output.FaceCornerOffsets = new int[faceCount * count + 1];
        if (output.CornerPointIndices.Length != cornerCount * count || ReferenceEquals(output.CornerPointIndices, sourceCorners))
            output.CornerPointIndices = new int[cornerCount * count];

        var positions = output.Positions;
        var offsets = output.FaceCornerOffsets;
        var corners = output.CornerPointIndices;
        var parts = new GeometryPart[count];

        output.Attributes.Clear();
        source.Attributes.TryGet<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, out var sourceNormals);
        var normals = sourceNormals != null
                          ? output.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, cornerCount * count)
                          : null;
        var colors = output.Attributes.GetOrCreate<Vector4>(GeometryAttributeNames.Color, AttributeDomain.Part, count);

        // The pivot of a single-hull body is where its pose is published; the copy is built around it.
        var hulls = body.Hulls;
        var pivot = hulls != null && hulls.Length > 0 ? hulls[0].Pivot : Vector3.Zero;

        for (var index = 0; index < count; index++)
        {
            ref readonly var pose = ref poses[index];
            var scale = ScaleOf(body, index);
            var pointBase = index * pointCount;
            var faceBase = index * faceCount;
            var cornerBase = index * cornerCount;

            for (var point = 0; point < pointCount; point++)
                positions[pointBase + point] = Vector3.Transform((sourcePositions[point] - pivot) * scale, pose.Orientation) + pose.Position;

            for (var face = 0; face < faceCount; face++)
                offsets[faceBase + face] = cornerBase + sourceOffsets[face];

            for (var corner = 0; corner < cornerCount; corner++)
                corners[cornerBase + corner] = pointBase + sourceCorners[corner];

            if (normals != null)
            {
                var inverse = new Vector3(1f / scale.X, 1f / scale.Y, 1f / scale.Z);
                var limit = Math.Min(cornerCount, sourceNormals.Values.Length);
                for (var corner = 0; corner < limit; corner++)
                {
                    var normal = Vector3.Transform(sourceNormals.Values[corner] * inverse, pose.Orientation);
                    var length = normal.Length();
                    normals.Values[cornerBase + corner] = length > 1e-8f ? normal / length : normal;
                }
            }

            colors.Values[index] = pose.Color;
            parts[index] = new GeometryPart(faceBase, faceCount, pose.Position, index, index);
        }

        offsets[faceCount * count] = cornerCount * count;
        output.Parts = parts;
        output.InvalidateTopologyCaches();
        return count;
    }

    /// <summary>The scale the world gave body <paramref name="index"/>: its instance scale, made positive and non-zero like the collider's.</summary>
    private static Vector3 ScaleOf(PhysicsBody body, int index)
    {
        if (body.Instances == null || index >= body.Instances.Length)
            return Vector3.One;

        var scale = body.Instances[index].Scale;
        scale = new Vector3(Positive(scale.X), Positive(scale.Y), Positive(scale.Z));

        // Primitive colliders cannot be squashed: round shapes take the larger of X and Z, as the world does.
        if (body.SourceGeometry == null && body.Shape != PhysicsShapeType.Box)
        {
            var radial = MathF.Max(scale.X, scale.Z);
            scale = body.Shape == PhysicsShapeType.Sphere ? new Vector3(radial) : new Vector3(radial, scale.Y, radial);
        }

        return scale;
    }

    private static float Positive(float value) => float.IsFinite(value) && MathF.Abs(value) > 0.0001f ? MathF.Abs(value) : 1f;

    /// <summary>Geometry of a primitive collider at its real size, centred on the body, with smooth normals on round surfaces.</summary>
    public static MeshGeometry BuildPrimitive(PhysicsBody body)
    {
        var radius = MathF.Max(0.001f, body.Radius);
        var half = MathF.Max(0.001f, body.Length) * 0.5f;
        const int arc = 6;
        var profile = new System.Collections.Generic.List<(float Radius, float Y, float NormalR, float NormalY)>();

        void Cap(float centerY, bool top)
        {
            for (var step = 0; step <= arc; step++)
            {
                var angle = MathF.PI * 0.5f * step / arc;
                var (sin, cos) = MathF.SinCos(top ? angle : MathF.PI * 0.5f - angle);
                profile.Add(top ? (radius * cos, centerY + radius * sin, cos, sin) : (radius * cos, centerY - radius * sin, cos, -sin));
            }
        }

        switch (body.Shape)
        {
            case PhysicsShapeType.Sphere:
                Cap(0f, false);
                Cap(0f, true);
                break;

            case PhysicsShapeType.Capsule:
                Cap(-half, false);
                Cap(half, true);
                break;

            case PhysicsShapeType.Cylinder:
                profile.Add((0f, -half, 0f, -1f));
                profile.Add((radius, -half, 0f, -1f));
                profile.Add((radius, -half, 1f, 0f));
                profile.Add((radius, half, 1f, 0f));
                profile.Add((radius, half, 0f, 1f));
                profile.Add((0f, half, 0f, 1f));
                break;

            default:
                return BuildBox(body.Size);
        }

        return Lathe(profile, 20);
    }

    private static MeshGeometry BuildBox(Vector3 size)
    {
        var h = new Vector3(MathF.Max(0.001f, size.X), MathF.Max(0.001f, size.Y), MathF.Max(0.001f, size.Z)) * 0.5f;
        var geometry = new MeshGeometry { Positions = new Vector3[8], CornerPointIndices = new int[24], FaceCornerOffsets = new int[7] };
        for (var index = 0; index < 8; index++)
            geometry.Positions[index] = new Vector3((index & 1) == 0 ? -h.X : h.X, (index & 2) == 0 ? -h.Y : h.Y, (index & 4) == 0 ? -h.Z : h.Z);

        // Counter-clockwise seen from outside.
        int[] quads = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5];
        Vector3[] faceNormals = [-Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitX, Vector3.UnitX];
        var normals = geometry.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, 24);
        for (var corner = 0; corner < 24; corner++)
        {
            geometry.CornerPointIndices[corner] = quads[corner];
            normals.Values[corner] = faceNormals[corner / 4];
        }

        for (var face = 0; face <= 6; face++)
            geometry.FaceCornerOffsets[face] = face * 4;

        return geometry;
    }

    /// <summary>Revolves a profile of (radius, height) around Y. Consecutive entries at the same place only change the normal.</summary>
    private static MeshGeometry Lathe(System.Collections.Generic.List<(float Radius, float Y, float NormalR, float NormalY)> profile, int segments)
    {
        var rings = profile.Count;
        var positions = new Vector3[rings * segments];
        for (var ring = 0; ring < rings; ring++)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var (sin, cos) = MathF.SinCos(MathF.Tau * segment / segments);
                positions[ring * segments + segment] = new Vector3(profile[ring].Radius * cos, profile[ring].Y, profile[ring].Radius * sin);
            }
        }

        var cornerList = new System.Collections.Generic.List<int>();
        var normalList = new System.Collections.Generic.List<Vector3>();
        var offsetList = new System.Collections.Generic.List<int> { 0 };
        for (var ring = 0; ring + 1 < rings; ring++)
        {
            var a = profile[ring];
            var b = profile[ring + 1];
            if (MathF.Abs(a.Radius - b.Radius) < 1e-6f && MathF.Abs(a.Y - b.Y) < 1e-6f)
                continue;

            for (var segment = 0; segment < segments; segment++)
            {
                var next = (segment + 1) % segments;
                Span<(int Ring, int Segment)> quad = [(ring, segment), (ring + 1, segment), (ring + 1, next), (ring, next)];
                foreach (var (quadRing, quadSegment) in quad)
                {
                    // A ring of zero radius is a pole: its points coincide, so the quad is a triangle there.
                    var (sin, cos) = MathF.SinCos(MathF.Tau * quadSegment / segments);
                    cornerList.Add(quadRing * segments + quadSegment);
                    normalList.Add(new Vector3(profile[quadRing].NormalR * cos, profile[quadRing].NormalY, profile[quadRing].NormalR * sin));
                }

                offsetList.Add(cornerList.Count);
            }
        }

        var geometry = new MeshGeometry { Positions = positions, CornerPointIndices = cornerList.ToArray(), FaceCornerOffsets = offsetList.ToArray() };
        var normals = geometry.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, normalList.Count);
        normalList.CopyTo(normals.Values);
        return geometry;
    }
}
