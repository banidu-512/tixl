namespace Examples.Lib.volume;

/// <summary>
/// The field path: [VolumeToTexture3D] bakes the sparse grid into a dense 3D texture, and [VolumeField] exposes it as a shader node that [RaymarchField] renders. Use this when you want the volume in TiXL's existing raymarching pipeline instead of the NanoVDB buffer.
/// </summary>
[Guid("6f4c9f44-e6f0-5832-945c-61a43a9b2458")]
internal sealed class VolumeToTexture3DExample : Instance<VolumeToTexture3DExample>
{
    [Output(Guid = "bdbf557e-2a60-5cdf-8bfc-e72d31e27934")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
