namespace Examples.Lib.physics;

/// <summary>
/// A wall of 80 bricks from one RigidBody, knocked down by a heavy block.
/// </summary>
[Guid("ee164a1a-32c0-5adf-b341-fe20e94f7608")]
internal sealed class PhysicsBrickWallExample : Instance<PhysicsBrickWallExample>
{
    [Output(Guid = "02355177-737b-509e-a63d-84ce4908245e")]
    public readonly Slot<Texture2D> Output = new();
}
