#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;

namespace T3.Core.DataTypes.Volume;

/// <summary>
/// Classic marching-cubes surface extraction over a float grid (OpenVDB's volumeToMesh with
/// adaptivity 0). Cells are marched through the active band; vertices on shared edges are
/// deduplicated per cell so the surface is watertight, and normals come from central-difference
/// gradients. Output is a triangulated <see cref="MeshGeometry"/>.
/// </summary>
public static class VolumeToMesh
{
    /// <summary>
    /// Upper bound for an extracted surface. Eight million vertices already need roughly 300 MB
    /// (positions, normals and corner indices); beyond that a runaway volume is reported instead
    /// of exhausting memory.
    /// </summary>
    public const int MaxVertices = 8_000_000;

    public static MeshGeometry Extract(VdbGrid<float> grid, float isovalue = 0f)
    {
        var tree = grid.Tree;
        var positions = new List<Vector3>(4096);
        var cornerIndices = new List<int>(4096);
        var normals = new List<Vector3>(4096);

        // Global edge -> vertex map so neighboring cells share vertices (watertight mesh).
        // Canonical key: the edge's minimum absolute corner + its axis.
        var edgeToVertex = new Dictionary<(VdbCoord Corner, int Axis), int>();

        // Reusable per-cell state.
        var cornerValues = new float[8];
        var edgeVertices = new int[12];

        foreach (var leaf in tree.Leaves)
        {
            // Each cell is owned by exactly one leaf (cells span corner..corner+1, so the cell at
            // a leaf's max border belongs to the neighbor). Every crossing cell is covered once.
            for (var z = 0; z < VdbLeafNode<float>.Dim; z++)
            {
                for (var y = 0; y < VdbLeafNode<float>.Dim; y++)
                {
                    for (var x = 0; x < VdbLeafNode<float>.Dim; x++)
                    {
                        var coord = leaf.Origin + new VdbCoord(x, y, z);

                        // Only cross cells whose corners all sit in the active band: out-of-band
                        // values are background-filled and would produce phantom surfaces.
                        cornerValues[0] = tree.GetValue(coord);
                        cornerValues[1] = tree.GetValue(coord + new VdbCoord(1, 0, 0));
                        cornerValues[2] = tree.GetValue(coord + new VdbCoord(1, 1, 0));
                        cornerValues[3] = tree.GetValue(coord + new VdbCoord(0, 1, 0));
                        cornerValues[4] = tree.GetValue(coord + new VdbCoord(0, 0, 1));
                        cornerValues[5] = tree.GetValue(coord + new VdbCoord(1, 0, 1));
                        cornerValues[6] = tree.GetValue(coord + new VdbCoord(1, 1, 1));
                        cornerValues[7] = tree.GetValue(coord + new VdbCoord(0, 1, 1));

                        var caseIndex = 0;
                        for (var c = 0; c < 8; c++)
                        {
                            if (cornerValues[c] < isovalue)
                                caseIndex |= 1 << c;
                        }

                        if (caseIndex == 0 || caseIndex == 255)
                            continue;

                        // Narrow-band level sets store +background outside and -background
                        // inside, so only the shell around the surface is active. A sign change
                        // between two *inactive* corners is the band edge, not the surface —
                        // emitting those cells creates geometry offset from the real surface.
                        // A crossing with at least one active endpoint is the actual surface.
                        var resolved = true;
                        for (var e = 0; e < 12; e++)
                        {
                            var (ca, cb) = EdgeCorners(e);
                            var insideA = cornerValues[ca] < isovalue;
                            var insideB = cornerValues[cb] < isovalue;
                            if (insideA == insideB)
                                continue;

                            if (tree.IsValueOn(CornerCoord(coord, ca)) || tree.IsValueOn(CornerCoord(coord, cb)))
                                continue;

                            resolved = false;
                            break;
                        }

                        if (!resolved)
                            continue;

                        Array.Clear(edgeVertices);

                        var tris = MarchingCubesTables.TriTable[caseIndex];
                        for (var t = 0; t < tris.Length; t += 3)
                        {
                            if (tris[t] < 0)
                                break;

                            // The table winds triangles clockwise seen from the inside; meshes in TiXL face
                            // outward counter-clockwise, so the last two corners are swapped.
                            for (var v = 0; v < 3; v++)
                            {
                                var edge = tris[t + (v == 0 ? 0 : 3 - v)];
                                var cornerIndex = edgeVertices[edge];
                                if (cornerIndex == 0)
                                {
                                    var (cornerA, cornerB) = EdgeCorners(edge);
                                    var absA = CornerCoord(coord, cornerA);
                                    var absB = CornerCoord(coord, cornerB);
                                    var axis = absA.X != absB.X ? 0 : absA.Y != absB.Y ? 1 : 2;
                                    var edgeKey = (VdbCoord.Min(absA, absB), axis);
                                    if (!edgeToVertex.TryGetValue(edgeKey, out cornerIndex))
                                    {
                                        cornerIndex = InterpolateEdge(grid, coord, cornerValues, edge, isovalue,
                                                                      positions, normals);
                                        edgeToVertex[edgeKey] = cornerIndex;
                                    }

                                    edgeVertices[edge] = cornerIndex + 1;
                                }
                                else
                                {
                                    cornerIndex -= 1;
                                }

                                cornerIndices.Add(cornerIndex);
                            }
                        }
                    }
                }
            }
        }

        var geometry = new MeshGeometry
        {
            Positions = positions.ToArray(),
            FaceCornerOffsets = BuildFaceOffsets(cornerIndices.Count / 3),
            CornerPointIndices = cornerIndices.ToArray(),
        };
        var normalAttribute = geometry.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, geometry.CornerCount);
        for (var corner = 0; corner < cornerIndices.Count; corner++)
            normalAttribute.Values[corner] = normals[cornerIndices[corner]];
        return geometry;
    }

    private static int[] BuildFaceOffsets(int triangleCount)
    {
        var offsets = new int[triangleCount + 1];
        for (var i = 0; i <= triangleCount; i++)
            offsets[i] = i * 3;
        return offsets;
    }

    private static int InterpolateEdge(VdbGrid<float> grid, VdbCoord coord, float[] cornerValues, int edge,
                                       float isovalue, List<Vector3> positions, List<Vector3> normals)
    {
        if (positions.Count >= MaxVertices)
            throw new InvalidOperationException($"Extracted surface exceeds {MaxVertices} vertices; raise the voxel size or Isovalue.");

        var (cornerA, cornerB) = EdgeCorners(edge);
        var va = cornerValues[cornerA];
        var vb = cornerValues[cornerB];
        var t = Math.Abs(vb - va) < 1e-20f ? 0.5f : (isovalue - va) / (vb - va);

        var coordA = CornerCoord(coord, cornerA);
        var coordB = CornerCoord(coord, cornerB);
        var indexPos = Vector3.Lerp(coordA.ToVector3(), coordB.ToVector3(), t);
        // Values live at voxel centers, so the interpolated position is a center-based index position.
        var worldPos = grid.Transform.IndexToWorld(indexPos);
        positions.Add(worldPos);

        // Central-difference gradient in index space; the uniform-scale transform preserves direction.
        var nx = grid.Tree.GetValue(coordA + new VdbCoord(1, 0, 0)) - grid.Tree.GetValue(coordA - new VdbCoord(1, 0, 0))
                 + grid.Tree.GetValue(coordB + new VdbCoord(1, 0, 0)) - grid.Tree.GetValue(coordB - new VdbCoord(1, 0, 0));
        var ny = grid.Tree.GetValue(coordA + new VdbCoord(0, 1, 0)) - grid.Tree.GetValue(coordA - new VdbCoord(0, 1, 0))
                 + grid.Tree.GetValue(coordB + new VdbCoord(0, 1, 0)) - grid.Tree.GetValue(coordB - new VdbCoord(0, 1, 0));
        var nz = grid.Tree.GetValue(coordA + new VdbCoord(0, 0, 1)) - grid.Tree.GetValue(coordA - new VdbCoord(0, 0, 1))
                 + grid.Tree.GetValue(coordB + new VdbCoord(0, 0, 1)) - grid.Tree.GetValue(coordB - new VdbCoord(0, 0, 1));

        var normal = new Vector3(nx, ny, nz);
        var length = normal.Length();
        normals.Add(length > 1e-9f ? normal / length : Vector3.UnitY);
        return positions.Count - 1;
    }

    private static VdbCoord CornerCoord(VdbCoord origin, int corner) => corner switch
    {
        1 => origin + new VdbCoord(1, 0, 0),
        2 => origin + new VdbCoord(1, 1, 0),
        3 => origin + new VdbCoord(0, 1, 0),
        4 => origin + new VdbCoord(0, 0, 1),
        5 => origin + new VdbCoord(1, 0, 1),
        6 => origin + new VdbCoord(1, 1, 1),
        7 => origin + new VdbCoord(0, 1, 1),
        _ => origin,
    };

    private static (int A, int B) EdgeCorners(int edge) => edge switch
    {
        0 => (0, 1),
        1 => (1, 2),
        2 => (2, 3),
        3 => (3, 0),
        4 => (4, 5),
        5 => (5, 6),
        6 => (6, 7),
        7 => (7, 4),
        8 => (0, 4),
        9 => (1, 5),
        10 => (2, 6),
        _ => (3, 7),
    };
}
