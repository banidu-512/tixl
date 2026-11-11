namespace Examples.Lib.volume;

/// <summary>
/// Samples a scalar field into a sparse volume. A noise field becomes a fog volume here, and [VolumeToMesh] extracts a blobby surface at the chosen density. Any field operator can be the source.
/// </summary>
[Guid("c36347c1-27db-5ec8-9145-066227b856d2")]
internal sealed class FieldToVolumeExample : Instance<FieldToVolumeExample>
{
    [Output(Guid = "d102fcf3-1b31-5881-88fa-ba72245080ec")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
