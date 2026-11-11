namespace Examples.Lib.volume;

/// <summary>
/// The debug view for NanoVDB: draws one wireframe box per node of the chosen tree level. If the boxes line up with the volume, the tree, bounds and transform were read correctly.
/// </summary>
[Guid("cdbf03ee-cc7d-51c6-a8a1-96862d858225")]
internal sealed class DrawVdbBoundsExample : Instance<DrawVdbBoundsExample>
{
    [Output(Guid = "6ef5928d-c7ca-5e8e-8895-070e0644dd75")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
