namespace Examples.Lib.physics;

/// <summary>
/// 300 balls rain on the bunny loaded from a .vdb file.
/// </summary>
[Guid("ecc715a2-4339-56c5-9d11-1fad0ce39817")]
internal sealed class PhysicsVdbColliderExample : Instance<PhysicsVdbColliderExample>
{
    [Output(Guid = "5c1cd322-bc7d-597f-9d16-ca205b401cb8")]
    public readonly Slot<Texture2D> Output = new();
}
