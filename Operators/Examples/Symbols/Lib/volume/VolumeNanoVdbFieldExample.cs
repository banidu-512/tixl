using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.Lib.volume{
    /// <summary>
    /// A production .vdb raymarched through TiXL's field pipeline: [LoadVdb] reads the file, [VolumeToNanoVdb] flattens it for the GPU, [VdbToSdfField] exposes the grid as a signed-distance field and [RaymarchField] draws it. From here every field operator - CombineSDF, TransformField, SDFToColor - works on the volume.
    /// </summary>
    [Guid("c5b0fe28-b153-43f8-82ed-bc67d3815cf7")]
    internal sealed class VolumeNanoVdbFieldExample : Instance<VolumeNanoVdbFieldExample>
    {
        [Output(Guid = "f56c30a6-d464-4673-95c4-706598ce13f7")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


    }
}
