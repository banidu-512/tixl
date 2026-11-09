using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Examples.Lib.io.ai;

[Guid("d9057671-ba90-47c5-bfaa-c9c53b3abf90")]
internal sealed class StreamDiffusionExampleImg2ImgRestyle : Instance<StreamDiffusionExampleImg2ImgRestyle>
{
    [Output(Guid = "574a52b9-8081-4668-a42b-1bbf7f83afc0")]
    public readonly Slot<Texture2D> Image = new();

    [Input(Guid = "0872f2fa-fc10-427c-96e3-f2cccf62e804")]
    public readonly InputSlot<string> ModelPath = new();

    [Input(Guid = "b9aa84fb-f9e3-494f-b0aa-483fc3ae4e31")]
    public readonly InputSlot<string> Prompt = new();

    [Input(Guid = "4c87d916-bc35-43bc-a785-7f239a686d3b")]
    public readonly InputSlot<string> ImagePath = new();

}
