#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace T3.Core.DataTypes.Volume;

/// <summary>
/// Flattens a <see cref="VdbGrid{T}"/> into the NanoVDB linear memory layout (v32): a single
/// contiguous, 32-byte-aligned buffer of [GridData 672B][TreeData 64B][Root + tiles][Upper nodes]
/// [Lower nodes][Leaf nodes], breadth-first with child links as relative byte offsets.
/// The buffer is directly uploadable to the GPU (e.g. as a StructuredBuffer&lt;uint&gt; for HLSL
/// HDDA traversal) and serializes to .nvdb files the reference NanoVDB library reads.
/// All field encodings follow nanovdb/NanoVDB.h (NanoVDB 32 / OpenVDB 12). Supports float and
/// vec3f value grids.
/// </summary>
public sealed class NanoVdbBuffer
{
    public const ulong MagicNumber = 0x304244566f6e614eUL;   // "NanoVDB0" (in-memory grid)
    public const ulong MagicFile = 0x324244566f6e614eUL;     // "NanoVDB2" (.nvdb file header)
    public const ulong MagicGrid = 0x314244566f6e614eUL;     // "NanoVDB1" (grid inside files)
    public const ulong EmptyChecksum = ulong.MaxValue;       // disabled checksum

    public const int GridDataSize = 672;
    public const int TreeDataSize = 64;
    private const int Alignment = 32;

    // Node layout constants; min/max use the value type, avg/dev stay float (NanoVDB GridTraits).
    private const int UpperSlots = 32768;
    private const int LowerSlots = 4096;
    private const int RootTileSize = 32;          // key 8 + child 8 + state 4 + value 4, aligned

    private static int StatsBytes<T>() => typeof(T) == typeof(Vector3) ? 12 + 12 + 4 + 4 : 16;
    private static int UpperHeader<T>() => 24 + 8 + 4096 + 4096 + StatsBytes<T>();
    private static int UpperHeaderPadded<T>() => AlignUp(UpperHeader<T>());
    private static int LowerHeader<T>() => 24 + 8 + 512 + 512 + StatsBytes<T>();
    private static int LowerHeaderPadded<T>() => AlignUp(LowerHeader<T>());
    private static int LeafHeader<T>() => 12 + 3 + 1 + 64 + StatsBytes<T>();
    private static int RootHeader<T>() => 24 + 4 + 3 * ValueBytes<T>() + 2 * 4;   // bbox + tableSize + background/min/max + avg/dev floats
    private static int RootHeaderPadded<T>() => AlignUp(RootHeader<T>());

    public byte[] Buffer { get; }
    public VdbValueType ValueType { get; }
    public VdbGridClass GridClass { get; }

    /// <summary>Actual section end offsets of the last built buffer (diagnostics/testing): root, upper, lower, leaf.</summary>
    internal static long[] LastSectionEnds = new long[4];

    private NanoVdbBuffer(byte[] buffer, VdbValueType valueType, VdbGridClass gridClass)
    {
        Buffer = buffer;
        ValueType = valueType;
        GridClass = gridClass;
    }

    public static NanoVdbBuffer FromGrid(VdbGrid<float> grid) => new(Build(grid), VdbValueType.Float, grid.GridClass);

    public static NanoVdbBuffer FromGrid(VdbGrid<Vector3> grid) => new(Build(grid), VdbValueType.Vec3Float, grid.GridClass);

    private static int AlignUp(int bytes) => (bytes + Alignment - 1) / Alignment * Alignment;

    private static uint PackVersion(int major, int minor, int patch) => (uint)(major << 21 | minor << 10 | patch);

    private static int CompareCoord(VdbCoord a, VdbCoord b)
        => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

    private static void WriteUtf8Name(BinaryWriter w, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name ?? string.Empty);
        if (bytes.Length > 255)
            Array.Resize(ref bytes, 255);
        w.Write(bytes);
        for (var i = bytes.Length; i < 256; i++)
            w.Write((byte)0);
    }

    /// <summary>Packs a root child origin (multiple of 4096) into NanoVDB's single 64-bit root key.</summary>
    public static ulong RootCoordToKey(VdbCoord origin)
    {
        const ulong mask21 = (1UL << 21) - 1;
        return ((((ulong)origin.X >> 12) & mask21) << 42)
               | ((((ulong)origin.Y >> 12) & mask21) << 21)
               | (((ulong)origin.Z >> 12) & mask21);
    }

    private static uint GridClassValue(VdbGridClass c) => c switch
    {
        VdbGridClass.LevelSet => 1,
        VdbGridClass.FogVolume => 2,
        VdbGridClass.Staggered => 3,
        _ => 0,
    };

    private static uint GridTypeValue<T>() => typeof(T) == typeof(Vector3) ? 6u : 1u; // GridType::Vec3f / Float

    private static void WriteValue<T>(BinaryWriter w, T value)
    {
        if (value is float f)
        {
            w.Write(f);
        }
        else if (value is Vector3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
        else
        {
            throw new NotSupportedException("Only float and Vector3 grids are supported");
        }
    }

    private static int ValueBytes<T>() => typeof(T) == typeof(Vector3) ? 12 : 4;

    private static void WriteZeroed(BinaryWriter w, int bytes)
    {
        for (var i = 0; i < bytes; i++)
            w.Write((byte)0);
    }

    private static void WriteBBox(BinaryWriter w, VdbCoordBBox bbox)
    {
        w.Write(bbox.Min.X);
        w.Write(bbox.Min.Y);
        w.Write(bbox.Min.Z);
        w.Write(bbox.Max.X);
        w.Write(bbox.Max.Y);
        w.Write(bbox.Max.Z);
    }

    private static void WriteMap(BinaryWriter w, VdbTransform t)
    {
        // Map = 264B: matf[9], invmatf[9], vecf[3], taperf, matd[9], invmatd[9], vecd[3], taperd.
        var s = (double)t.VoxelSize;
        var tr = t.Translation;
        Span<double> d = stackalloc double[9];

        // matf: 3x3 row-major scale; inverse on the diagonal for invmatf.
        WriteFloatRow(w, (float)s);
        WriteFloatRow(w, 1f / (float)s);
        w.Write((float)tr.X);
        w.Write((float)tr.Y);
        w.Write((float)tr.Z);
        w.Write(0f); // taperf

        for (var i = 0; i < 9; i++)
            d[i] = 0;
        d[0] = s;
        d[4] = s;
        d[8] = s;
        for (var i = 0; i < 9; i++)
            w.Write(d[i]);

        for (var i = 0; i < 9; i++)
            d[i] = 0;
        d[0] = 1 / s;
        d[4] = 1 / s;
        d[8] = 1 / s;
        for (var i = 0; i < 9; i++)
            w.Write(d[i]);

        w.Write((double)tr.X);
        w.Write((double)tr.Y);
        w.Write((double)tr.Z);
        w.Write(0.0); // taperd

        static void WriteFloatRow(BinaryWriter w, float diagonal)
        {
            w.Write(diagonal);
            w.Write(0f);
            w.Write(0f);
            w.Write(0f);
            w.Write(diagonal);
            w.Write(0f);
            w.Write(0f);
            w.Write(0f);
            w.Write(diagonal);
        }
    }

    /// <summary>Min/max/avg/stddev over a node's active values; vec3 grids store per-component stats.</summary>
    private struct NodeStats
    {
        public float MinScalar = float.MaxValue;
        public float MaxScalar = float.MinValue;
        public Vector3 MinVec = new(float.MaxValue);
        public Vector3 MaxVec = new(float.MinValue);
        public double Sum;
        public double SumSquares;
        public Vector3 SumVec;
        public Vector3 SumSquaresVec;
        public long Count;

        public NodeStats()
        {
        }

        public void Add(float v)
        {
            if (v < MinScalar)
                MinScalar = v;
            if (v > MaxScalar)
                MaxScalar = v;
            Sum += v;
            SumSquares += (double)v * v;
            Count++;
        }

        public void Add(Vector3 v)
        {
            MinVec = Vector3.Min(MinVec, v);
            MaxVec = Vector3.Max(MaxVec, v);
            SumVec += v;
            SumSquaresVec += v * v;
            Count++;
        }

        public void Merge(in NodeStats other)
        {
            if (other.Count == 0)
                return;
            MinScalar = MathF.Min(MinScalar, other.MinScalar);
            MaxScalar = MathF.Max(MaxScalar, other.MaxScalar);
            MinVec = Vector3.Min(MinVec, other.MinVec);
            MaxVec = Vector3.Max(MaxVec, other.MaxVec);
            Sum += other.Sum;
            SumSquares += other.SumSquares;
            SumVec += other.SumVec;
            SumSquaresVec += other.SumSquaresVec;
            Count += other.Count;
        }

        public void Write(BinaryWriter w, bool isVec3)
        {
            if (Count == 0)
            {
                WriteZero(w, isVec3);
                return;
            }

            if (isVec3)
            {
                // min/max keep the value type (Vec3f); avg/dev are scalar floats in NanoVDB.
                var avg = SumVec / Count;
                var dev = Vector3.SquareRoot(Vector3.Max(SumSquaresVec / Count - avg * avg, Vector3.Zero));
                WriteVec(w, MinVec);
                WriteVec(w, MaxVec);
                w.Write((avg.X + avg.Y + avg.Z) / 3f);
                w.Write((dev.X + dev.Y + dev.Z) / 3f);
            }
            else
            {
                var mean = Sum / Count;
                var dev = (float)Math.Sqrt(Math.Max(SumSquares / Count - mean * mean, 0.0));
                w.Write(MinScalar);
                w.Write(MaxScalar);
                w.Write((float)mean);
                w.Write(dev);
            }
        }

        private void WriteZero(BinaryWriter w, bool isVec3)
        {
            for (var i = 0; i < 4; i++)
            {
                if (isVec3)
                {
                    w.Write(0f);
                    w.Write(0f);
                    w.Write(0f);
                }
                else
                {
                    w.Write(0f);
                }
            }
        }

        private static void WriteVec(BinaryWriter w, Vector3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
    }

    private static NodeStats StatsOfLeaf<T>(VdbLeafNode<T> leaf)
    {
        var stats = new NodeStats();
        foreach (var i in leaf.ValueMask.OnBits())
        {
            var v = leaf.Values[i];
            if (v is float f)
                stats.Add(f);
            else if (v is Vector3 v3)
                stats.Add(v3);
        }

        return stats;
    }

    private static NodeStats StatsOfLower<T>(VdbInternalNode1<T> node)
    {
        var stats = new NodeStats();
        foreach (var slot in node.ValueMask.OnBits())
        {
            if (node.ChildMask.IsOn(slot))
                continue;
            var v = node.Values[slot];
            if (v is float f)
                stats.Add(f);
            else if (v is Vector3 v3)
                stats.Add(v3);
        }

        foreach (var leaf in node.Children)
        {
            if (leaf != null)
                stats.Merge(StatsOfLeaf(leaf));
        }

        return stats;
    }

    private static NodeStats StatsOfUpper<T>(VdbInternalNode2<T> node)
    {
        var stats = new NodeStats();
        foreach (var slot in node.ValueMask.OnBits())
        {
            if (node.ChildMask.IsOn(slot))
                continue;
            var v = node.Values[slot];
            if (v is float f)
                stats.Add(f);
            else if (v is Vector3 v3)
                stats.Add(v3);
        }

        foreach (var node1 in node.Children)
        {
            if (node1 != null)
                stats.Merge(StatsOfLower(node1));
        }

        return stats;
    }

    private static NodeStats CollectAllStats<T>(VdbTree<T> tree)
    {
        var stats = new NodeStats();
        foreach (var leaf in tree.Leaves)
            stats.Merge(StatsOfLeaf(leaf));
        foreach (var tile in tree.Root.Tiles.Values)
        {
            if (!tile.Active)
                continue;
            if (tile.Value is float f)
                stats.Add(f);
            else if (tile.Value is Vector3 v)
                stats.Add(v);
        }

        return stats;
    }

    private static byte[] Build<T>(VdbGrid<T> grid)
    {
        var tree = grid.Tree;

        // Deterministic breadth-first ordering: sorted uppers, lowers, leaves.
        var upperOrigins = new List<VdbCoord>(tree.Root.Children.Count);
        foreach (var origin in tree.Root.Children.Keys)
            upperOrigins.Add(origin);
        upperOrigins.Sort(CompareCoord);

        var upperIndex = new Dictionary<VdbCoord, int>();
        var lowers = new List<(VdbCoord Origin, VdbInternalNode1<T> Node)>();
        for (var i = 0; i < upperOrigins.Count; i++)
        {
            upperIndex[upperOrigins[i]] = i;
            var node2 = tree.Root.Children[upperOrigins[i]];
            for (var slot = 0; slot < node2.Children.Length; slot++)
            {
                var node1 = node2.Children[slot];
                if (node1 != null)
                    lowers.Add((node1.Origin, node1));
            }
        }

        lowers.Sort((a, b) => CompareCoord(a.Origin, b.Origin));
        var lowerIndex = new Dictionary<VdbCoord, int>();
        var leaves = new List<(VdbCoord Origin, VdbLeafNode<T> Leaf)>();
        for (var i = 0; i < lowers.Count; i++)
        {
            lowerIndex[lowers[i].Origin] = i;
            var node1 = lowers[i].Node;
            for (var slot = 0; slot < node1.Children.Length; slot++)
            {
                var leaf = node1.Children[slot];
                if (leaf != null)
                    leaves.Add((leaf.Origin, leaf));
            }
        }

        leaves.Sort((a, b) => CompareCoord(a.Origin, b.Origin));
        var leafIndex = new Dictionary<VdbCoord, int>();
        for (var i = 0; i < leaves.Count; i++)
            leafIndex[leaves[i].Origin] = i;

        var upperHeaderPadded = UpperHeaderPadded<T>();
        var lowerHeaderPadded = LowerHeaderPadded<T>();
        var rootHeaderPadded = RootHeaderPadded<T>();
        var valueBytes = ValueBytes<T>();
        var leafSize = LeafHeader<T>() + VdbLeafNode<T>.Size * valueBytes;
        var upperStride = upperHeaderPadded + UpperSlots * 8;
        var lowerStride = lowerHeaderPadded + LowerSlots * 8;

        // All section positions are absolute in the buffer; TreeData stores them tree-relative.
        var treeStart = (long)GridDataSize;
        var rootOffset = treeStart + TreeDataSize;
        var rootTableSize = tree.Root.Children.Count + tree.Root.Tiles.Count;
        var upperOffset = rootOffset + rootHeaderPadded + rootTableSize * RootTileSize;
        var lowerOffset = upperOffset + upperOrigins.Count * upperStride;
        var leafOffset = lowerOffset + lowers.Count * lowerStride;
        var gridSize = AlignUp((int)(leafOffset + leaves.Count * leafSize));

        var worldBBox = grid.ActiveWorldBBox();

        using var ms = new MemoryStream(gridSize);
        using var w = new BinaryWriter(ms);

        // ---------- GridData (672 bytes)
        w.Write(MagicNumber);
        w.Write(EmptyChecksum);                                     // checksum disabled
        w.Write(PackVersion(32, 11, 0));                            // NanoVDB 32.11
        const uint gridFlags = (1u << 5) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4); // IsBreadthFirst | HasBBox | HasMinMax | HasAverage | HasStdDeviation
        w.Write(gridFlags);
        w.Write(0u);                                                // gridIndex
        w.Write(1u);                                                // gridCount
        w.Write((ulong)gridSize);
        WriteUtf8Name(w, grid.Name);
        WriteMap(w, grid.Transform);
        w.Write((double)worldBBox.Min.X);
        w.Write((double)worldBBox.Min.Y);
        w.Write((double)worldBBox.Min.Z);
        w.Write((double)worldBBox.Max.X);
        w.Write((double)worldBBox.Max.Y);
        w.Write((double)worldBBox.Max.Z);
        w.Write((double)grid.Transform.VoxelSize);
        w.Write((double)grid.Transform.VoxelSize);
        w.Write((double)grid.Transform.VoxelSize);
        w.Write(GridClassValue(grid.GridClass));
        w.Write(GridTypeValue<T>());
        w.Write(0L);                                                // blindMetadataOffset
        w.Write(0u);                                                // blindMetadataCount
        w.Write(0u);
        w.Write(0UL);
        w.Write(0UL);
        w.Flush();
        if (ms.Length != GridDataSize)
            throw new InvalidOperationException($"GridData layout produced {ms.Length} bytes, expected {GridDataSize}");

        // ---------- TreeData (64 bytes): offsets (leaf, lower, upper, root) are tree-relative
        w.Write((ulong)(leafOffset - treeStart));
        w.Write((ulong)(lowerOffset - treeStart));
        w.Write((ulong)(upperOffset - treeStart));
        w.Write((ulong)(rootOffset - treeStart));
        w.Write((uint)leaves.Count);
        w.Write((uint)lowers.Count);
        w.Write((uint)upperOrigins.Count);
        w.Write((uint)CountTiles(tree, upper: false));
        w.Write((uint)CountTiles(tree, upper: true));
        w.Write((uint)rootTableSize);
        w.Write((ulong)tree.ActiveVoxelCount);
        w.Flush();
        if (ms.Length != GridDataSize + TreeDataSize)
            throw new InvalidOperationException($"TreeData layout produced {ms.Length} bytes, expected {GridDataSize + TreeDataSize}");

        // ---------- RootData + tiles
        WriteBBox(w, tree.ActiveIndexBBox());
        w.Write((uint)rootTableSize);
        WriteValue(w, tree.Root.Background);
        CollectAllStats(tree).Write(w, typeof(T) == typeof(Vector3));
        w.Flush();
        WriteZeroed(w, (int)(rootHeaderPadded - (ms.Length - rootOffset)));

        // Root children and constant tiles live in separate dictionaries but share one NanoVDB table.
        var rootEntries = new List<(VdbCoord Coord, VdbRootTile<T>? Tile)>(rootTableSize);
        foreach (var origin in upperOrigins)
            rootEntries.Add((origin, null));
        foreach (var (coord, tile) in tree.Root.Tiles)
            rootEntries.Add((coord, tile));
        rootEntries.Sort((a, b) => CompareCoord(a.Coord, b.Coord));

        foreach (var (coord, tile) in rootEntries)
        {
            w.Write(RootCoordToKey(coord));
            if (tile == null)
            {
                var childPos = upperOffset + upperIndex[coord] * upperStride;
                w.Write(childPos - rootOffset);
                w.Write(0u);
                WriteValue(w, tree.Root.Background);
            }
            else
            {
                w.Write(0L);
                w.Write(tile.Active ? 1u : 0u);                     // state
                WriteValue(w, tile.Value);
            }

            WriteZeroed(w, RootTileSize - 20 - valueBytes);         // key+child+state+value then pad to 32
        }

        LastSectionEnds[0] = ms.Position;

        // ---------- Upper (level-2) nodes
        foreach (var origin in upperOrigins)
        {
            var node2 = tree.Root.Children[origin];
            var nodeStart = ms.Position;
            WriteBBox(w, node2.BBox);
            w.Write((1UL << 1) | (1UL << 4));                       // HasBBox | HasStats
            node2.ValueMask.Save(w);
            node2.ChildMask.Save(w);
            StatsOfUpper(node2).Write(w, typeof(T) == typeof(Vector3));
            var written = ms.Position - nodeStart;
            WriteZeroed(w, (int)(upperHeaderPadded - written));

            for (var slot = 0; slot < UpperSlots; slot++)
            {
                var node1 = node2.Children[slot];
                if (node1 != null)
                {
                    var lowerPos = lowerOffset + lowerIndex[node1.Origin] * lowerStride;
                    w.Write(lowerPos - nodeStart);
                }
                else
                {
                    // Union slot: tile value in the first bytes, zero child id.
                    WriteValue(w, node2.ValueMask.IsOn(slot) ? node2.Values[slot] : tree.Root.Background);
                    WriteZeroed(w, 8 - valueBytes);
                }
            }
        }

        LastSectionEnds[1] = ms.Position;

        // ---------- Lower (level-1) nodes
        foreach (var (_, node1) in lowers)
        {
            var nodeStart = ms.Position;
            WriteBBox(w, node1.BBox);
            w.Write((1UL << 1) | (1UL << 4));
            node1.ValueMask.Save(w);
            node1.ChildMask.Save(w);
            StatsOfLower(node1).Write(w, typeof(T) == typeof(Vector3));
            var written = ms.Position - nodeStart;
            WriteZeroed(w, (int)(lowerHeaderPadded - written));

            for (var slot = 0; slot < LowerSlots; slot++)
            {
                var leaf = node1.Children[slot];
                if (leaf != null)
                {
                    var leafPos = leafOffset + leafIndex[leaf.Origin] * leafSize;
                    w.Write(leafPos - nodeStart);
                }
                else
                {
                    WriteValue(w, node1.ValueMask.IsOn(slot) ? node1.Values[slot] : tree.Root.Background);
                    WriteZeroed(w, 8 - valueBytes);
                }
            }
        }

        LastSectionEnds[2] = ms.Position;

        // ---------- Leaves
        foreach (var (_, leaf) in leaves)
        {
            w.Write(leaf.Origin.X);
            w.Write(leaf.Origin.Y);
            w.Write(leaf.Origin.Z);
            w.Write((byte)7);
            w.Write((byte)7);
            w.Write((byte)7);
            w.Write((byte)((1 << 1) | (1 << 4)));                   // HasBBox | HasStats
            leaf.ValueMask.Save(w);
            StatsOfLeaf(leaf).Write(w, typeof(T) == typeof(Vector3));
            if (leaf.Values is float[] floats)
            {
                for (var i = 0; i < VdbLeafNode<T>.Size; i++)
                    w.Write(floats[i]);
            }
            else if (leaf.Values is Vector3[] vectors)
            {
                for (var i = 0; i < VdbLeafNode<T>.Size; i++)
                {
                    var v = vectors[i];
                    w.Write(v.X);
                    w.Write(v.Y);
                    w.Write(v.Z);
                }
            }
            else
            {
                throw new NotSupportedException("Only float and Vector3 grids are supported");
            }
        }

        LastSectionEnds[3] = ms.Position;
        WriteZeroed(w, (int)(gridSize - ms.Length));
        w.Flush();
        var result = ms.ToArray();
        if (result.Length != gridSize)
            throw new InvalidOperationException($"NanoVDB buffer size {result.Length} != planned {gridSize}");
        return result;
    }

    private static int CountTiles<T>(VdbTree<T> tree, bool upper)
    {
        var count = 0;
        foreach (var node2 in tree.Root.Children.Values)
        {
            if (upper)
            {
                count += CountTileSlots(node2.ValueMask, node2.ChildMask);
                continue;
            }

            foreach (var node1 in node2.Children)
            {
                if (node1 == null)
                    continue;
                count += CountTileSlots(node1.ValueMask, node1.ChildMask);
            }
        }

        return count;

        static int CountTileSlots(NodeMask valueMask, NodeMask childMask)
        {
            var c = 0;
            foreach (var slot in valueMask.OnBits())
            {
                if (!childMask.IsOn(slot))
                    c++;
            }

            return c;
        }
    }

    // ---------- IO ----------

    /// <summary>Writes the .nvdb file wrapper (16-byte FileHeader) around the grid buffer.</summary>
    public void Save(string path)
    {
        using var stream = File.Create(path);
        Save(stream);
    }

    public void Save(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(header, MagicFile);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], PackVersion(32, 11, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0); // codec: none
        stream.Write(header);
        stream.Write(Buffer, 0, Buffer.Length);
    }

    public static NanoVdbBuffer Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static NanoVdbBuffer Load(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadBytes(ms.ToArray());
    }

    public static NanoVdbBuffer LoadBytes(byte[] bytes)
    {
        if (bytes.Length < 16)
            throw new InvalidDataException("NanoVDB buffer too small");

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        int gridStart;
        switch (magic)
        {
            case MagicFile:
                gridStart = 16;
                break;
            case MagicNumber:
            case MagicGrid:
                gridStart = 0;
                break;
            default:
                throw new InvalidDataException("Not a NanoVDB buffer");
        }

        var grid = bytes[gridStart..];
        if (grid.Length < GridDataSize)
            throw new InvalidDataException("NanoVDB grid truncated");

        var valueType = BinaryPrimitives.ReadUInt32LittleEndian(grid.AsSpan(636)) switch
        {
            1 => VdbValueType.Float,
            6 => VdbValueType.Vec3Float,
            _ => VdbValueType.Float,
        };
        var gridClass = BinaryPrimitives.ReadUInt32LittleEndian(grid.AsSpan(632)) switch
        {
            1 => VdbGridClass.LevelSet,
            2 => VdbGridClass.FogVolume,
            3 => VdbGridClass.Staggered,
            _ => VdbGridClass.Unknown,
        };

        // Normalize an in-file grid magic so the buffer also works as a standalone grid.
        if (BinaryPrimitives.ReadUInt64LittleEndian(grid) == MagicGrid)
            BinaryPrimitives.WriteUInt64LittleEndian(grid, MagicNumber);

        return new NanoVdbBuffer(grid, valueType, gridClass);
    }

    /// <summary>Parsed header fields for UI/diagnostics.</summary>
    public (int LeafNodes, int LowerNodes, int UpperNodes, long VoxelCount, string Name) ReadSummary()
    {
        var treeOffset = GridDataSize;
        var leafCount = BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(treeOffset + 32));
        var lowerCount = BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(treeOffset + 36));
        var upperCount = BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(treeOffset + 40));
        var voxelCount = BinaryPrimitives.ReadInt64LittleEndian(Buffer.AsSpan(treeOffset + 56));
        var nameStart = 40;                                       // mGridName lives inside GridData
        var nameEnd = Array.IndexOf(Buffer, (byte)0, nameStart, 256);
        if (nameEnd < 0)
            nameEnd = nameStart + 256;
        var name = System.Text.Encoding.UTF8.GetString(Buffer, nameStart, nameEnd - nameStart);
        return ((int)leafCount, (int)lowerCount, (int)upperCount, voxelCount, name);
    }
}
