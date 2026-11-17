namespace Examples.Lib.physics;

/// <summary>
/// Six ragdolls fall onto a sphere. Each is ten bodies held by nine limited ball sockets.
/// </summary>
[Guid("6010f3f0-c2cf-5f9e-a0ee-90c30aa2a0ff")]
internal sealed class PhysicsRagdollExample : Instance<PhysicsRagdollExample>
{
    [Output(Guid = "92d26d24-c3c8-5434-96bd-cfdf1ea8cc5f")]
    public readonly Slot<Texture2D> Output = new();
}
