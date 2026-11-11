namespace Examples.Lib.physics;

/// <summary>
/// A chassis on four motorised wheels drives into a stack of boxes.
/// </summary>
[Guid("6c7e14dd-d79e-5655-b36a-79947562024e")]
internal sealed class PhysicsCarExample : Instance<PhysicsCarExample>
{
    [Output(Guid = "616deba8-41c3-5637-a850-8ff113d14aff")]
    public readonly Slot<Texture2D> Output = new();
}
