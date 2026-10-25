#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Interfaces;
using T3.Core.Operator.Slots;
using T3.Core.Output;
using T3.Core.Output.Rendering;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.Windows.OutputSetup;

/// <summary>
/// Streams the setup's laser outputs to their bound devices — the output-setup counterpart of
/// UpdatePresentation, and the replacement for the former graph-side output operators
/// ([EtherDreamOutput]/[PONKOutput]). Every pump interval each laser output's routed camera is
/// evaluated (or peeked when the camera already ran this frame), run through the output's
/// processing pipeline (optimizer + safety), and handed to the <see cref="LaserOutputStream"/>
/// of the laser plug the output is bound to on this machine.
///
/// Runs on the render thread, whatever view the editor shows — leaving the graph view must never
/// stop the show. The <see cref="LaserDriverEnabled"/> kill-switch silences the whole driver for
/// profiling.
/// </summary>
internal static class LaserDriver
{
    /// <summary>
    /// Pumps every laser output: camera frame in, pipeline through, device stream fed. Called from
    /// the frame loop (both the normal path and the minimized keep-alive path).
    /// </summary>
    public static void Update()
    {
        // The root instance, not the focused view: the setup's cameras live at the home level, and
        // navigating into a child composition must never stop the show from streaming.
        var composition = ProjectView.Focused?.RootInstance;
        var setup = ActiveSetup.Current;
        var machine = ActiveSetup.Machine;
        if (composition == null || setup == null || machine == null)
        {
            DisposeAllStreams();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastPump < EffectivePumpInterval)
            return;

        _lastPump = now;
        var pullStart = now;
        OutputContentResolver.PrepareContext(default);
        var context = OutputContentResolver.Context;

        var livePlugIds = new HashSet<Guid>();
        foreach (var output in setup.Outputs)
        {
            if (output.Kind != OutputDefinition.Kinds.Laser)
                continue;

            // One warm stream per bound plug - created even while the output is muted so the
            // connection stays up. A laser output may bind several plugs: the same frame fans
            // out to each device (two scanners, one show).
            LaserPoint[]? frame = null;
            var frameComputed = false;
            foreach (var binding in machine.FindLaserBindings(output.Id))
            {
                var plug = machine.FindLaserPlug(binding.PlugId);
                if (plug == null)
                    continue;

                livePlugIds.Add(plug.Id);
                if (!_streams.TryGetValue(plug.Id, out var stream))
                {
                    var created = LaserOutputStream.Create(plug);
                    if (created == null)
                        continue;

                    _streams[plug.Id] = created;
                    stream = created;
                }

                if (!output.IsSending)
                    continue;

                if (!frameComputed)
                {
                    frame = ComputeFrame(output, context);
                    _frameCache[output.Id] = (Stopwatch.GetTimestamp(), frame);
                    frameComputed = true;
                }

                if (frame is { Length: > 0 })
                {
                    // The stream re-applies the safety stage at the door - pass the output's current
                    // config so edited limits take effect on the next frame without recreating the stream.
                    var processing = output.LaserProcessing ?? LaserProcessingSettings.Default;
                    stream.SubmitFrame(frame, processing.Safety.ToConfig());
                }
            }
        }

        // A stream whose plug (or its binding, or the output) vanished stops its threads — the
        // same lifetime the operator's Dispose had, just driven by the machine config instead of
        // the graph.
        foreach (var deadPlugId in _streams.Keys.Where(id => !livePlugIds.Contains(id)).ToList())
        {
            _streams[deadPlugId].Dispose();
            _streams.Remove(deadPlugId);
        }

        // The pump runs on the render thread and shares the frame budget with everything else.
        // A pull that came in expensive (heavy scene re-evaluation, GPU readback backlog) means
        // the next one would likely blow the 16ms budget too — back off for a few pump intervals.
        // The streams loop their last frame meanwhile, so the DAC never starves.
        var pullCost = Stopwatch.GetTimestamp() - pullStart;
        _pumpBackoff = pullCost > ExpensivePullThreshold ? Math.Min(_pumpBackoff + 1, 3) : 0;
    }

    /// <summary>
    /// The frame this output streams right now: the pipeline result of the camera routed to it
    /// by OutputRef — or, while uncalibrated, the alignment rectangle (input ignored). Empty when
    /// no camera is routed and safety is calibrated. Backs the Board card and parameter previews:
    /// the pump refreshes the cache every interval with the frame it just submitted, so previews
    /// show the streamed truth without ever re-evaluating the camera. Only when the driver is not
    /// pumping (kill-switch off) does the preview compute on demand.
    /// </summary>
    public static LaserPoint[] GetFrame(OutputDefinition output)
    {
        var now = Stopwatch.GetTimestamp();
        if (_frameCache.TryGetValue(output.Id, out var cached) && now - cached.Timestamp < PreviewStaleAfter)
            return cached.Points;

        _frameCache[output.Id] = cached = (now, ComputeFrame(output, null));
        return cached.Points;
    }

    /// <summary>The live status of the stream serving this output — connection, buffer, watchdog.</summary>
    public static bool TryGetStreamStatus(Guid outputId, out string message, out IStatusProvider.StatusLevel level)
    {
        message = string.Empty;
        level = IStatusProvider.StatusLevel.Undefined;

        var machine = ActiveSetup.Machine;
        if (machine == null)
            return false;

        var binding = machine.FindBinding(outputId);
        if (binding is not { IsLaser: true })
            return false;

        if (!_streams.TryGetValue(binding.PlugId, out var stream))
            return false;

        message = stream.StatusMessage;
        level = stream.StatusLevel;
        return true;
    }

    /// <summary>
    /// The camera's frame through the output's pipeline. With a context supplied (the driver's
    /// pump) the routed camera is evaluated into it; with null (a preview outside the pump -
    /// driver kill-switch off) a fresh context is prepared so time-animated content keeps
    /// moving. One evaluation per camera per pump interval: two outputs routed to the same
    /// camera must not run the scene (and its GPU readbacks) twice.
    /// </summary>
    private static LaserPoint[] ComputeFrame(OutputDefinition output, EvaluationContext? pumpContext)
    {
        // Test screen: the alignment rectangle with the output's name, regardless of the Calibrated
        // gate - it is the aiming aid used BEFORE calibration. The stream door still applies the
        // safety config, so the safe zone clamps it like anything else.
        if (_testScreenOutputs.Contains(output.Id))
        {
            var testSettings = output.LaserProcessing ?? LaserProcessingSettings.Default;
            LaserPipeline.RectFromSafety(testSettings.Safety, out var cx, out var cy, out var hw, out var hh);
            return LaserTestFrame.Build(output.Name, cx, cy, hw, hh);
        }

        StructuredList? list = null;
        foreach (var camera in LaserContent.Cameras)
        {
            if (camera is not Instance instance || LaserContent.OutputRefOf(instance) != output.Id)
                continue;

            var pullTimestamp = Stopwatch.GetTimestamp();
            if (_cameraPulls.TryGetValue(instance.SymbolChildId, out var pulled)
                && pullTimestamp - pulled.Pump < EffectivePumpInterval)
            {
                // Already evaluated within this pump interval - another output shares the camera.
                list = pulled.List;
            }
            else
            {
                if (pumpContext == null)
                {
                    // The shared evaluation context, reset for this pull: in setup mode with no
                    // video composite rendering, nobody else prepares it, and a stale context freezes
                    // time-animated content (the camera's roll animator, oscillators).
                    OutputContentResolver.PrepareContext(default);
                    list = ((ILaserContentSupplier)instance).GetLaserPoints(OutputContentResolver.Context);
                }
                else
                {
                    list = ((ILaserContentSupplier)instance).GetLaserPoints(pumpContext);
                }

                _cameraPulls[instance.SymbolChildId] = (pullTimestamp, list);
            }

            break;
        }

        LaserPoint[]? input = null;
        var count = 0;
        if (list is StructuredList<LaserPoint> typed && typed.TypedElements != null && typed.NumElements > 0)
        {
            input = typed.TypedElements;
            count = typed.NumElements;
        }

        if (!_pipelineSessions.TryGetValue(output.Id, out var session))
            _pipelineSessions[output.Id] = session = new LaserPipeline.Session();

        var settings = output.LaserProcessing ?? LaserProcessingSettings.Default;
        return session.Apply(settings, input, count, out _);
    }

    /// <summary>Pump rate for the streams. 30 Hz is ILDA-show fps; the streams loop the last
    /// frame in between so the device is fed at scan rate regardless.</summary>
    private static readonly long PumpInterval = Stopwatch.Frequency / 30;

    /// <summary>Pulls costlier than this (in stopwatch ticks ≈ 8ms) stretch the next interval.</summary>
    private static readonly long ExpensivePullThreshold = Stopwatch.Frequency * 8 / 1000;

    private static int _pumpBackoff;

    private static long EffectivePumpInterval => PumpInterval * (1 + _pumpBackoff);

    private static long _lastPump;

    /// <summary>Device streams per laser plug id; created lazily, disposed when the plug's binding goes away.</summary>
    private static readonly Dictionary<Guid, LaserOutputStream> _streams = [];

    /// <summary>Pipeline scratch per output; render-thread only, like everything that reads it.</summary>
    private static readonly Dictionary<Guid, LaserPipeline.Session> _pipelineSessions = [];

    /// <summary>The frame the pump just computed and submitted per output - what the previews show.</summary>
    private static readonly Dictionary<Guid, (long Timestamp, LaserPoint[] Points)> _frameCache = [];

    /// <summary>How long a pump-cached frame backs the previews before an on-demand pull (only while the driver is off).</summary>
    private static readonly long PreviewStaleAfter = Stopwatch.Frequency / 4; // 250 ms

    /// <summary>One camera evaluation per pump interval, keyed by symbol-child id - shared by outputs routed to the same camera.</summary>
    private static readonly Dictionary<Guid, (long Pump, StructuredList? List)> _cameraPulls = [];

    /// <summary>Outputs currently streaming the test screen (rectangle + output name); transient show state, never saved.</summary>
    private static readonly HashSet<Guid> _testScreenOutputs = [];

    public static bool IsTestScreen(Guid outputId) => _testScreenOutputs.Contains(outputId);

    public static void SetTestScreen(Guid outputId, bool on)
    {
        if (on)
            _testScreenOutputs.Add(outputId);
        else
            _testScreenOutputs.Remove(outputId);
    }

    /// <summary>Live per-output state for the debug protocol: what the pump is doing, what the
    /// streams report, and how big the last frame was - the remote view of the show.</summary>
    public static IEnumerable<object> GetDiagnostics(Setup setup, MachineConfig machine)
    {
        foreach (var output in setup.Outputs)
        {
            if (output.Kind != OutputDefinition.Kinds.Laser)
                continue;

            var bindings = new List<object>();
            foreach (var binding in machine.FindLaserBindings(output.Id))
            {
                var plug = machine.FindLaserPlug(binding.PlugId);
                if (plug == null)
                    continue;

                var hasStatus = _streams.TryGetValue(plug.Id, out var stream);
                bindings.Add(new
                                 {
                                     plug = plug.Name,
                                     kind = plug.Kind,
                                     connected = hasStatus && stream.IsConnected,
                                     status = hasStatus ? stream.StatusMessage : "no stream",
                                 });
            }

            _frameCache.TryGetValue(output.Id, out var cached);
            yield return new
                             {
                                 name = output.Name,
                                 sending = output.IsSending,
                                 testScreen = _testScreenOutputs.Contains(output.Id),
                                 calibrated = output.LaserProcessing?.Safety.Calibrated ?? false,
                                 framePoints = cached.Points?.Length ?? 0,
                                 bindings,
                             };
        }
    }

    private static void DisposeAllStreams()
    {
        if (_streams.Count == 0)
            return;

        foreach (var stream in _streams.Values)
            stream.Dispose();

        _streams.Clear();
    }
}
