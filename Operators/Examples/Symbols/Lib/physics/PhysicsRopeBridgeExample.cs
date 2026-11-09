namespace Examples.Lib.physics;

/// <summary>
/// A sagging plank bridge held by two kinematic anchors, with a crate dropped on it.
/// </summary>
[Guid("0b533efb-952f-5173-ac30-a5bc40fa5da6")]
internal sealed class PhysicsRopeBridgeExample : Instance<PhysicsRopeBridgeExample>
{
    [Output(Guid = "a0a7c7ea-9ccf-5311-bc30-ff789e2c029b")]
    public readonly Slot<Texture2D> Output = new();
}
