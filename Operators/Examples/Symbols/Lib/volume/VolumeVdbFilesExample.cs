namespace Examples.Lib.volume;

/// <summary>
/// Loading production .vdb files: point [LoadVdb] at a .vdb file, inspect it with [VolumeInfo] and turn it into geometry with [VolumeToMesh]. The Path input accepts absolute paths or package addresses (the default uses Examples:volumes/bunny.vdb); GridName picks a grid when the file contains several.
/// </summary>
[Guid("b45dd17b-a505-5525-ad5e-5ce49e2a6059")]
internal sealed class VolumeVdbFilesExample : Instance<VolumeVdbFilesExample>
{
    [Output(Guid = "e4a20163-e888-5d61-9007-cd81318c3d05")]
    public readonly Slot<Texture2D> ColorBuffer = new();

}
