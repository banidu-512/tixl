namespace Examples.Lib.volume;

/// <summary>
/// Exports a NanoVDB file from a generated volume while previewing the same data on the GPU.
/// </summary>
[Guid("65643c65-e4d9-524d-98ea-7ee6b4257cbc")]
internal sealed class VolumeNanoVdbSaveExample : Instance<VolumeNanoVdbSaveExample>
{
    [Output(Guid = "4a5585b1-54d8-5365-a7fd-49b203f7b4d9")]
    public readonly Slot<Texture2D> ColorBuffer = new();
}
