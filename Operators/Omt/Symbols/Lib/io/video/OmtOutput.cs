#nullable enable
using Lib.Utils;

namespace Lib.io.video;

[Guid("957ef10e-b410-4eb6-8c2a-b813e8df3a3e")]
[ExportDependencies("libomtnet.dll", "libvmx.dll", "libvmx.dylib", "libvmx.so")]
public sealed class OmtOutput : Instance<OmtOutput>, IStatusProvider
{
    [Output(Guid = "cade9b2b-2a82-4028-859e-813f0a763c39", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Texture2D?> TextureOutput = new();

    [Output(Guid = "fc06d936-aa9d-4e70-af49-acf151cbf3ca")]
    public readonly Slot<string?> SourceAddress = new();

    public OmtOutput()
    {
        TextureOutput.UpdateAction = Update;
        SourceAddress.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var texture = Texture.GetValue(context);
        var senderName = SenderName.GetValue(context) ?? "TiXL";

        TextureOutput.Value = texture;

        if (texture == null)
        {
            _lastErrorMessage = "No texture input";
            SourceAddress.Value = _sender?.AdvertisedAddress;
            return;
        }

        _sender ??= new OmtSender(senderName);
        _sender.Rename(senderName);
        _sender.FrameRate = FrameRate.GetValue(context);
        _sender.EnableAlpha = EnableAlpha.GetValue(context);
        _sender.Send(texture);
        _lastErrorMessage = _sender.LastError;
        SourceAddress.Value = _sender.AdvertisedAddress;
        SenderName.Update(context);
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing)
            return;

        _sender?.Dispose();
        _sender = null;
    }

    IStatusProvider.StatusLevel IStatusProvider.GetStatusLevel() =>
        string.IsNullOrEmpty(_lastErrorMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Error;

    string? IStatusProvider.GetStatusMessage() => _lastErrorMessage;

    private OmtSender? _sender;
    private string? _lastErrorMessage;

    [Input(Guid = "a16c8835-4298-43c0-b240-9ab428f6dba9")]
    public readonly InputSlot<Texture2D> Texture = new();

    [Input(Guid = "852d49b4-a3d9-48b0-a855-d41383f7f95e")]
    public readonly InputSlot<string> SenderName = new();

    [Input(Guid = "98e9190e-9cde-45da-bffc-e3c97a528be3")]
    public readonly InputSlot<int> FrameRate = new();

    [Input(Guid = "e7bbb443-b97e-4446-acb8-10648d65237a")]
    public readonly InputSlot<bool> EnableAlpha = new();
}
