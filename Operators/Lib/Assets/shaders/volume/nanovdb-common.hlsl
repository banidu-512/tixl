// Shared NanoVDB traversal for TiXL compute shaders.
//
// Layout constants and the tree walk are the ones NanoVdbVolume.hlsl has been using; they follow
// nanovdb/NanoVDB.h (NanoVDB 32.11) as written by NanoVdbBuffer and the reference nanovdb_convert.
// Keep them in sync with Core/DataTypes/Volume/Tools/NanoVdbBuffer.cs if either side changes.
//
// Generic over the value type where it matters: the offsets below describe a float grid. A vec3
// grid has 12 extra bytes of node statistics in root/upper/lower headers, so its table offsets
// differ - read those grids with their own constants, not with these.

#ifndef TIXL_NANOVDB_COMMON
#define TIXL_NANOVDB_COMMON

static const uint GridDataBytes      = 672;   // GridData: magic..+file fields, map, bbox, name
static const uint MapMatfBytes       = 296;   // Map: matf[9], invmatf[9], vecf[3] at 296+72
static const uint MapVecfBytes       = 296 + 72;
static const uint RootHeaderBytes    = 64;    // bbox + tableSize + background/min/max + avg/dev
static const uint RootTileBytes      = 32;    // key, child, state, value
static const uint UpperChildMaskBytes = 24 + 8 + 4096;
static const uint UpperTableBytes    = 8256;
static const uint LowerChildMaskBytes = 24 + 8 + 512;
static const uint LowerTableBytes    = 1088;
static const uint LeafValuesBytes    = 96;    // bbox 12 + dif 3 + flags 1 + mask 64 + stats 16
static const uint LeafValueMaskBytes = 16;    // offset of the 64-byte value mask word array
static const uint LeafValueCount     = 512;
static const uint LeafValueStride    = 4;     // bytes per value (float grid)

struct VdbGridInfo
{
    uint rootOffset;
    uint tileCount;
    float background;
    float voxelSize;
    float3 translation;
    int3 indexMin;
    int3 indexMax;
    float3 worldMin;
    float3 worldMax;
};

VdbGridInfo VdbReadGridInfo(StructuredBuffer<uint> g)
{
    VdbGridInfo info;
    info.rootOffset = GridDataBytes + g[(GridDataBytes + 24) >> 2];
    uint root = info.rootOffset >> 2;
    info.indexMin = int3(g[root], g[root + 1], g[root + 2]);
    info.indexMax = int3(g[root + 3], g[root + 4], g[root + 5]);
    info.tileCount = g[root + 6];
    info.background = asfloat(g[root + 7]);
    info.voxelSize = asfloat(g[MapMatfBytes >> 2]);
    uint vec = MapVecfBytes >> 2;
    info.translation = float3(asfloat(g[vec]), asfloat(g[vec + 1]), asfloat(g[vec + 2]));

    // Outside this box every sample is the background, so callers can reject early.
    info.worldMin = info.translation + (info.indexMin - 1) * info.voxelSize;
    info.worldMax = info.translation + (info.indexMax + 1) * info.voxelSize;
    return info;
}

/// Tree section offsets are relative to the start of the grid buffer, stored tree-relative.
uint VdbLeafSection(StructuredBuffer<uint> g)
{
    return GridDataBytes + g[GridDataBytes >> 2];
}

uint VdbLeafCount(StructuredBuffer<uint> g)
{
    return g[(GridDataBytes + 32) >> 2];
}

/// Byte stride of one leaf node: its header, its 512 value mask bits, its stats and its values.
uint VdbLeafStride()
{
    return LeafValuesBytes + LeafValueCount * LeafValueStride;
}

// ---------- internal nodes, for diagnostics that walk the tree itself ----------

static const uint LowerNodeStride = LowerTableBytes + 4096 * 8;     // 16^3 slots of child+value
static const uint UpperNodeStride = UpperTableBytes + 32768 * 8;    // 32^3 slots of child+value
static const uint LowerNodeDim    = 128;                            // extent of one lower node
static const uint UpperNodeDim    = 4096;                           // extent of one upper node

/// Byte offset of the section holding the node level: 0 leaf, 1 lower, 2 upper.
uint VdbNodeSection(StructuredBuffer<uint> g, uint level)
{
    uint pointer = level == 0 ? GridDataBytes : (level == 1 ? GridDataBytes + 8 : GridDataBytes + 16);
    return GridDataBytes + g[pointer >> 2];
}

/// How many nodes the level holds, as stored in the tree header.
uint VdbNodeCount(StructuredBuffer<uint> g, uint level)
{
    uint pointer = level == 0 ? (GridDataBytes + 32) : (level == 1 ? (GridDataBytes + 36) : (GridDataBytes + 40));
    return g[pointer >> 2];
}

uint VdbNodeStride(uint level)
{
    return level == 0 ? VdbLeafStride() : (level == 1 ? LowerNodeStride : UpperNodeStride);
}

uint VdbNodeDim(uint level)
{
    return level == 0 ? 8 : (level == 1 ? LowerNodeDim : UpperNodeDim);
}

/// Every node begins with the same bbox triple, so one reader covers all three levels.
int3 VdbNodeBBoxMin(StructuredBuffer<uint> g, uint nodeAddress)
{
    uint b = nodeAddress >> 2;
    return int3(g[b], g[b + 1], g[b + 2]);
}

/// World position of an index-space corner, without the half-voxel offset used for samples.
float3 VdbIndexCornerWorldPosition(in VdbGridInfo info, int3 indexCorner)
{
    return info.translation + (float3)indexCorner * info.voxelSize;
}

/// True when voxel `slot` (0..511, order (x<<6)|(y<<3)|z) is active inside this leaf.
bool VdbLeafIsActive(StructuredBuffer<uint> g, uint leafAddress, uint slot)
{
    uint maskBase = (leafAddress + LeafValueMaskBytes) >> 2;
    uint word = g[maskBase + (slot >> 5)];
    return (word & (1u << (slot & 31))) != 0;
}

float VdbLeafValue(StructuredBuffer<uint> g, uint leafAddress, uint slot)
{
    return asfloat(g[((leafAddress + LeafValuesBytes) >> 2) + slot]);
}

int3 VdbLeafBBoxMin(StructuredBuffer<uint> g, uint leafAddress)
{
    uint b = leafAddress >> 2;
    return int3(g[b], g[b + 1], g[b + 2]);
}

/// Index-space coordinate of a voxel inside its leaf, in NanoVDB's value order.
int3 VdbLeafVoxelCoord(int3 bboxMin, uint slot)
{
    return bboxMin + int3((slot >> 6) & 7, (slot >> 3) & 7, slot & 7);
}

/// World position of a voxel centre, for a grid whose map is translation + uniform voxel size.
float3 VdbVoxelWorldPosition(in VdbGridInfo info, int3 ijk)
{
    return info.translation + ((float3)ijk + 0.5) * info.voxelSize;
}

#endif // TIXL_NANOVDB_COMMON
