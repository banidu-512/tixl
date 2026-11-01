#nullable enable
using Lib.Utils;
using libomtnet;
using T3.Core.Utils;

namespace Lib.io.video;

/// <summary>
/// Receives one OMT source and publishes it as a texture. Sources are found through OMT's DNS-SD discovery, so a
/// sender that starts after the operator was set up is picked up by the library on its own.
/// </summary>
[Guid("608acba9-f310-4876-981b-ade0664c5c66")]
[ExportDependencies("libomtnet.dll", "libvmx.dll", "libvmx.dylib", "libvmx.so")]
public sealed class OmtInput : Instance<OmtInput>, IStatusProvider, ICustomDropdownHolder
{
    [Output(Guid = "786f11e0-1e13-42f1-bcb0-1c07c8659be6", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> Texture = new();

    public OmtInput()
    {
        Texture.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        if (!OmtRuntime.IsAvailable)
        {
            Texture.Value = null;
            return;
        }

        var reconnectTriggered = MathUtils.WasTriggered(TriggerReconnect.GetValue(context), ref _reconnect);
        if (reconnectTriggered)
            TriggerReconnect.SetTypedInputValue(false);

        if (SourceName.DirtyFlag.IsDirty || reconnectTriggered)
            Reconnect(SourceName.GetValue(context));

        _receiver?.TryUpload(out _);
        Texture.Value = _receiver?.Texture;
    }

    private void Reconnect(string? sourceName)
    {
        _receiver?.Dispose();
        _receiver = null;

        if (string.IsNullOrEmpty(sourceName))
            return;

        _receiver = new OmtVideoReceiver(sourceName);
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing)
            return;

        Log.Debug("Disposing OmtInput...", this);
        _receiver?.Dispose();
        _receiver = null;
        Texture.Value = null;
    }

    #region status provider
    IStatusProvider.StatusLevel IStatusProvider.GetStatusLevel()
    {
        if (!OmtRuntime.IsAvailable)
            return IStatusProvider.StatusLevel.Error;

        var message = _receiver?.LastStatusMessage;
        return string.IsNullOrEmpty(message) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    }

    string? IStatusProvider.GetStatusMessage()
    {
        if (!OmtRuntime.IsAvailable)
            return OmtRuntime.MissingMessage;

        return _receiver != null
                   ? _receiver.LastStatusMessage
                   : PlaceholderText;
    }
    #endregion

    #region device dropdown
    private const string PlaceholderText = "Select OMT source...";

    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        var currentValue = SourceName.Value;

        if (string.IsNullOrEmpty(currentValue))
            return PlaceholderText;

        return OmtSources().Contains(currentValue) ? currentValue : PlaceholderText;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId != SourceName.Id)
        {
            yield return "undefined";
            yield break;
        }

        yield return PlaceholderText;

        foreach (var source in OmtSources())
            yield return source;
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected) || selected == PlaceholderText)
        {
            SourceName.SetTypedInputValue(string.Empty);
            Reconnect(null);
            return;
        }

        SourceName.SetTypedInputValue(selected!);
    }

    /// <summary>The sources OMT currently discovers on the network, as "MACHINE (Source Name)".</summary>
    private static string[] OmtSources()
    {
        try
        {
            return OMTDiscovery.GetInstance().GetAddresses();
        }
        catch (Exception e)
        {
            Log.Debug($"OMT source discovery failed: {e.Message}");
            return [];
        }
    }
    #endregion

    private OmtVideoReceiver? _receiver;
    private bool _reconnect;

    [Input(Guid = "6988eb41-8866-43b8-a6aa-124e413cecc4")]
    public readonly InputSlot<string> SourceName = new();

    [Input(Guid = "b1e6d486-3e33-4147-b03b-c30f8cd6caab")]
    public readonly InputSlot<bool> TriggerReconnect = new();
}
