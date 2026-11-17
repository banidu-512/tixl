namespace Examples.Lib.physics;

/// <summary>
/// A cube is cut into Voronoi cells and dropped: every cell is its own convex body.
/// </summary>
[Guid("e9035c72-0fb4-52af-890e-d4096fcec9da")]
internal sealed class PhysicsFractureExample : Instance<PhysicsFractureExample>
{
    [Output(Guid = "8ff9421d-ca53-5567-a0f2-501e095c023f")]
    public readonly Slot<Texture2D> Output = new();
}
