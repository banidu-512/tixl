namespace Examples.Lib.volume;

/// <summary>
/// Changes a volume's voxel size with trilinear resampling. A coarse torus is resampled from 6 cm to 2 cm voxels before meshing, which costs memory but sharpens the extracted surface.
/// </summary>
[Guid("d402b885-e2e4-5370-b14f-10302c54f7b2")]
internal sealed class VolumeResampleExample : Instance<VolumeResampleExample>
{
    [Output(Guid = "5b645c6f-0049-575c-99b6-a2e212923880")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
