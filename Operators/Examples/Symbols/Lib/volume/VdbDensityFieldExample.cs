namespace Examples.Lib.volume;

/// <summary>
/// A fog volume as a field: [VdbDensityField] returns IsoValue minus the sampled density, so dense smoke reads as inside. Here the shipped smoke.vdb is raymarched through [RaymarchField].
/// </summary>
[Guid("0454aceb-0ea0-5588-b37f-7b774f931a8a")]
internal sealed class VdbDensityFieldExample : Instance<VdbDensityFieldExample>
{
    [Output(Guid = "cefd0321-8bf4-5f7a-9a08-718ca69cfb2d")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
