namespace Examples.Lib.physics;

/// <summary>
/// Three capsules and two cylinders dropped from the same height with rising bounciness, showing the collider shapes the other physics examples do not use.
/// </summary>
[Guid("aa6cc41a-29c5-4cd1-8648-ba18d656f237")]
internal sealed class PhysicsCapsulesAndCylindersExample : Instance<PhysicsCapsulesAndCylindersExample>
{
    [Output(Guid = "8f72b254-3ece-5952-8c77-7deaadf651c1")]
    public readonly Slot<Texture2D> Output = new();
}
