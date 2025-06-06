namespace Examples.user.wake.revision2021;

[Guid("18d9721b-e170-4f4d-b630-30445aba5e20")]
 internal sealed class FadingFacesRev2021 : Instance<FadingFacesRev2021>
{

    [Input(Guid = "71eb23a5-dde7-42bd-916a-5278343b64ad")]
    public readonly InputSlot<float> OverrideTime = new();

    [Output(Guid = "29a0bc9a-4c33-4777-aa73-b2c7074a89fa")]
    public readonly TimeClipSlot<Command> Output2 = new();

    [Output(Guid = "fc1d05ff-7395-4408-a025-4f3d4247a2ea")]
    public readonly Slot<Texture2D> Output3 = new();


}