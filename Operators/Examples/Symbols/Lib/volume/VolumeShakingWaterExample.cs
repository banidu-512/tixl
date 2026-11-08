namespace Examples.Lib.volume;

/// <summary>
/// Shaking water in an open box: a box minus a slightly smaller box gives a hollow, open-topped container. The water is a [VolumeNoise] displaced level set clipped to the cavity and drawn as a surface. Two [OscillateVec3] nodes do the shaking - one translates the container, one tilts the water with a slower phase so the liquid lags behind the box. They run on the idle-motion clock, so it animates without pressing Play; drag their Amplitude/Period inputs to change the shake.
/// </summary>
[Guid("37449751-31a8-5ead-9525-88759fdfd1b0")]
internal sealed class VolumeShakingWaterExample : Instance<VolumeShakingWaterExample>
{
    [Output(Guid = "e29b2d3c-8e83-55d0-a789-f484a390fffc")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
