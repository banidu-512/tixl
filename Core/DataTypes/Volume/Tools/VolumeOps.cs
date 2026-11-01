#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace T3.Core.DataTypes.Volume.Tools;

/// <summary>
/// Splatting point clouds into density volumes (OpenVDB's ParticlesToLevelSet/PointAdvect
/// territory): each point contributes a normalized falloff kernel (gaussian or smoothstep) over
/// its radius. Points are grouped per leaf so parallel workers never share mask words.
/// </summary>
public static class PointsToVolume
{
    public enum Falloff
    {
        Gaussian,
        Smoothstep,
        Constant,
    }

    public static VdbGrid<float> Splat(Vector3[] positions, float[]? weights,
                                       float voxelSize, float radius, float density,
                                       Falloff falloff, int seed)
    {
        radius = MathF.Max(radius, 1e-5f);
        var radiusVoxels = (int)MathF.Ceiling(radius / voxelSize);
        var transform = new VdbTransform(voxelSize, Vector3.Zero);

        var grid = new VdbGrid<float>(0f, voxelSize, "density", VdbGridClass.FogVolume);

        // Group point indices by every leaf their splat touches (deterministic + race-free parallelism).
        var leafGroups = new Dictionary<VdbCoord, List<int>>();
        for (var i = 0; i < positions.Length; i++)
        {
            var center = transform.WorldToIndexf(positions[i]);
            var minCoord = new VdbCoord((int)MathF.Floor(center.X - radiusVoxels), (int)MathF.Floor(center.Y - radiusVoxels), (int)MathF.Floor(center.Z - radiusVoxels));
            var maxCoord = new VdbCoord((int)MathF.Floor(center.X + radiusVoxels), (int)MathF.Floor(center.Y + radiusVoxels), (int)MathF.Floor(center.Z + radiusVoxels));
            var leafMin = new VdbCoord(minCoord.X & ~7, minCoord.Y & ~7, minCoord.Z & ~7);
            var leafMax = new VdbCoord(maxCoord.X & ~7, maxCoord.Y & ~7, maxCoord.Z & ~7);
            for (var lz = leafMin.Z; lz <= leafMax.Z; lz += 8)
            {
                for (var ly = leafMin.Y; ly <= leafMax.Y; ly += 8)
                {
                    for (var lx = leafMin.X; lx <= leafMax.X; lx += 8)
                    {
                        var leafOrigin = new VdbCoord(lx, ly, lz);
                        if (!leafGroups.TryGetValue(leafOrigin, out var list))
                            leafGroups[leafOrigin] = list = new List<int>();
                        list.Add(i);
                    }
                }
            }
        }

        // Allocate leaves serially (tree is not thread-safe), then splat in parallel.
        var leafList = new List<VdbCoord>(leafGroups.Keys);
        var leafNodes = new List<VdbLeafNode<float>>(leafList.Count);
        foreach (var origin in leafList)
            leafNodes.Add(grid.Tree.AddLeaf(origin));

        Parallel.For(0, leafList.Count, leafIndex =>
        {
            var group = new KeyValuePair<VdbCoord, List<int>>(leafList[leafIndex], leafGroups[leafList[leafIndex]]);
            var values = new float[VdbLeafNode<float>.Size];
            var active = new bool[VdbLeafNode<float>.Size];
            var any = false;

            foreach (var pointIndex in group.Value)
            {
                var center = transform.WorldToIndexf(positions[pointIndex]);
                var weight = weights != null ? weights[pointIndex] : 1f;

                for (var dz = -radiusVoxels; dz <= radiusVoxels; dz++)
                {
                    for (var dy = -radiusVoxels; dy <= radiusVoxels; dy++)
                    {
                        for (var dx = -radiusVoxels; dx <= radiusVoxels; dx++)
                        {
                            var local = center + new Vector3(dx, dy, dz);
                            var coord = new VdbCoord((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y), (int)MathF.Floor(local.Z));
                            var offsetIndex = coord - group.Key;
                            if (offsetIndex.X < 0 || offsetIndex.X > 7 || offsetIndex.Y < 0 || offsetIndex.Y > 7 || offsetIndex.Z < 0 || offsetIndex.Z > 7)
                                continue;

                            var world = transform.IndexToWorld(coord);
                            var dist = Vector3.Distance(world, positions[pointIndex]);
                            if (dist > radius)
                                continue;

                            var t = 1f - dist / radius;
                            var contribution = falloff switch
                            {
                                Falloff.Gaussian => MathF.Exp(-6f * dist * dist / (radius * radius)),
                                Falloff.Smoothstep => t * t * (3f - 2f * t),
                                Falloff.Constant => 1f,
                                _ => t,
                            };

                            var offset = coord.LeafOffset();
                            values[offset] += contribution * weight * density;
                            active[offset] = true;
                            any = true;
                        }
                    }
                }
            }

            if (!any)
                return;

            var leaf = leafNodes[leafIndex];
            for (var i = 0; i < VdbLeafNode<float>.Size; i++)
            {
                leaf.Values[i] = values[i];
                if (active[i])
                    leaf.ValueMask.SetOn(i);
            }
        });

        grid.Tree.Prune();
        return grid;
    }
}

/// <summary>
/// Analytic level-set primitives (sphere, box, torus, ...) rasterized straight into the tree,
/// the counterparts of OpenVDB's tools/LevelSetSphere etc.
/// </summary>
public static class VolumePrimitives
{
    public enum Shape
    {
        Sphere,
        Box,
        RoundedBox,
        Torus,
        Capsule,
        Cylinder,
        Cone,
        Octahedron,
        Plane,
    }

    public delegate float SdfFn(Vector3 localPoint);

    public static VdbGrid<float> Create(Shape shape, Vector3 size,
                                        float voxelSize, Vector3 translation, int halfWidthVoxels = 3)
    {
        var sdf = LocalSdf(shape, size);
        var extent = BoundingBoxOf(shape, size);
        // LocalSdf is centred on the origin, so the translation has to move the *sample point*:
        // evaluating the unshifted SDF and only shifting the index transform leaves the geometry
        // at the origin while the voxel grid moves away from it (Center would silently do nothing).
        return CreateSdf(p => sdf(p - translation), extent.Min + translation, extent.Max + translation,
                         voxelSize, translation, halfWidthVoxels);
    }

    /// <summary>World-space bounds of a primitive (pre-translation), for band rasterization.</summary>
    public static (Vector3 Min, Vector3 Max) BoundingBoxOf(Shape shape, Vector3 size)
    {
        var half = size * 0.5f;
        return shape switch
        {
            Shape.Sphere => new(new Vector3(-half.X), new Vector3(half.X)),
            Shape.Box => new(-half, half),
            Shape.RoundedBox => new(-half - new Vector3(0.1f * MathF.Min(size.X, MathF.Min(size.Y, size.Z))),
                                    half + new Vector3(0.1f * MathF.Min(size.X, MathF.Min(size.Y, size.Z)))),
            Shape.Torus => new(new Vector3(-(half.X + half.Y), -half.Y, -(half.X + half.Y)),
                               new Vector3(half.X + half.Y, half.Y, half.X + half.Y)),
            Shape.Capsule => new(new Vector3(-half.X, -half.Y, -half.X), new Vector3(half.X, half.Y, half.X)),
            Shape.Cylinder => new(new Vector3(-half.X, -half.Y, -half.X), new Vector3(half.X, half.Y, half.X)),
            Shape.Cone => new(new Vector3(-half.X, -half.Y, -half.X), new Vector3(half.X, half.Y, half.X)),
            Shape.Octahedron => new(-half, half),
            Shape.Plane => new(new Vector3(-half.X, -half.Y, -half.X), new Vector3(half.X, half.Y, half.X)),
            _ => new(-half, half),
        };
    }

    /// <summary>
    /// Rasterizes a world-space SDF into a narrow-band level set over the given world bounds.
    /// Voxels within the band around the zero crossing get exact values; the rest stays inactive
    /// background (the same topology OpenVDB's LevelSetSphere & co produce).
    /// </summary>
    public static VdbGrid<float> CreateSdf(SdfFn worldSdf, Vector3 worldMin, Vector3 worldMax,
                                           float voxelSize, Vector3 translation, int halfWidthVoxels = 3)
    {
        var t = new VdbTransform(voxelSize, translation);
        var band = Math.Clamp(halfWidthVoxels, 1, 64);
        var bandWorld = band * t.VoxelSize;
        var background = bandWorld;
        var grid = new VdbGrid<float>(background, t.VoxelSize, "LS", VdbGridClass.LevelSet)
        {
            Transform = t,
        };

        var indexMin = t.WorldToIndex(worldMin - new Vector3(bandWorld));
        var indexMax = t.WorldToIndex(worldMax + new Vector3(bandWorld));
        indexMin -= new VdbCoord(1);
        indexMax += new VdbCoord(1);

        var leafMin = new VdbCoord(indexMin.X & ~7, indexMin.Y & ~7, indexMin.Z & ~7);
        var leafMax = new VdbCoord(indexMax.X & ~7, indexMax.Y & ~7, indexMax.Z & ~7);

        var leafCountX = (leafMax.X - leafMin.X >> 3) + 1;
        var leafCountY = (leafMax.Y - leafMin.Y >> 3) + 1;
        var leafCountZ = (leafMax.Z - leafMin.Z >> 3) + 1;
        var leafCount = (long)leafCountX * leafCountY * leafCountZ;
        if (leafCount > 2_000_000)
            throw new ArgumentException($"Primitive too large for voxel size {voxelSize} ({leafCount} leaves)");

        for (var lz = leafMin.Z >> 3; lz <= leafMax.Z >> 3; lz++)
        {
            for (var ly = leafMin.Y >> 3; ly <= leafMax.Y >> 3; ly++)
            {
                for (var lx = leafMin.X >> 3; lx <= leafMax.X >> 3; lx++)
                {
                    RasterizeLeaf(grid, worldSdf, new VdbCoord(lx << 3, ly << 3, lz << 3), t, bandWorld, background);
                }
            }
        }

        grid.Tree.Prune();
        return grid;
    }

    private static void RasterizeLeaf(VdbGrid<float> grid, SdfFn worldSdf, VdbCoord leafOrigin,
                                      VdbTransform t, float bandWorld, float background)
    {
        // Voxels outside the band keep the background value, so inactive reads stay consistent.
        var values = new float[VdbLeafNode<float>.Size];
        Array.Fill(values, background);
        var active = new bool[VdbLeafNode<float>.Size];
        var anyActive = false;

        for (var i = 0; i < VdbLeafNode<float>.Size; i++)
        {
            var local = VdbCoord.LocalFromOffset(i);
            var world = t.IndexToWorld(leafOrigin + local);
            var d = worldSdf(world);
            if (d > bandWorld)
                continue;

            values[i] = d;
            active[i] = true;
            anyActive = true;
        }

        if (!anyActive)
            return;

        var leaf = grid.Tree.AddLeaf(leafOrigin);
        Array.Copy(values, leaf.Values, values.Length);
        for (var i = 0; i < active.Length; i++)
        {
            if (active[i])
                leaf.ValueMask.SetOn(i);
        }
    }

    /// <summary>Local-space (pre-translation) SDF for each primitive shape.</summary>
    public static SdfFn LocalSdf(Shape shape, Vector3 size) => shape switch
    {
        Shape.Sphere => p => p.Length() - size.X * 0.5f,
        Shape.Box => p => SdBox(p, size * 0.5f),
        Shape.RoundedBox => p => SdBox(p, size * 0.5f) - MathF.Min(size.X, MathF.Min(size.Y, size.Z)) * 0.1f,
        Shape.Torus => p =>
        {
            var q = new Vector2(new Vector2(p.X, p.Y).Length() - size.X * 0.5f, p.Z);
            return q.Length() - size.Y * 0.5f;
        },
        Shape.Capsule => p =>
        {
            var half = MathF.Max(size.Y * 0.5f - size.X * 0.5f, 0f);
            p.Y -= Math.Clamp(p.Y, -half, half);
            return p.Length() - size.X * 0.5f;
        },
        Shape.Cylinder => p =>
        {
            var d = new Vector2(new Vector2(p.X, p.Z).Length() - size.X * 0.5f, MathF.Abs(p.Y) - size.Y * 0.5f);
            return MathF.Min(MathF.Max(d.X, d.Y), 0f) + new Vector2(MathF.Max(d.X, 0f), MathF.Max(d.Y, 0f)).Length();
        },
        Shape.Cone => p =>
        {
            // Capped cone centered on Y (Inigo Quilez): base radius at y=-h/2, apex at y=+h/2.
            var h = MathF.Max(size.Y * 0.5f, 1e-4f);
            var r1 = MathF.Max(size.X * 0.5f, 1e-4f);  // bottom radius
            var r2 = 0f;                                // top radius (apex)
            var q = new Vector2(new Vector2(p.X, p.Z).Length(), p.Y);
            var k1 = new Vector2(r2, h);
            var k2 = new Vector2(r2 - r1, 2f * h);
            var ca = new Vector2(q.X - MathF.Min(q.X, q.Y < 0f ? r1 : r2), MathF.Abs(q.Y) - h);
            var cb = q - k1 + k2 * Math.Clamp(Vector2.Dot(k1 - q, k2) / Vector2.Dot(k2, k2), 0f, 1f);
            var sign = cb.X < 0f && ca.Y < 0f ? -1f : 1f;
            return sign * MathF.Sqrt(MathF.Min(Vector2.Dot(ca, ca), Vector2.Dot(cb, cb)));
        },
        Shape.Octahedron => p =>
        {
            var s = size.X * 0.5f;
            var ap = Vector3.Abs(p);
            return (ap.X + ap.Y + ap.Z - s) * 0.57735027f;
        },
        Shape.Plane => p => p.Y + size.Y * 0.5f,
        _ => p => p.Length() - size.X * 0.5f,
    };

    private static float SdBox(Vector3 p, Vector3 halfSize)
    {
        var q = Vector3.Abs(p) - halfSize;
        var outside = Vector3.Max(q, Vector3.Zero).Length();
        return outside + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
    }
}

/// <summary>
/// Scalar grid arithmetic (OpenVDB tools::csg/comp decorators): two-grid operations, constant
/// operations, and unary function application, all producing fresh grids.
/// </summary>
public static class VolumeMath
{
    public enum BinaryOp
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Min,
        Max,
    }

    public static VdbGrid<float> Combine(VdbGrid<float> a, VdbGrid<float> b, BinaryOp op)
    {
        var result = new VdbGrid<float>(Apply(op, a.Background, b.Background), a.Transform.VoxelSize, a.Name, a.GridClass)
        {
            Transform = a.Transform,
        };

        var sameSpace = a.Transform.VoxelSize == b.Transform.VoxelSize
                        && a.Transform.Translation == b.Transform.Translation;

        var leafCoords = new SortedSet<VdbCoord>();
        VdbSampler? samplerA = null;
        VdbSampler? samplerB = null;
        foreach (var leaf in a.Tree.Leaves)
            leafCoords.Add(leaf.Origin);
        if (sameSpace)
        {
            foreach (var leaf in b.Tree.Leaves)
                leafCoords.Add(leaf.Origin);
        }
        else
        {
            VolumeCsg.CollectLeafCoordsMapped(b, a.Transform, leafCoords);
            samplerA = new VdbSampler(a);
            samplerB = new VdbSampler(b);
        }

        foreach (var coord in leafCoords)
        {
            var values = new float[VdbLeafNode<float>.Size];
            var active = new bool[VdbLeafNode<float>.Size];
            var anyActive = false;
            for (var i = 0; i < VdbLeafNode<float>.Size; i++)
            {
                var voxelCoord = coord + VdbCoord.LocalFromOffset(i);
                float va, vb;
                bool onA, onB;
                if (sameSpace)
                {
                    va = a.Tree.GetValue(voxelCoord);
                    vb = b.Tree.GetValue(voxelCoord);
                    onA = a.Tree.IsValueOn(voxelCoord);
                    onB = b.Tree.IsValueOn(voxelCoord);
                }
                else
                {
                    var world = a.Transform.IndexToWorld(voxelCoord);
                    va = VolumeCsg.SampleWorld(a, samplerA!, world, out onA);
                    vb = VolumeCsg.SampleWorld(b, samplerB!, world, out onB);
                }

                values[i] = Apply(op, va, vb);
                active[i] = onA || onB;
                anyActive |= active[i];
            }

            if (!anyActive)
                continue;

            var leaf = result.Tree.AddLeaf(coord);
            Array.Copy(values, leaf.Values, values.Length);
            for (var i = 0; i < active.Length; i++)
            {
                if (active[i])
                    leaf.ValueMask.SetOn(i);
            }
        }

        return result;
    }

    public static VdbGrid<float> Constant(VdbGrid<float> a, float constant, BinaryOp op)
    {
        var result = (VdbGrid<float>)a.CloneGrid();
        foreach (var leaf in result.Tree.Leaves)
        {
            for (var i = 0; i < VdbLeafNode<float>.Size; i++)
                leaf.Values[i] = Apply(op, leaf.Values[i], constant);
        }

        return result;
    }

    public static VdbGrid<float> ApplyFunction(VdbGrid<float> a, Func<float, float> function, float background = float.NaN)
    {
        var result = (VdbGrid<float>)a.CloneGrid();
        var bg = float.IsNaN(background) ? function(a.Background) : background;
        result.Tree.Root.Background = bg;
        foreach (var leaf in result.Tree.Leaves)
        {
            for (var i = 0; i < VdbLeafNode<float>.Size; i++)
                leaf.Values[i] = function(leaf.Values[i]);
        }

        return result;
    }

    private static float Apply(BinaryOp op, float a, float b) => op switch
    {
        BinaryOp.Add => a + b,
        BinaryOp.Subtract => a - b,
        BinaryOp.Multiply => a * b,
        BinaryOp.Divide => MathF.Abs(b) < 1e-12f ? 0f : a / b,
        BinaryOp.Min => MathF.Min(a, b),
        BinaryOp.Max => MathF.Max(a, b),
        _ => a,
    };
}

/// <summary>
/// Fractal noise application to a float grid: add, multiply, or displace (level-set-safe
/// displacement) using <see cref="VdbNoise"/>.
/// </summary>
public static class VolumeNoise
{
    public enum Mode
    {
        Add,
        Multiply,
        Displace,
        Replace,
    }

    public static VdbGrid<float> Apply(VdbGrid<float> input, VdbNoise noise,
                                       float frequency, float amplitude, Vector3 offset,
                                       int octaves, VdbNoiseType noiseType, VdbNoiseFractalType fractal,
                                       Mode mode)
    {
        var result = new VdbGrid<float>(mode == Mode.Replace ? 0f : input.Background, input.Transform.VoxelSize, input.Name, input.GridClass)
        {
            Transform = input.Transform,
        };
        var scale = frequency / input.Transform.VoxelSize;

        foreach (var leaf in input.Tree.Leaves)
        {
            // Inactive voxels keep their values: for a level set they carry the interior fill.
            var values = new float[VdbLeafNode<float>.Size];
            Array.Copy(leaf.Values, values, values.Length);
            for (var i = 0; i < VdbLeafNode<float>.Size; i++)
            {
                var v = leaf.Values[i];
                var isActive = leaf.ValueMask.IsOn(i);
                if (!isActive && mode != Mode.Replace)
                    continue;

                var coord = leaf.Origin + VdbCoord.LocalFromOffset(i);
                var world = input.Transform.IndexToWorld(coord);
                var p = world * scale + offset;
                var n = noise.Fractal(noiseType, fractal, p.X, p.Y, p.Z, octaves);

                values[i] = mode switch
                {
                    Mode.Add => v + n * amplitude,
                    Mode.Multiply => v * (1f + n * amplitude),
                    Mode.Displace => v + n * amplitude * input.Transform.VoxelSize,
                    Mode.Replace => n * amplitude,
                    _ => v,
                };
            }

            var newLeaf = result.Tree.AddLeaf(leaf.Origin);
            Array.Copy(values, newLeaf.Values, values.Length);
            if (mode == Mode.Replace)
                newLeaf.ValueMask.SetAll();
            else
                newLeaf.ValueMask.Words.AsSpan().CopyFrom(leaf.ValueMask.Words);
        }

        result.Tree.Prune();
        return result;
    }
}
