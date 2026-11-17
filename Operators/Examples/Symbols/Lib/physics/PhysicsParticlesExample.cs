namespace Examples.Lib.physics;

[Guid("f1632cf1-8515-412c-b645-7897556edce8")]
internal sealed class PhysicsParticlesExample : Instance<PhysicsParticlesExample>
{
    [Output(Guid = "5a69fb54-9ca6-4f37-a236-bc82a4db7dd4")]
    public readonly Slot<Texture2D> Output = new();
}
