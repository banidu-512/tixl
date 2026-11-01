namespace Examples.Lib.volume;

/// <summary>
/// Mesh to volume and back: [VolumeFromMesh] voxelizes a mesh into an SDF, [VolumeMorphology] dilates it, and [VolumeToMesh] re-extracts a watertight surface. This is the workflow for remeshing, booleaning and smoothing arbitrary geometry.
/// </summary>
[Guid("f44e5acd-ee4f-570f-9d48-f2248c42297d")]
internal sealed class VolumeMeshRoundtripExample : Instance<VolumeMeshRoundtripExample>
{
    [Output(Guid = "670dbf16-8af6-5233-a6f2-40cf4d4c6cd4")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
