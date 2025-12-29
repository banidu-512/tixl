#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Core.Utils;

namespace T3.Editor.Gui.Windows.RenderExport;

internal sealed class RenderWindow : Window
{
    public RenderWindow()
    {
        Config.Title = "Render To File";
    }

    protected override void DrawContent()
    {
        FormInputs.AddVerticalSpace(15);
        DrawTimeSetup();
        ImGui.Indent(5);
        DrawInnerContent();
    }

    private void DrawInnerContent()
    {
        if (RenderProcess.State == RenderProcess.States.NoOutputWindow)
        {
            _lastHelpString = "No output view available";
            CustomComponents.HelpText(_lastHelpString);
            return;
        }

        if (RenderProcess.State == RenderProcess.States.NoValidOutputType)
        {
            _lastHelpString = RenderProcess.MainOutputType == null
                                  ? "The output view is empty"
                                  : "Select or pin a Symbol with Texture2D output in order to render to file";
            FormInputs.AddVerticalSpace(5);
            ImGui.Separator();
            FormInputs.AddVerticalSpace(5);
            ImGui.BeginDisabled();
            ImGui.Button("Start Render");
            CustomComponents.TooltipForLastItem("Only Symbols with a texture2D output can be rendered to file");
            ImGui.EndDisabled();
            CustomComponents.HelpText(_lastHelpString);
            return;
        }

        _lastHelpString = "Ready to render.";

        FormInputs.AddVerticalSpace();
        FormInputs.AddSegmentedButtonWithLabel(ref RenderSettings.RenderMode, "Render Mode");

        FormInputs.AddVerticalSpace();

        if (RenderSettings.RenderMode == RenderSettings.RenderModes.Video)
            DrawVideoSettings(RenderProcess.MainOutputOriginalSize);
        else
            DrawImageSequenceSettings();

        FormInputs.AddVerticalSpace(5);
        ImGui.Separator();
        FormInputs.AddVerticalSpace(5);

        DrawRenderingControls();

        CustomComponents.HelpText(RenderProcess.IsExporting ? RenderProcess.LastHelpString : _lastHelpString);
    }

    private static void DrawTimeSetup()
    {
        FormInputs.SetIndentToParameters();

        // Range
        FormInputs.AddSegmentedButtonWithLabel(ref RenderSettings.TimeRange, "Render Range");
        RenderTiming.ApplyTimeRange(RenderSettings.TimeRange, RenderSettings);

        FormInputs.AddVerticalSpace();

        // Reference switch converts values
        var oldRef = RenderSettings.Reference;
        if (FormInputs.AddSegmentedButtonWithLabel(ref RenderSettings.Reference, "Defined as"))
        {
            RenderSettings.StartInBars =
                (float)RenderTiming.ConvertReferenceTime(RenderSettings.StartInBars, oldRef, RenderSettings.Reference, RenderSettings.Fps);
            RenderSettings.EndInBars = (float)RenderTiming.ConvertReferenceTime(RenderSettings.EndInBars, oldRef, RenderSettings.Reference, RenderSettings.Fps);
        }

        var changed = false;
        changed |= FormInputs.AddFloat($"Start in {RenderSettings.Reference}", ref RenderSettings.StartInBars);
        changed |= FormInputs.AddFloat($"End in {RenderSettings.Reference}", ref RenderSettings.EndInBars);
        if (changed)
            RenderSettings.TimeRange = RenderSettings.TimeRanges.Custom;

        FormInputs.AddVerticalSpace();

        // FPS (also rescales frame-based numbers)
        FormInputs.AddFloat("FPS", ref RenderSettings.Fps, 0);
        if (RenderSettings.Fps < 0) RenderSettings.Fps = -RenderSettings.Fps;
        if (RenderSettings.Fps != 0 && Math.Abs(_lastValidFps - RenderSettings.Fps) > float.Epsilon)
        {
            RenderSettings.StartInBars = (float)RenderTiming.ConvertFps(RenderSettings.StartInBars, _lastValidFps, RenderSettings.Fps);
            RenderSettings.EndInBars = (float)RenderTiming.ConvertFps(RenderSettings.EndInBars, _lastValidFps, RenderSettings.Fps);
            _lastValidFps = RenderSettings.Fps;
        }

        RenderSettings.FrameCount = RenderTiming.ComputeFrameCount(RenderSettings);

        FormInputs.AddFloat("Resolution Factor", ref RenderSettings.ResolutionFactor, 0.125f, 4, 0.1f, true, true,
                            "A factor applied to the output resolution of the rendered frames.");

        if (FormInputs.AddInt("Motion Blur Samples", ref RenderSettings.OverrideMotionBlurSamples, -1, 50, 1,
                              "This requires a [RenderWithMotionBlur] operator. Please check its documentation."))
        {
            RenderSettings.OverrideMotionBlurSamples = Math.Clamp(RenderSettings.OverrideMotionBlurSamples, -1, 50);
        }
    }

    private void DrawVideoSettings(Int2 size)
    {
        FormInputs.AddInt("Bitrate", ref RenderSettings.Bitrate, 0, 500000000, 1000);

        var startSec = RenderTiming.ReferenceTimeToSeconds(RenderSettings.StartInBars, RenderSettings.Reference, RenderSettings.Fps);
        var endSec = RenderTiming.ReferenceTimeToSeconds(RenderSettings.EndInBars, RenderSettings.Reference, RenderSettings.Fps);
        var duration = Math.Max(0, endSec - startSec);

        double bpp = size.Width <= 0 || size.Height <= 0 || RenderSettings.Fps <= 0
                         ? 0
                         : RenderSettings.Bitrate / (double)(size.Width * size.Height) / RenderSettings.Fps;

        var q = GetQualityLevelFromRate((float)bpp);
        FormInputs.AddHint($"{q.Title} quality ({RenderSettings.Bitrate * duration / 1024 / 1024 / 8:0} MB for {duration / 60:0}:{duration % 60:00}s at {size.Width}×{size.Height})");
        CustomComponents.TooltipForLastItem(q.Description);

        FormInputs.AddFilePicker("File name",
                                 ref UserSettings.Config.RenderVideoFilePath!,
                                 ".\\Render\\Title-v01.mp4 ",
                                 null,
                                 "Using v01 in the file name will enable auto incrementation and don't forget the .mp4 extension, I'm serious.",
                                 FileOperations.FilePickerTypes.Folder);

        if (RenderPaths.IsFilenameIncrementable())
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, RenderSettings.AutoIncrementVersionNumber ? 0.7f : 0.3f);
            FormInputs.AddCheckBox("Increment version after export", ref RenderSettings.AutoIncrementVersionNumber);
            ImGui.PopStyleVar();
        }

        FormInputs.AddCheckBox("Export Audio (experimental)", ref RenderSettings.ExportAudio);
    }

    // Image sequence options
    private static void DrawImageSequenceSettings()
    {
        FormInputs.AddEnumDropdown(ref RenderSettings.FileFormat, "File Format");

        if (FormInputs.AddStringInput("File name", ref UserSettings.Config.RenderSequenceFileName))
        {
            UserSettings.Config.RenderSequenceFileName = (UserSettings.Config.RenderSequenceFileName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(UserSettings.Config.RenderSequenceFileName))
                UserSettings.Config.RenderSequenceFileName = "output";
        }

        if (ImGui.IsItemHovered())
        {
            CustomComponents.TooltipForLastItem("Base filename for the image sequence (e.g., 'frame' for 'frame_0000.png').\n" +
                                                "Invalid characters (?, |, \", /, \\, :) will be replaced with underscores.\n" +
                                                "If empty, defaults to 'output'.");
        }

        FormInputs.AddFilePicker("Output Folder",
                                 ref UserSettings.Config.RenderSequenceFilePath!,
                                 ".\\ImageSequence ",
                                 null,
                                 "Specify the folder where the image sequence will be saved.",
                                 FileOperations.FilePickerTypes.Folder);
    }

    private static void DrawRenderingControls()
    {
        if (!RenderProcess.IsExporting && !RenderProcess.IsToollRenderingSomething)
        {
            if (ImGui.Button("Start Render"))
            {
                RenderProcess.TryStart(RenderSettings);
            }
        }
        else if (RenderProcess.IsExporting)
        {
            ImGui.ProgressBar((float)RenderProcess.Progress, new Vector2(-1, 16 * T3Ui.UiScaleFactor));

            if (ImGui.Button("Cancel"))
            {
                var elapsed = T3.Core.Animation.Playback.RunTimeInSecs - RenderProcess.ExportStartedTimeLocal;
                RenderProcess.Cancel($"Render cancelled after {StringUtils.HumanReadableDurationFromSeconds(elapsed)}");
            }
        }
    }

    // Helpers
    private RenderSettings.QualityLevel GetQualityLevelFromRate(float bitsPerPixelSecond)
    {
        RenderSettings.QualityLevel q = default;
        for (var i = _qualityLevels.Length - 1; i >= 0; i--)
        {
            q = _qualityLevels[i];
            if (q.MinBitsPerPixelSecond < bitsPerPixelSecond)
                break;
        }

        return q;
    }

    internal override List<Window> GetInstances() => [];

    private static string _lastHelpString = string.Empty;
    private static float _lastValidFps = RenderSettings.Fps;
    private static RenderSettings RenderSettings => RenderSettings.Current;

    private readonly RenderSettings.QualityLevel[] _qualityLevels =
        {
            new(0.01, "Poor", "Very low quality. Consider lower resolution."),
            new(0.02, "Low", "Probable strong artifacts"),
            new(0.05, "Medium", "Will exhibit artifacts in noisy regions"),
            new(0.08, "Okay", "Compromise between filesize and quality"),
            new(0.12, "Good", "Good quality. Probably sufficient for YouTube."),
            new(0.5, "Very good", "Excellent quality, but large."),
            new(1, "Reference", "Indistinguishable. Very large files."),
        };
}