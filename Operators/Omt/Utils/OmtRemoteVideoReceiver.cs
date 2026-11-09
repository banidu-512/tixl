#nullable enable
using T3.Core.Video;

namespace Lib.Utils;

/// <summary>Adapts <see cref="OmtVideoReceiver"/> to the remote-video input contract used by boygrouping.</summary>
internal sealed class OmtRemoteVideoReceiver : IRemoteVideoInputReceiver
{
    public OmtRemoteVideoReceiver(string sourceName)
    {
        _receiver = new OmtVideoReceiver(sourceName);
    }

    public Texture2D? Update() => _receiver.TryUpload(out var texture) ? texture : null;

    public bool IsConnected => _receiver.IsConnected;

    public string? StatusMessage => _receiver.LastStatusMessage;

    public void Dispose() => _receiver.Dispose();

    private readonly OmtVideoReceiver _receiver;
}
