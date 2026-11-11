namespace Examples.Lib.physics;

/// <summary>
/// Three forces turn 300 boxes into a whirlwind.
/// </summary>
[Guid("a1bb63ab-c77e-5a2d-8ae4-8bb88982e506")]
internal sealed class PhysicsVortexExample : Instance<PhysicsVortexExample>
{
    [Output(Guid = "711a0abd-d6a7-5739-a068-4e227afd58ae")]
    public readonly Slot<Texture2D> Output = new();
}
