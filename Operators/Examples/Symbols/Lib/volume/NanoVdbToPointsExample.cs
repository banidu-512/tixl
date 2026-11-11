namespace Examples.Lib.volume;

/// <summary>
/// Turns the active voxels of a NanoVDB grid into points on the GPU. [NanoVdbToPoints] dispatches one thread group per leaf from the buffer [VolumeToNanoVdb] produced, so it scales with the grid instead of reading it back to the CPU.
/// </summary>
[Guid("1f5f83dc-8301-520f-834e-e8b0613b0a35")]
internal sealed class NanoVdbToPointsExample : Instance<NanoVdbToPointsExample>
{
    [Output(Guid = "8b207621-52d3-52cf-aeb5-7574c875d6f3")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
