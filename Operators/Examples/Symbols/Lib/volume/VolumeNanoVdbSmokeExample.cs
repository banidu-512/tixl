namespace Examples.Lib.volume;

/// <summary>
/// Billowing smoke rendered from a NanoVDB level set.
/// </summary>
[Guid("a5e30eab-98f2-543f-9d82-d04d04b001df")]
internal sealed class VolumeNanoVdbSmokeExample : Instance<VolumeNanoVdbSmokeExample>
{
    [Output(Guid = "daf9c4ae-237d-513a-a8d9-2b27ad3e37b9")]
    public readonly Slot<Texture2D> ColorBuffer = new();
}
