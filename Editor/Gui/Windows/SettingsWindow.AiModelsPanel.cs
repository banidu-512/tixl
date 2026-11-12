#nullable enable
using System;
using System.IO;
using System.Linq;
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Core.Logging;
using T3.Core.Settings;
using T3.Core.SystemUi;
using T3.Core.Utils;
using T3.Editor.Gui.AiModels;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui.Windows;

// ReSharper disable once UnusedType.Global
internal sealed partial class SettingsWindow
{
    /** Which family the model browser lists; All shows every entry grouped by family. */
    private enum AiModelBrowserFilter { All, DepthAnything, StreamDiffusion, MediaPipe }

    private static AiModelBrowserFilter _aiBrowserFilter = AiModelBrowserFilter.All;

    private static long _aiStoreSizeBytes;
    private static bool _aiStoreSizeValid;
    private static bool _aiAutoCheckDone;

    private static void DrawAiModelsPanel(ref bool changed)
    {
        var aiConfigChanged = false;
        var directMlChanged = false;

        DrawAiStoreSection(ref aiConfigChanged);

        FormInputs.AddVerticalSpace(16);
        DrawAiModelBrowserSection();

        FormInputs.AddVerticalSpace(16);
        DrawAiSetupCheckSection();

        FormInputs.AddVerticalSpace(16);
        DrawAiEnginesSection(ref aiConfigChanged, ref directMlChanged);

        if (aiConfigChanged || directMlChanged)
        {
            changed = true;
            AiModelSettings.Save();
            AiModelSettings.ResetRuntimeProbes();
        }
    }

    // ---------------------------------------------------------------- model store

    private static void DrawAiStoreSection(ref bool aiConfigChanged)
    {
        FormInputs.AddSectionSubHeader("Model Store");
        CustomComponents.HelpText(
            """
            Where downloaded AI models live. Set this to a drive with room to spare - models are
            multi-gigabyte and deliberately kept out of the TiXL installation and the git repository.
            The operators find them here automatically.
            """);
        FormInputs.AddVerticalSpace();

        var previousDirectory = AiModelSettings.Config.ModelsDirectory;
        aiConfigChanged |= FormInputs.AddFilePicker("Store folder",
                                            ref AiModelSettings.Config.ModelsDirectory,
                                            $"(default: {AiModelSettings.ModelsRoot})",
                                            null,
                                            "Models download into one subfolder per operator package here.",
                                            FileOperations.FilePickerTypes.Folder);

        if (!string.Equals(previousDirectory, AiModelSettings.Config.ModelsDirectory, StringComparison.Ordinal))
        {
            _aiStoreSizeValid = false;
            try
            {
                Directory.CreateDirectory(AiModelSettings.ModelsRoot);
            }
            catch (Exception e)
            {
                Log.Error($"[AI Models] Could not create model store folder '{AiModelSettings.ModelsRoot}': {e.Message}");
            }
        }

        FormInputs.ApplyIndent();
        if (ImGui.Button("Open folder"))
            CoreUi.Instance.OpenWithDefaultApplication(AiModelSettings.ModelsRoot);

        CustomComponents.TooltipForLastItem("Open the model store in the file browser.", AiModelSettings.ModelsRoot);

        ImGui.SameLine(0, 8 * T3Ui.UiScaleFactor);
        if (!_aiStoreSizeValid)
        {
            if (ImGui.Button("Calculate size"))
            {
                _aiStoreSizeBytes = GetDirectorySize(AiModelSettings.ModelsRoot);
                _aiStoreSizeValid = true;
            }
        }
        else
        {
            var sizeText = AiModelCatalog.FormatSize(_aiStoreSizeBytes);
            ImGui.TextUnformatted($"Store size: {sizeText}");
            ImGui.SameLine(0, 8 * T3Ui.UiScaleFactor);
            if (ImGui.SmallButton("Refresh"))
                _aiStoreSizeValid = false;
        }
    }

    // ---------------------------------------------------------------- setup check / one-click

    private static void DrawAiSetupCheckSection()
    {
        FormInputs.AddSectionSubHeader("Setup Check");

        // Run once per session when the page is first opened, so the state of things is
        // visible without a click. Re-checking stays on the button.
        if (!_aiAutoCheckDone)
        {
            _aiAutoCheckDone = true;
            AiSetupChecker.Run();
        }

        CustomComponents.HelpText(
            """
            Checks everything the AI operators need: CUDA/TensorRT runtimes, the model store and the
            models for DepthAnything, StreamDiffusion and MediaPipe. "Download defaults" installs the
            fastest working model of every family that is missing one.
            """);
        FormInputs.AddVerticalSpace();

        if (AiSetupChecker.IsFixing)
        {
            DrawStatusLine(UiColors.StatusActivated, AiSetupChecker.FixProgressText.Length > 0 ? AiSetupChecker.FixProgressText : "Working…");
            FormInputs.AddVerticalSpace();
        }

        FormInputs.ApplyIndent();
        var checking = AiSetupChecker.IsFixing;
        if (checking)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Run check"))
        {
            AiSetupChecker.Run();
        }

        CustomComponents.TooltipForLastItem("Probes the engine runtimes and checks which models are installed.",
                                            "Probing CUDA/TensorRT loads the runtime DLLs, exactly like the operators do later.");
        ImGui.SameLine(0, 8 * T3Ui.UiScaleFactor);

        if (CustomComponents.DrawCtaButton("Download defaults", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            if (AiSetupChecker.LastCheckAt == DateTime.MinValue)
                AiSetupChecker.Run();

            _ = AiSetupChecker.FixAllAsync();
        }

        CustomComponents.TooltipForLastItem("First checks the dependencies, then downloads the fastest basic default models for each AI operator family.",
                                            "Downloads go into the model store, not into the TiXL installation or the git repository.");
        if (checking)
        {
            ImGui.EndDisabled();
        }

        if (AiSetupChecker.LastCheckAt == DateTime.MinValue)
        {
            FormInputs.AddVerticalSpace();
            CustomComponents.HelpText("Not checked yet in this session.");
            return;
        }

        FormInputs.AddVerticalSpace();
        foreach (var item in AiSetupChecker.LastResults)
        {
            var color = item.Result switch
                            {
                                AiCheckResult.Ok      => UiColors.StatusControlled,
                                AiCheckResult.Fixable => UiColors.StatusWarning,
                                AiCheckResult.Missing => UiColors.StatusAttention,
                                _                     => UiColors.TextMuted,
                            };

            DrawStatusLine(color, $"{item.ResultText}  {item.Title}");
            CustomComponents.TooltipForLastItem(item.Title, item.Detail);
        }
    }

    // ---------------------------------------------------------------- engines

    private static void DrawAiEnginesSection(ref bool aiConfigChanged, ref bool directMlChanged)
    {
        FormInputs.AddSectionSubHeader("Engine Runtimes");
        CustomComponents.HelpText(
            """
            The ONNX operators run on CUDA, TensorRT or DirectML. Their runtimes are found automatically
            (PATH, TENSORRT_ROOT, the NVIDIA install folders). A folder set here wins over all of them,
            e.g. when the toolkit lives on another drive.
            """);
        FormInputs.AddVerticalSpace();

        DrawEngineStatus("CUDA 12 runtime (cuDNN 9)", () => OperatingSystem.IsWindows()
                                                            ? WindowsCudaRuntime.TryPrepare(out var reason)
                                                            : LinuxCudaRuntime.TryPrepare(out reason),
                                         () => AiModelSettings.Config.CudaRuntimeDirectory);

        FormInputs.AddVerticalSpace();
        DrawEngineStatus("TensorRT 10 runtime", () => OperatingSystem.IsWindows()
                                                           ? WindowsCudaRuntime.TryPrepareTensorRt(out var reason, out _)
                                                           : LinuxCudaRuntime.TryPrepareTensorRt(out reason, out _),
                                         () => AiModelSettings.Config.TensorRtDirectory);

        if (OperatingSystem.IsWindows())
        {
            FormInputs.AddVerticalSpace();

            var directMlAvailable = File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64-directml", "native", "onnxruntime.dll"))
                                    || File.Exists(Path.Combine(AiModelSettings.Config.DirectMlDirectory ?? "", "DirectML.dll"));
            DrawStatusLine(directMlAvailable ? UiColors.StatusControlled : UiColors.TextMuted,
                           directMlAvailable
                               ? "DirectML runtime is shipped with TiXL"
                               : "DirectML build is not shipped in this installation; CUDA is the GPU path");

            directMlChanged |= FormInputs.AddCheckBox("Prefer DirectML for AI models",
                                                      ref AiModelSettings.Config.PreferDirectMlForOnnx,
                                                      """
                                                      Runs the ONNX operators through DirectML instead of CUDA - for AMD/Intel GPUs
                                                      without a CUDA stack. Operators pick it up when they initialize. If an AI operator
                                                      already ran in this session, restart TiXL to switch the ONNX Runtime build.
                                                      """,
                                                      AiModelSettings.Defaults.PreferDirectMlForOnnx);

            if (directMlChanged && OnnxNativeSelector.IsAnyNativeBound)
            {
                FormInputs.ApplyIndent();
                if (CustomComponents.DrawCtaButton("Restart Editor", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
                    EditorRestart.TryRestart();

                CustomComponents.TooltipForLastItem("The ONNX Runtime build (CUDA vs DirectML) binds once per session.",
                                                    "The switch takes effect on the next start.");
            }
        }

        FormInputs.AddVerticalSpace();

        aiConfigChanged |= FormInputs.AddFilePicker("CUDA runtime folder",
                                            ref AiModelSettings.Config.CudaRuntimeDirectory,
                                            "(search PATH and CUDA_PATH)",
                                            null,
                                            "Folder containing cudart64_12.dll and cudnn64_9.dll. Checked before every standard location.",
                                            FileOperations.FilePickerTypes.Folder);

        aiConfigChanged |= FormInputs.AddFilePicker("TensorRT runtime folder",
                                            ref AiModelSettings.Config.TensorRtDirectory,
                                            "(search TENSORRT_ROOT and installs)",
                                            null,
                                            "Folder containing nvinfer_10.dll. Checked before TENSORRT_ROOT and the standard installs.",
                                            FileOperations.FilePickerTypes.Folder);

        if (OperatingSystem.IsWindows())
        {
            aiConfigChanged |= FormInputs.AddFilePicker("DirectML.dll folder",
                                                ref AiModelSettings.Config.DirectMlDirectory,
                                                "(use the shipped copy)",
                                                null,
                                                "Preferred over the DirectML.dll shipped with the DirectML build.",
                                                FileOperations.FilePickerTypes.Folder);
        }

        FormInputs.ApplyIndent();
        if (ImGui.Button("Re-detect runtimes"))
        {
            AiModelSettings.ResetRuntimeProbes();
            AiSetupChecker.Run();
        }

        CustomComponents.TooltipForLastItem("Re-probes CUDA and TensorRT after installing or moving a runtime.",
                                            "Operators pick the change up on their next use without a restart.");
    }

    private static void DrawEngineStatus(string title, Func<bool> probe, Func<string?> configuredFolder)
    {
        var ok = probe();
        var configured = configuredFolder();
        var note = string.IsNullOrEmpty(configured) ? "" : $"  (configured: {configured})";
        DrawStatusLine(ok ? UiColors.StatusControlled : UiColors.TextMuted,
                       $"{title}  -  {(ok ? "available" : "not found")}{note}");
    }

    // ---------------------------------------------------------------- browser

    private static void DrawAiModelBrowserSection()
    {
        FormInputs.AddSectionSubHeader("Model Browser");
        CustomComponents.HelpText("Compatible models for the AI operators. Downloads land in the model store; the operators find them automatically.");

        FormInputs.AddVerticalSpace();
        FormInputs.ApplyIndent();
        CustomComponents.SegmentedButton(ref _aiBrowserFilter);
        FormInputs.AddVerticalSpace();

        switch (_aiBrowserFilter)
        {
            case AiModelBrowserFilter.All:
                foreach (var family in AiModelCatalog.Families)
                {
                    DrawFamilyGroup(family);
                }
                break;

            // Explicit mapping: the browser filter and the family enum have different bases.
            case AiModelBrowserFilter.DepthAnything:
                DrawFamilyGroup(AiModelFamily.DepthAnything);
                break;

            case AiModelBrowserFilter.StreamDiffusion:
                DrawFamilyGroup(AiModelFamily.StreamDiffusion);
                break;

            case AiModelBrowserFilter.MediaPipe:
                DrawFamilyGroup(AiModelFamily.MediaPipe);
                break;
        }
    }

    private static void DrawFamilyGroup(AiModelFamily family)
    {
        var entries = AiModelCatalog.ForFamily(family);
        if (entries.Count == 0)
            return;

        ImGui.PushFont(Fonts.FontBold);
        ImGui.TextUnformatted(family.Title());
        ImGui.PopFont();
        CustomComponents.TooltipForLastItem(family.Title(), family.Description());
        FormInputs.AddVerticalSpace(4);

        foreach (var entry in entries)
        {
            DrawCatalogEntry(entry);
        }

        FormInputs.AddVerticalSpace(8);
    }

    private static void DrawCatalogEntry(ModelCatalogEntry entry)
    {
        FormInputs.ApplyIndent();
        ImGui.PushID(entry.Id);

        var rowStartX = ImGui.GetCursorPosX();
        var rowAvailX = ImGui.GetContentRegionAvail().X;
        var status = AiModelDownloadService.GetStatus(entry);
        var running = AiModelDownloadService.IsRunning(entry);
        var installed = entry.IsInstalled();
        var bundled = !installed && entry.IsAvailable();

        // --- first row: name + size + action button on the right edge
        ImGui.TextUnformatted(entry.DisplayName);
        if (entry.TotalSizeBytes > 0)
        {
            ImGui.SameLine(0, 8 * T3Ui.UiScaleFactor);
            ImGui.PushFont(Fonts.FontSmall);
            ImGui.TextColored(UiColors.TextMuted, AiModelCatalog.FormatSize(entry.TotalSizeBytes));
            ImGui.PopFont();
        }

        CustomComponents.TooltipForLastItem(entry.DisplayName, entry.Description);

        // Right-aligned action on the name line, pinned to the row's right edge
        const float actionWidth = 96;
        var buttonX = Math.Max(ImGui.GetCursorPosX() + 8 * T3Ui.UiScaleFactor,
                               rowStartX + rowAvailX - actionWidth * T3Ui.UiScaleFactor);
        ImGui.SameLine(buttonX);

        if (entry.RequiresLocalExport)
        {
            ImGui.PushFont(Fonts.FontSmall);
            ImGui.TextColored(installed ? UiColors.StatusControlled : UiColors.TextMuted,
                              installed ? "export present" : "own export");
            ImGui.PopFont();
        }
        else if (running)
        {
            if (ImGui.SmallButton("Cancel"))
                AiModelDownloadService.Cancel(entry);

            CustomComponents.TooltipForLastItem("Stop the download.",
                                                "Partial files are removed; already completed files of this model stay and a retry only fetches the rest.");
        }
        else if (status.State == AiModelDownloadService.States.Failed)
        {
            if (ImGui.SmallButton("Retry##failed"))
                AiModelDownloadService.Start(entry);

            CustomComponents.TooltipForLastItem("Download failed", status.Error);
        }
        else if (installed)
        {
            if (ImGui.SmallButton("Remove"))
                AiModelDownloadService.Remove(entry);

            CustomComponents.TooltipForLastItem("Delete the installed model from the store.", entry.StoreDirectory);
        }
        else
        {
            var downloadLabel = bundled ? "Copy to store" : "Download";
            if (ImGui.SmallButton(downloadLabel))
                AiModelDownloadService.Start(entry);

            if (bundled)
                CustomComponents.TooltipForLastItem("The model ships with TiXL. A copy in the store also covers trimmed installations.",
                                                    entry.StoreDirectory);
            else if (entry.IsDefault)
                CustomComponents.TooltipForLastItem("Download this model", "Installed as the family's default by the one-click setup.");
        }

        // --- second row: description / progress
        if (running)
        {
            var barWidth = ImGui.GetContentRegionAvail().X;
            if (barWidth > 0)
                ImGui.ProgressBar(status.Progress, new Vector2(barWidth, ImGui.GetFrameHeight() * 0.6f), "");

            ImGui.PushFont(Fonts.FontSmall);
            ImGui.TextColored(UiColors.TextMuted, status.CurrentFile.Length > 0 ? status.CurrentFile : "preparing…");
            ImGui.PopFont();
        }
        else if (status.State == AiModelDownloadService.States.Failed)
        {
            ImGui.PushFont(Fonts.FontSmall);
            ImGui.TextColored(UiColors.StatusAttention, $"Download failed: {status.Error}");
            ImGui.PopFont();
        }
        else
        {
            ImGui.PushFont(Fonts.FontSmall);
            ImGui.SetCursorPosX(rowStartX + 14 * T3Ui.UiScaleFactor);
            ImGui.PushTextWrapPos(rowStartX + rowAvailX);
            var statusText = installed ? "Installed"
                             : bundled ? "Bundled with TiXL"
                             : entry.RequiresLocalExport ? entry.Description
                             : entry.IsDefault ? $"{entry.Description}  Default for the one-click setup."
                             : entry.Description;
            ImGui.TextColored(UiColors.TextMuted, statusText);
            ImGui.PopTextWrapPos();
            ImGui.PopFont();
        }

        FormInputs.AddVerticalSpace(6);
        ImGui.PopID();
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return 0;

            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
