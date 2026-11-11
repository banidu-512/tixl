namespace Examples.Lib.physics;

/// <summary>
/// A rope of 24 links from a line of points, hung by its first link.
/// </summary>
[Guid("c1815df0-b2e8-578b-b0ef-c5dc3cbfaca1")]
internal sealed class PhysicsRopeExample : Instance<PhysicsRopeExample>
{
    [Output(Guid = "da4d72a3-28ee-55a1-823d-7f2acac9fa10")]
    public readonly Slot<Texture2D> Output = new();
}
