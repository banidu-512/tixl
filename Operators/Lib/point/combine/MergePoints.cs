namespace Lib.point.combine;

[Guid("7cbb9cab-fa32-46d6-afca-5c198f34bd67")]
internal sealed class MergePoints : Instance<MergePoints>
{

    [Output(Guid = "085fdf4a-1bab-496a-b8f0-afb88a0ab511")]
    public readonly Slot<BufferWithViews> OutBuffer = new();

        [Input(Guid = "4a8e200c-32c0-4c5a-b56d-ddc2994e7b99")]
        public readonly MultiInputSlot<T3.Core.DataTypes.BufferWithViews> Input = new MultiInputSlot<T3.Core.DataTypes.BufferWithViews>();

        
    private enum BlendModes
    {
        Mix,
        UseA_F1,
        UseB_F1,
        MixWithSlidingRange,
        MixWithSlidingRangeSmooth,
    }
        
    private enum PairingModes
    {
        WrapAround,
        Adjust,
    }
        
}