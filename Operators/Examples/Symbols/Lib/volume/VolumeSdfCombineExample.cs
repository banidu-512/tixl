namespace Examples.Lib.volume;

/// <summary>
/// A volume and a procedural sphere merged as SDF fields and raymarched as one shape.
/// </summary>
[Guid("a2ff6e7a-3dd4-561a-9a64-4390ee26576d")]
internal sealed class VolumeSdfCombineExample : Instance<VolumeSdfCombineExample>
{
    [Output(Guid = "2bc0530e-9a39-5f0d-833a-3d5ab997049f")]
    public readonly Slot<Texture2D> Output = new();
}
