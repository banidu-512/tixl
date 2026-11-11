namespace Examples.Lib.physics;

/// <summary>
/// Bouncing balls; every impact flashes at the point where it happens.
/// </summary>
[Guid("d73c1700-6d8b-5e84-93e3-e4d60c17518d")]
internal sealed class PhysicsContactsExample : Instance<PhysicsContactsExample>
{
    [Output(Guid = "9f7f71f8-7201-5bba-b182-901f6cd2e20a")]
    public readonly Slot<Texture2D> Output = new();
}
