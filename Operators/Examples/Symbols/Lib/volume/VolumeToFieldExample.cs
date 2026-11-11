namespace Examples.Lib.volume;

/// <summary>
/// Wraps a volume as a CPU scalar field and samples it back into a volume with [FieldToVolume]. The roundtrip is the bridge that lets field operators and volume operators see the same data.
/// </summary>
[Guid("0d3845e3-f9db-5ae4-88a6-b829e1ada0db")]
internal sealed class VolumeToFieldExample : Instance<VolumeToFieldExample>
{
    [Output(Guid = "ee6d45af-2813-52de-ae46-1253b5a13359")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
