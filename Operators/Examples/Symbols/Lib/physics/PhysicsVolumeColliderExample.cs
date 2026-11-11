namespace Examples.Lib.physics;

/// <summary>
/// Balls rain on a torus that comes from a volume.
/// </summary>
[Guid("2a931f95-a85a-5412-a205-3c750bebb02e")]
internal sealed class PhysicsVolumeColliderExample : Instance<PhysicsVolumeColliderExample>
{
    [Output(Guid = "a3696592-b293-50d8-a1d9-39c85768a977")]
    public readonly Slot<Texture2D> Output = new();
}
