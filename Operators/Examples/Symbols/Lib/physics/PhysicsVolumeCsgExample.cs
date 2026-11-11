namespace Examples.Lib.physics;

/// <summary>
/// A bowl is carved out of a block with volume CSG, and the balls collect in it.
/// </summary>
[Guid("4d81d2fc-2dda-5528-8e9a-8417c8b0ca79")]
internal sealed class PhysicsVolumeCsgExample : Instance<PhysicsVolumeCsgExample>
{
    [Output(Guid = "c46e59a8-52dc-598b-a9fd-7b049e0c07ce")]
    public readonly Slot<Texture2D> Output = new();
}
