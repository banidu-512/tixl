namespace Examples.Lib.volume;

/// <summary>
/// Fire, smoke and liquid in a glass box, raymarched on the GPU from NanoVDB buffers.
///
/// Three volumes are generated and animated on the CPU: a sloshing liquid slab, a flame capsule and a smoke sphere, each displaced by [VolumeNoise]. [VolumeToNanoVdb] flattens every one into a NanoVDB buffer and [NanoVdbRender] walks the trees in HLSL to draw all three inside the box.
/// </summary>
[Guid("e44dccac-69b6-5a5c-9396-411445c408f0")]
internal sealed class VolumeNanoVdbBoxExample : Instance<VolumeNanoVdbBoxExample>
{
    [Output(Guid = "18a45aa5-cbe6-533a-b63e-d499448ba874")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
