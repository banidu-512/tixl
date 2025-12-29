namespace Skills.Image.I01_BasicShapes;

[Guid("e2f54c8c-2507-4b55-bc94-22a242ddf0ff")]
internal sealed class I01b_Swimming : Instance<I01b_Swimming>
{
    [Output(Guid = "4948c028-db5e-4e38-8f8d-a3b114bfeaa1")]
    public readonly Slot<Texture2D> ColorBuffer = new();


}