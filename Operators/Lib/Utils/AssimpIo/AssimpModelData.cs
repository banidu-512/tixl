#nullable enable
using T3.Core.Rendering;

namespace Lib.Utils.AssimpIo;

internal enum AssimpPostProcessPresets
{
    None,
    Basic,
    Full,
}

internal enum AssimpAxisConversions
{
    None,
    YUpToZUp,
    ZUpToYUp,
    FlipX,
    FlipY,
    FlipZ,
}

/// <summary>Everything that changes the result of an import. Equality decides whether a reload is needed.</summary>
internal readonly record struct AssimpImportSettings(AssimpPostProcessPresets Preset,
                                                     float Scale,
                                                     bool Normalize,
                                                     AssimpAxisConversions AxisConversion,
                                                     bool FlipNormals,
                                                     int SelectedMeshIndex);

/// <summary>
/// Per-vertex joint influences in the layout of the skinning compute shader used by [SkinMesh]
/// (identical to the one [LoadGltfScene] produces).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
internal struct AssimpSkinWeight
{
    [FieldOffset(0)]
    public Int4 JointIndices;

    [FieldOffset(16)]
    public Vector4 Weights;

    public const int Stride = 32;
}

/// <summary>
/// CPU-side result of an Assimp import. Built on a worker thread; only the owning operator
/// turns it into GPU resources, on the render thread. Never mutated after publication.
/// </summary>
internal sealed class AssimpModelData
{
    public PbrVertex[] Vertices = [];
    public Int3[] Triangles = [];

    /// <summary>One chunk per imported mesh instance, so chunk draws can address single objects of the file.</summary>
    public MeshChunkDef[] Chunks = [];

    public Point[] Points = [];

    /// <summary>Polygonal geometry (N-gons preserved, positions welded) for the procedural geometry operators.</summary>
    public MeshGeometry? Geometry;

    /// <summary>Parallel to <see cref="Vertices"/>; null for files without bones.</summary>
    public AssimpSkinWeight[]? SkinWeights;

    public SceneSetup.SceneSkeleton? Skeleton;
    public Point[] SkeletonRestPoints = [];
    public readonly List<SceneSetup.SceneAnimClip> AnimationClips = [];

    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    public int FileMeshCount;
    public int UsedMeshInstanceCount;
    public int BoneCount;
    public string MeshNames = string.Empty;
    public string AnimationNames = string.Empty;

    public readonly List<string> Warnings = [];
}
