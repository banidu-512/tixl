namespace Examples.Lib.volume;

/// <summary>
/// Plays a pre-converted NanoVDB cache. [LoadNanoVdb] uploads the .nvdb file straight to a GPU buffer with no CPU grid and no rebuild, and [NanoVdbRender] raymarches it. This is the path for large animated caches.
/// </summary>
[Guid("790d64f6-d3ee-560d-9f3e-b8f890ba5bed")]
internal sealed class LoadNanoVdbExample : Instance<LoadNanoVdbExample>
{
    [Output(Guid = "2fa84f7b-2057-5e04-897c-f090b087750c")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
