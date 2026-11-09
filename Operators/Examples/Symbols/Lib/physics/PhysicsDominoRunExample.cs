namespace Examples.Lib.physics;

/// <summary>
/// A row of fourteen dominoes toppled by the first one.
/// </summary>
[Guid("299a07d5-54ef-570a-897b-50c617da3b7d")]
internal sealed class PhysicsDominoRunExample : Instance<PhysicsDominoRunExample>
{
    [Output(Guid = "7e1d8084-aac9-5250-864d-a0bb11a7d7fc")]
    public readonly Slot<Texture2D> Output = new();
}
