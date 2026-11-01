#nullable enable
using System;
using System.Numerics;

namespace T3.Core.DataTypes.Volume.Tools;

/// <summary>
/// Trilinear sampler over a float grid in world space - the bridge that lets TiXL's existing
/// <c>ScalarField</c>/<c>VectorField</c> consumers (geometry displacement, scatter density,
/// custom fields) read volumes without knowing about the tree.
/// </summary>
public sealed class VdbSampler
{
    private readonly VdbGrid<float> _grid;

    public VdbSampler(VdbGrid<float> grid) => _grid = grid;

    public float SampleWorld(Vector3 world)
    {
        var index = _grid.Transform.WorldToIndexf(world);
        return SampleIndex(index);
    }

    public float SampleIndex(Vector3 index)
    {
        var x0 = (int)MathF.Floor(index.X);
        var y0 = (int)MathF.Floor(index.Y);
        var z0 = (int)MathF.Floor(index.Z);
        var tx = index.X - x0;
        var ty = index.Y - y0;
        var tz = index.Z - z0;

        var c000 = _grid.Tree.GetValue(new VdbCoord(x0, y0, z0));
        var c100 = _grid.Tree.GetValue(new VdbCoord(x0 + 1, y0, z0));
        var c010 = _grid.Tree.GetValue(new VdbCoord(x0, y0 + 1, z0));
        var c110 = _grid.Tree.GetValue(new VdbCoord(x0 + 1, y0 + 1, z0));
        var c001 = _grid.Tree.GetValue(new VdbCoord(x0, y0, z0 + 1));
        var c101 = _grid.Tree.GetValue(new VdbCoord(x0 + 1, y0, z0 + 1));
        var c011 = _grid.Tree.GetValue(new VdbCoord(x0, y0 + 1, z0 + 1));
        var c111 = _grid.Tree.GetValue(new VdbCoord(x0 + 1, y0 + 1, z0 + 1));

        var c00 = c000 + (c100 - c000) * tx;
        var c10 = c010 + (c110 - c010) * tx;
        var c01 = c001 + (c101 - c001) * tx;
        var c11 = c011 + (c111 - c011) * tx;

        var c0 = c00 + (c10 - c00) * ty;
        var c1 = c01 + (c11 - c01) * ty;

        return c0 + (c1 - c0) * tz;
    }
}

/// <summary>
/// Dense extraction and resampling. <see cref="ExtractDense"/> fills an RGBA-normalized
/// [x + y*sizeX + z*sizeX*sizeY] float buffer for Texture3D upload; <see cref="Resample"/>
/// changes voxel size via trilinear sampling.
/// </summary>
public static class VolumeDense
{
    /// <summary>
    /// Samples the grid's active index bbox into a dense float buffer (linear, clamped outside).
    /// Returns null when the grid has no active voxels.
    /// </summary>
    public static DenseVolume ExtractDense(VdbGrid<float> grid, int maxSize = 512)
    {
        var bbox = grid.ActiveIndexBBox;
        if (bbox.IsEmpty)
            return null!;

        var sizeX = Math.Clamp(bbox.Max.X - bbox.Min.X + 2, 1, maxSize);
        var sizeY = Math.Clamp(bbox.Max.Y - bbox.Min.Y + 2, 1, maxSize);
        var sizeZ = Math.Clamp(bbox.Max.Z - bbox.Min.Z + 2, 1, maxSize);

        var data = new float[sizeX * sizeY * sizeZ];
        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    data[x + y * sizeX + z * sizeX * sizeY] = grid.Tree.GetValue(new VdbCoord(bbox.Min.X + x, bbox.Min.Y + y, bbox.Min.Z + z));
                }
            }
        }

        return new DenseVolume(data, sizeX, sizeY, sizeZ, bbox.Min, grid.Transform.VoxelSize, grid.Background);
    }

    /// <summary>Changes voxel size by trilinear resampling of the source grid.</summary>
    public static VdbGrid<float> Resample(VdbGrid<float> source, float newVoxelSize)
    {
        newVoxelSize = Math.Clamp(newVoxelSize, 1e-5f, 1e4f);
        if (MathF.Abs(newVoxelSize - source.Transform.VoxelSize) < 1e-7f)
            return (VdbGrid<float>)source.CloneGrid();

        var worldBBox = source.ActiveWorldBBox();
        if (worldBBox.IsEmpty)
            return new VdbGrid<float>(source.Background, newVoxelSize, source.Name, source.GridClass);

        var sampler = new VdbSampler(source);
        var target = new VdbGrid<float>(source.Background, newVoxelSize, source.Name, source.GridClass)
        {
            Transform = source.Transform.WithVoxelSize(newVoxelSize),
        };

        var bandWorld = MathF.Abs(source.Background) * 3f;
        var bandVoxels = (int)MathF.Ceiling(bandWorld / newVoxelSize) + 1;
        var min = source.Transform.WorldToIndex(worldBBox.Min - new Vector3(bandWorld));
        var max = source.Transform.WorldToIndex(worldBBox.Max + new Vector3(bandWorld));

        var leafMin = new VdbCoord(min.X & ~7, min.Y & ~7, min.Z & ~7);
        var leafMax = new VdbCoord(max.X & ~7, max.Y & ~7, max.Z & ~7);

        for (var lz = leafMin.Z; lz <= leafMax.Z; lz += 8)
        {
            for (var ly = leafMin.Y; ly <= leafMax.Y; ly += 8)
            {
                for (var lx = leafMin.X; lx <= leafMax.X; lx += 8)
                {
                    var leafOrigin = new VdbCoord(lx, ly, lz);
                    var values = new float[VdbLeafNode<float>.Size];
                    var active = new bool[VdbLeafNode<float>.Size];
                    var anyActive = false;

                    for (var i = 0; i < VdbLeafNode<float>.Size; i++)
                    {
                        var coord = leafOrigin + VdbCoord.LocalFromOffset(i);
                        var world = target.Transform.IndexToWorld(coord);

                        // Only keep samples near the source's band; outside stays background.
                        var approx = source.Tree.GetValue(source.Transform.WorldToIndex(world));
                        var dist = MathF.Abs(approx);
                        if (dist > bandWorld)
                        {
                            // Beyond the band only the sign survives, as +-background, so a solid's interior stays inside.
                            values[i] = approx < 0 ? -source.Background : source.Background;
                            continue;
                        }

                        values[i] = sampler.SampleWorld(world);
                        active[i] = true;
                        anyActive = true;
                    }

                    if (!anyActive)
                        continue;

                    var leaf = target.Tree.AddLeaf(leafOrigin);
                    Array.Copy(values, leaf.Values, values.Length);
                    for (var i = 0; i < active.Length; i++)
                    {
                        if (active[i])
                            leaf.ValueMask.SetOn(i);
                    }
                }
            }
        }

        target.Tree.Prune();
        return target;
    }
}

/// <summary>A dense (x-fastest) float block extracted from a grid, with its index-space origin.</summary>
public sealed class DenseVolume
{
    public DenseVolume(float[] data, int sizeX, int sizeY, int sizeZ, VdbCoord origin, float voxelSize, float background)
    {
        Data = data;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Origin = origin;
        VoxelSize = voxelSize;
        Background = background;
    }

    public float[] Data { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public VdbCoord Origin { get; }
    public float VoxelSize { get; }
    public float Background { get; }

    public float GetValue(int x, int y, int z) => Data[x + y * SizeX + z * SizeX * SizeY];

    public long VoxelCount => (long)SizeX * SizeY * SizeZ;
}

/// <summary>
/// Integrity diagnostics for a volume (OpenVDB tools/Diagnostics): NaN/Inf counting, active
/// voxel stats and memory usage - what [VolumeInfo] reports into the graph.
/// </summary>
public static class VolumeDiagnose
{
    public sealed class Report
    {
        public long ActiveVoxels;
        public long AllocatedVoxels;
        public int LeafCount;
        public int NanCount;
        public int InfCount;
        public float MinValue = float.MaxValue;
        public float MaxValue = float.MinValue;
        public double Average;
        public long MemoryBytes;
        public VdbCoordBBox ActiveBBox = VdbCoordBBox.Empty;
        public BBox WorldBBox = new(Vector3.Zero, Vector3.Zero);
        public bool HasErrors => NanCount > 0 || InfCount > 0;
    }

    public static Report Check(VdbGrid<float> grid)
    {
        var report = new Report
        {
            ActiveVoxels = grid.ActiveVoxelCount,
            LeafCount = grid.Tree.LeafCount,
            MemoryBytes = grid.AllocatedMemoryBytes,
            ActiveBBox = grid.ActiveIndexBBox,
            WorldBBox = grid.ActiveWorldBBox(),
        };

        var sum = 0.0;
        var count = 0L;
        foreach (var leaf in grid.Tree.Leaves)
        {
            report.AllocatedVoxels += VdbLeafNode<float>.Size;
            foreach (var i in leaf.ValueMask.OnBits())
            {
                var v = leaf.Values[i];
                if (float.IsNaN(v))
                {
                    report.NanCount++;
                    continue;
                }

                if (float.IsInfinity(v))
                {
                    report.InfCount++;
                    continue;
                }

                if (v < report.MinValue)
                    report.MinValue = v;
                if (v > report.MaxValue)
                    report.MaxValue = v;
                sum += v;
                count++;
            }
        }

        report.Average = count > 0 ? sum / count : 0;
        if (count == 0)
        {
            report.MinValue = 0;
            report.MaxValue = 0;
        }

        return report;
    }
}
