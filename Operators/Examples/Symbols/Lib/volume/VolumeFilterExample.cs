namespace Examples.Lib.volume;

/// <summary>
/// Fog volumes and smoothing: [VolumeFilter] blurs the voxel values themselves (not the surface), and [VolumeToTexture3D] exposes the result to the GPU so the density can be ray-marched directly - the classic cloud/smoke workflow.
/// </summary>
[Guid("5c0daf57-944a-584a-94b2-3cdca53da082")]
internal sealed class VolumeFilterExample : Instance<VolumeFilterExample>
{
    [Output(Guid = "1bca140a-12cb-58fa-b9c6-bd61a6a3f59f")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
