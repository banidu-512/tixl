#nullable enable
using System.IO;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Core.Animation;
using T3.Core.Logging;
using T3.Core.Output;
using T3.Editor.Collab;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows;
using T3.Editor.Gui.Windows.Output;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Show;

/// <summary>
/// Failover is a 1:1 copy of the higher-priority machine: the standby shows what the active one shows. The
/// active sends its output setup and machine bindings, its window layout and the state of its output windows
/// (pinned outputs and view settings) over the failover collaboration session; the standby applies them, so
/// a takeover continues with the same outputs, streams and window arrangement. The composition itself
/// already follows through the collab sync.
/// </summary>
internal static class FailoverMirror
{
    public const string MessageType = "failoverMirror";
    public const string TimeMessageType = "failoverTime";

    /// <summary>Active side, called every frame: sends the mirror when it changed, and periodically for late joiners.</summary>
    public static void Tick(CollabManager collab)
    {
        var now = Environment.TickCount64;
        if (now < _nextCheckTicks)
            return;

        _nextCheckTicks = now + CheckIntervalMs;

        var peerCount = collab.Peers.Count;
        if (peerCount == 0)
        {
            _lastSentJson = null;
            return;
        }

        var message = BuildMessage(collab);
        if (message == null)
            return;

        var json = message.ToString(Formatting.None);
        var peersChanged = peerCount != _lastPeerCount;
        if (json == _lastSentJson && !peersChanged && now < _resendAtTicks)
            return;

        _lastSentJson = json;
        _lastPeerCount = peerCount;
        _resendAtTicks = now + ResendIntervalMs;
        collab.BroadcastJson(message);
    }

    /// <summary>Active side, called every frame: sends the playback clock four times a second.</summary>
    public static void TickTime(CollabManager collab)
    {
        var now = Environment.TickCount64;
        if (now < _nextTimeSendTicks || collab.Peers.Count == 0)
            return;

        _nextTimeSendTicks = now + TimeIntervalMs;
        var playback = Playback.Current;
        if (playback == null)
            return;

        collab.BroadcastJson(new
                                 {
                                     t = TimeMessageType,
                                     bars = playback.TimeInBars,
                                     fx = playback.FxTimeInBars,
                                     idle = UserSettings.Config.EnableIdleMotion,
                                     speed = playback.PlaybackSpeed,
                                     bpm = playback.Bpm,
                                 });
    }

    /// <summary>Standby side: follows the active machine's playback clock; the small drift between messages is not corrected.</summary>
    public static void ApplyTime(JObject message, CollabManager collab)
    {
        if (!T3.Core.Show.FailoverArbiter.IsEnabled || T3.Core.Show.FailoverArbiter.Role != T3.Core.Show.FailoverArbiter.NodeRole.Standby)
            return;

        var playback = Playback.Current;
        if (playback == null)
            return;

        var bars = message.Value<double>("bars");
        var speed = message.Value<double>("speed");
        var bpm = message.Value<double>("bpm");
        if (bpm <= 0)
            return;

        playback.PlaybackSpeed = speed;
        playback.Bpm = bpm;

        // The message was sent half a round trip ago; a running timeline has moved on by that much since.
        var rttMs = collab.GetPeerRttMs(0);
        var latencyBars = rttMs > 0 ? speed * bpm / 240.0 * (rttMs / 2000.0) : 0;
        bars += latencyBars;

        // A running timeline only needs a nudge once it drifted by a few milliseconds; a paused one is set
        // only when it differs, because every change of the timeline time also resets the effect time.
        if (Math.Abs(playback.TimeInBars - bars) > (speed == 0 ? MinPausedDifferenceBars : MaxTimeDriftBars))
            playback.TimeInBars = bars;

        // Effect time (what noise and idle motion animate on) runs on its own while the timeline is paused, so
        // idle motion has to match too or it would stand still between messages.
        if (message["idle"] != null)
            UserSettings.Config.EnableIdleMotion = message.Value<bool>("idle");

        if (message["fx"] != null)
            playback.FxTimeOverride = message.Value<double>("fx") + latencyBars;
    }

    /// <summary>Standby side, on the UI thread: writes the received setup files and applies layout and output window states.</summary>
    public static void Apply(JObject message)
    {
        if (!T3.Core.Show.FailoverArbiter.IsEnabled || T3.Core.Show.FailoverArbiter.Role != T3.Core.Show.FailoverArbiter.NodeRole.Standby)
            return;

        try
        {
            ApplySetupFiles(message);
            ApplyWindowLayout(message);
            ApplyOutputWindowStates(message);
            ApplyComposition(message);
        }
        catch (Exception e)
        {
            Log.Warning($"Failover: could not apply the mirrored state: {e.Message}");
        }
    }

    private static JObject? BuildMessage(CollabManager collab)
    {
        var view = ProjectView.Focused;
        if (view == null)
            return null;

        var folder = CollabManager.FindProjectFolder(collab.SharedProjectName) ?? view.OpenedProject.Package.Folder;
        if (string.IsNullOrEmpty(folder))
            return null;

        var message = new JObject
                          {
                              ["t"] = MessageType,
                              ["project"] = string.IsNullOrEmpty(collab.SharedProjectName)
                                                ? view.OpenedProject.Package.DisplayName
                                                : collab.SharedProjectName,
                          };

        var setupsFolder = SetupFiles.FolderIn(folder);
        var machinePath = Path.Combine(setupsFolder, MachineConfig.FileName);
        if (TryReadCached(_machineFile, machinePath, out var machineText))
            message["machine"] = machineText;

        if (OutputSetupHandling.TryGetActiveSetup(out var setup, out _))
        {
            var setupFile = setup.Name + Setup.FileSuffix;
            var setupPath = Path.Combine(setupsFolder, setupFile);
            if (TryReadCached(_setupFile, setupPath, out var setupText))
            {
                message["setupFile"] = setupFile;
                message["setup"] = setupText;
            }
        }

        ProjectView.CaptureWindowLayout(out var imGuiSettings, out var windowVisibility);
        message["layout"] = imGuiSettings;
        message["layoutVersion"] = ImGui.GetVersion();
        message["visibility"] = JObject.FromObject(windowVisibility);

        var outputWindows = new JArray();
        foreach (var window in OutputWindow.OutputWindowInstances)
        {
            if (window is OutputWindow outputWindow)
                outputWindows.Add(JObject.FromObject(outputWindow.CaptureState()));
        }

        message["outputWindows"] = outputWindows;

        // The composition the active machine has open, so the standby shows the same graph.
        if (view.CompositionInstance != null)
        {
            var path = new List<Guid>();
            Structure.PopulateInstancePath(view.CompositionInstance, path);
            message["composition"] = new JArray(path.Select(id => id.ToString()));
        }

        return message;
    }

    /// <summary>Re-reads the file only when its timestamp or size changed; this runs once a second on the active machine.</summary>
    private static bool TryReadCached(CachedFile cache, string path, out string text)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            text = "";
            return false;
        }

        if (cache.Path != path || cache.LastWriteUtc != info.LastWriteTimeUtc || cache.Length != info.Length)
        {
            cache.Text = File.ReadAllText(path);
            cache.Path = path;
            cache.LastWriteUtc = info.LastWriteTimeUtc;
            cache.Length = info.Length;
        }

        text = cache.Text;
        return true;
    }

    private static void ApplySetupFiles(JObject message)
    {
        var project = message.Value<string>("project") ?? "";
        var folder = CollabManager.FindProjectFolder(project);
        if (folder == null)
            return;

        var setupsFolder = SetupFiles.FolderIn(folder);
        Directory.CreateDirectory(setupsFolder);

        var changed = false;
        var setupFile = message.Value<string>("setupFile");
        var setup = message.Value<string>("setup");
        if (!string.IsNullOrEmpty(setupFile) && setup != null && setup != _lastAppliedSetup)
        {
            _lastAppliedSetup = setup;
            changed |= WriteIfDifferent(Path.Combine(setupsFolder, Path.GetFileName(setupFile)), setup);
        }

        var machine = message.Value<string>("machine");
        if (machine != null && machine != _lastAppliedMachine)
        {
            _lastAppliedMachine = machine;
            changed |= WriteIfDifferent(Path.Combine(setupsFolder, MachineConfig.FileName), machine);
        }

        if (changed)
        {
            Log.Info("Failover: mirrored the output setup of the active machine");
            OutputSetupHandling.OnSetupFilesChanged(folder);
        }
    }

    private static bool WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;

        File.WriteAllText(path, content);
        return true;
    }

    private static void ApplyWindowLayout(JObject message)
    {
        var layout = message.Value<string>("layout");
        if (string.IsNullOrEmpty(layout) || layout == _lastAppliedLayout)
            return;

        _lastAppliedLayout = layout;
        var visibility = message["visibility"]?.ToObject<Dictionary<string, bool>>();
        ProjectView.ApplyWindowLayout(layout, message.Value<string>("layoutVersion"), visibility);
    }

    private static void ApplyOutputWindowStates(JObject message)
    {
        if (message["outputWindows"] is not JArray states)
            return;

        var index = 0;
        foreach (var window in OutputWindow.OutputWindowInstances)
        {
            if (window is not OutputWindow outputWindow)
                continue;

            if (index >= states.Count)
                break;

            var stateJson = states[index].ToString(Formatting.None);
            index++;

            if (_lastAppliedWindowStates.TryGetValue(index, out var last) && last == stateJson)
                continue;

            _lastAppliedWindowStates[index] = stateJson;
            var state = JsonConvert.DeserializeObject<OutputWindowState>(stateJson);
            if (state != null)
                outputWindow.ApplyState(state);
        }
    }

    private static void ApplyComposition(JObject message)
    {
        if (message["composition"] is not JArray array || ProjectView.Focused is not { } view)
            return;

        var path = new List<Guid>(array.Count);
        foreach (var token in array)
        {
            if (!Guid.TryParse(token.Value<string>(), out var id))
                return;

            path.Add(id);
        }

        var key = string.Join('/', path);
        if (key == _lastAppliedComposition)
            return;

        _lastAppliedComposition = key;
        if (path.Count == 0 || view.CompositionInstance == null)
            return;

        var current = new List<Guid>();
        Structure.PopulateInstancePath(view.CompositionInstance, current);
        if (!current.SequenceEqual(path))
            view.TrySetCompositionOp(path);
    }

    private const long CheckIntervalMs = 1000;
    private const long TimeIntervalMs = 250;
    private const double MaxTimeDriftBars = 0.002;
    private const double MinPausedDifferenceBars = 0.000001;
    private const long ResendIntervalMs = 5000;

    private sealed class CachedFile
    {
        public string Path = "";
        public DateTime LastWriteUtc;
        public long Length;
        public string Text = "";
    }

    private static readonly CachedFile _machineFile = new();
    private static readonly CachedFile _setupFile = new();
    private static string? _lastAppliedMachine;
    private static string? _lastAppliedSetup;
    private static long _nextCheckTicks;
    private static long _nextTimeSendTicks;
    private static string? _lastAppliedComposition;
    private static long _resendAtTicks;
    private static int _lastPeerCount;
    private static string? _lastSentJson;
    private static string? _lastAppliedLayout;
    private static readonly Dictionary<int, string> _lastAppliedWindowStates = new();
}
