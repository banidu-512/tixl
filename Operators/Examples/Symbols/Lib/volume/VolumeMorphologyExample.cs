namespace Examples.Lib.volume;

/// <summary>
/// Offset-based sculpting: [VolumeMorphology] grows and shrinks level sets in world units. Open and Close are the classic closing/opening operators - they fill concave crevices or shave convex spikes. Compare Dilate, Erode, Open and Close side by side.
/// </summary>
[Guid("0148a58c-2d83-5cd2-bb39-1b54fc2d6791")]
internal sealed class VolumeMorphologyExample : Instance<VolumeMorphologyExample>
{
    [Output(Guid = "27758cf4-281d-53bb-a386-9366b4a7b8e1")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
