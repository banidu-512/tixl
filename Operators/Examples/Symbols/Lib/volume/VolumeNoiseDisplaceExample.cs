namespace Examples.Lib.volume;

/// <summary>
/// Animated volume noise: [VolumeNoise] in Displace mode pushes a sphere's surface around, then [VolumeToMesh] turns the result into a mesh. Drive the Offset with [Time] for a continuous boil. Great for organic blobs, asteroids and clouds.
/// </summary>
[Guid("c6c76c8f-9879-5bab-b3b2-edbad91c93c2")]
internal sealed class VolumeNoiseDisplaceExample : Instance<VolumeNoiseDisplaceExample>
{
    [Output(Guid = "1421f349-ba2f-5d8d-8b47-5360d557e694")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
