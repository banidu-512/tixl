namespace Examples.Lib.physics;

/// <summary>
/// A motorised paddle on a fixed hub sweeps 80 balls off its circle.
/// </summary>
[Guid("c3c534be-fb7c-503f-a4fe-b929b5b859e1")]
internal sealed class PhysicsMotorExample : Instance<PhysicsMotorExample>
{
    [Output(Guid = "565a5c7d-d574-5c7b-8a00-344f66bef7d6")]
    public readonly Slot<Texture2D> Output = new();
}
