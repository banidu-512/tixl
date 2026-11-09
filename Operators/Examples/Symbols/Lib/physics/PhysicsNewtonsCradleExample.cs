namespace Examples.Lib.physics;

/// <summary>
/// A five-ball Newton's cradle: one ball is released and the opposite ball swings out.
/// </summary>
[Guid("248675b1-7feb-5ff1-9443-53cff7da76ca")]
internal sealed class PhysicsNewtonsCradleExample : Instance<PhysicsNewtonsCradleExample>
{
    [Output(Guid = "8220d84f-5a53-5343-95cf-c92c3344a923")]
    public readonly Slot<Texture2D> Output = new();
}
