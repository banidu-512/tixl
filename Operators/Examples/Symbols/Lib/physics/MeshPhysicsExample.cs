namespace Examples.Lib.physics;

[Guid("1fdc5221-e9f8-4abc-980f-7a58bc3fd919")]
internal sealed class MeshPhysicsExample : Instance<MeshPhysicsExample>
{
    [Output(Guid = "f71dffd9-de57-4831-be25-821b8ef96a3c")]
    public readonly Slot<Texture2D> Output = new();
}
