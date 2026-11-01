namespace Examples.Lib.physics;

/// <summary>
/// A heavy crate drops onto a seesaw and launches the crate on its other end.
/// </summary>
[Guid("a31926dd-defb-5980-9e21-f16e77cd0a56")]
internal sealed class PhysicsSeesawExample : Instance<PhysicsSeesawExample>
{
    [Output(Guid = "e200945b-35bc-554b-84ea-0969b756b5a6")]
    public readonly Slot<Texture2D> Output = new();
}
