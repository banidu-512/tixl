namespace Examples.Lib.volume;

/// <summary>
/// Fire with smoke from a sparse volume: three stacked spheres union into a tapering plume, [VolumeNoise] sculpts it with turbulence, and [VolumeToPoints] scatters the surface into particles that two [DrawBillboards] layers draw - a small hot core and a wide grey haze around the same points.
/// </summary>
[Guid("361188e3-9476-5358-b69e-e61310c86696")]
internal sealed class VolumeFireSmokeExample : Instance<VolumeFireSmokeExample>
{
    [Output(Guid = "b36a0efb-e814-5024-b956-f24bda753caa")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
