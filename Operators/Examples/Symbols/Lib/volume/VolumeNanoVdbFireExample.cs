namespace Examples.Lib.volume;

/// <summary>
/// A flame rendered from a NanoVDB level set.
/// </summary>
[Guid("a939eda2-b435-590f-824b-85ca030dc2cd")]
internal sealed class VolumeNanoVdbFireExample : Instance<VolumeNanoVdbFireExample>
{
    [Output(Guid = "a53726cd-4ed6-5320-832e-be767323e7a7")]
    public readonly Slot<Texture2D> ColorBuffer = new();
}
