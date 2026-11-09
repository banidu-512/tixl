namespace Examples.Lib.physics;

/// <summary>
/// Five balls dropped from the same height with Bounciness from 0 to 1.
/// </summary>
[Guid("83a94bb9-64ef-5d72-93c9-2e8795126b04")]
internal sealed class PhysicsBouncingBallsExample : Instance<PhysicsBouncingBallsExample>
{
    [Output(Guid = "8f72b254-3ece-5952-8c77-7deaadf651c1")]
    public readonly Slot<Texture2D> Output = new();
}
