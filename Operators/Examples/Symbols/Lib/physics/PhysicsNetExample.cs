namespace Examples.Lib.physics;

/// <summary>
/// A net of 196 knots, linked to their neighbours, falls over a sphere.
/// </summary>
[Guid("0f11509d-c28e-57c5-92c7-239dd2839283")]
internal sealed class PhysicsNetExample : Instance<PhysicsNetExample>
{
    [Output(Guid = "a317d219-6efd-593b-939f-e483889ea7bf")]
    public readonly Slot<Texture2D> Output = new();
}
