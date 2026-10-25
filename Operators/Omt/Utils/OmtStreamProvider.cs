#nullable enable
using T3.Core.Output.Streaming;
using T3.Core.Video;

namespace Lib.Utils;

/// <summary>
/// Offers OMT senders to the output setup and OMT receivers to boygrouping's remote video inputs;
/// discovered by type when this package loads.
/// </summary>
/// <remarks>Declares the codec so an installation that only streams, with no OMT operator in its graph, still
/// ships it with an exported player.</remarks>
[ExportDependencies("libomtnet.dll", "libvmx.dll", "libvmx.dylib", "libvmx.so")]
internal sealed class OmtStreamProvider : IOutputStreamProvider, IRemoteVideoInputProvider
{
    public string Kind => "OMT";

    public OutputStreamOptions Supported => OutputStreamOptions.FrameRate | OutputStreamOptions.Alpha;

    public IOutputStreamSender CreateSender(string name) => new OmtSender(name);

    public bool IsAvailable => OmtRuntime.IsAvailable;

    public string? UnavailableMessage => OmtRuntime.IsAvailable ? null : OmtRuntime.MissingMessage;

    public IRemoteVideoInputReceiver? CreateReceiver(string sourceName) => new OmtRemoteVideoReceiver(sourceName);
}
