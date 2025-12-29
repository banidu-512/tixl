namespace Skills.Image.I01_BasicShapes;

[Guid("98046598-4b25-44d3-95ef-268226a09038")]
internal sealed class I01c_Kalaidos : Instance<I01c_Kalaidos>
{
    [Output(Guid = "1cc22e61-00a6-4852-aa58-8bec78140790")]
    public readonly Slot<Texture2D> ColorBuffer = new();


}