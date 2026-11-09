namespace Examples.Lib.physics;

/// <summary>
/// Adjacent balls welded into rigid pairs, so each pair tumbles and lands as one assembly instead of scattering.
/// </summary>
[Guid("d4d3c22e-754e-41f3-9eaf-c90c94309cc8")]
internal sealed class PhysicsWeldedPairsExample : Instance<PhysicsWeldedPairsExample>
{
    [Output(Guid = "8f72b254-3ece-5952-8c77-7deaadf651c1")]
    public readonly Slot<Texture2D> Output = new();
}
