namespace Examples.Lib.volume;

/// <summary>
/// Arithmetic between two volumes: a sphere with a box subtracted, then meshed. UseConstant switches B for a single number; Min and Max behave as union and intersection on level sets.
/// </summary>
[Guid("c7bf565f-3eaf-5e17-a50a-9ffd5bbecc34")]
internal sealed class VolumeMathExample : Instance<VolumeMathExample>
{
    [Output(Guid = "06e10603-c87c-5860-80a3-5e6f71a34d34")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
