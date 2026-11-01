namespace Examples.Lib.volume;

/// <summary>
/// A sphere with a box carved out of it, rendered as a NanoVDB liquid surface.
/// </summary>
[Guid("426795c0-91a4-5e3e-ade4-c8116322ab35")]
internal sealed class VolumeNanoVdbCsgExample : Instance<VolumeNanoVdbCsgExample>
{
    [Output(Guid = "e77d6987-d6bc-5114-bf5a-03f5c6e6c466")]
    public readonly Slot<Texture2D> ColorBuffer = new();
}
