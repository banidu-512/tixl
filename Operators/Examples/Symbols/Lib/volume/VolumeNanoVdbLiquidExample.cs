namespace Examples.Lib.volume;

/// <summary>
/// A sloshing liquid rendered from a NanoVDB level set in a glass box.
/// </summary>
[Guid("4a32ffa9-1654-5ffd-accc-46a58b58faa7")]
internal sealed class VolumeNanoVdbLiquidExample : Instance<VolumeNanoVdbLiquidExample>
{
    [Output(Guid = "e3ef9cf1-0e7a-512b-a295-0b2ac2449fac")]
    public readonly Slot<Texture2D> ColorBuffer = new();
}
