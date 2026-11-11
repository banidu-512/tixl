namespace Examples.Lib.volume;

/// <summary>
/// Both directions between volumes and points: [VolumeToPoints] samples the active voxels as a point cloud (drawn with [DrawPoints]), and [VolumeFromPoints] splats those points back into a density volume that [VolumeToMesh] turns into a surface.
/// </summary>
[Guid("91bb0f8b-593c-557c-9da6-15005722b733")]
internal sealed class VolumeToPointsExample : Instance<VolumeToPointsExample>
{
    [Output(Guid = "267e084f-5393-5b46-83b0-c8c4c901120c")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
