#nullable enable
using ImGuiNET;
using T3.Editor.App.Startup;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Startup" panel of the settings window: which project opens on launch, whether the editor
/// starts with the operating system, and automatic restart after crashes — the kiosk/show-machine
/// corner of the settings.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static void DrawStartupPanel(ref bool changed)
    {
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Project autoload");
        FormInputs.AddVerticalSpace();

        var selected = config.StartupProjectFolder;
        var options = new List<string> { string.Empty };
        options.AddRange(EditableSymbolProject.AllProjects.Select(p => p.Folder));

        if (FormInputs.AddDropdown(ref selected,
                                   options,
                                   "Project at startup",
                                   GetProjectDisplayName,
                                   """
                                   The selected project opens automatically on the next launch.
                                   Choose "Hub" to start without a project.
                                   """))
        {
            config.StartupProjectFolder = selected;
            changed = true;
        }

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Start with the computer");
        FormInputs.AddVerticalSpace();

        var autostart = config.AutostartWithSystem;
        if (FormInputs.AddCheckBox("Start TiXL when you log in",
                                   ref autostart,
                                   $"""
                                    Registers this TiXL build to start at login and load the selected
                                    startup project. The entry follows the platform convention
                                    ({OsAutostart.DescribeEntry()}) and can also be managed from your
                                    OS's autostart settings.
                                    """,
                                   false))
        {
            if (OsAutostart.TrySetEnabled(autostart, out var error))
            {
                config.AutostartWithSystem = autostart;
            }
            else
            {
                config.AutostartWithSystem = false;
                Log.Warning($"Could not {(autostart ? "install" : "remove")} the autostart entry: {error}");
            }

            changed = true;
        }

        ImGui.TextDisabled("Entry: " + OsAutostart.DescribeEntry());

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Crash protection");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddCheckBox("Restart automatically after a crash",
                                          ref config.CrashAutoRestart,
                                          """
                                          A small supervisor process relaunches the editor when it crashes,
                                          reloading the startup project. Normal quits and explicit kills
                                          (task manager, kill) are not restarted, and a project that
                                          crashes the editor at startup gives up after several attempts.
                                          """,
                                          false);

        ImGui.TextDisabled(CrashWatchdog.IsChildProcess
                               ? "Watchdog active: this instance is supervised."
                               : "Watchdog: becomes active on the next start.");
    }

    /// <summary>Dropdown label for a project folder; empty shows the Hub, unknown folders stay visible.</summary>
    private static string GetProjectDisplayName(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return "Hub  (no project)";

        foreach (var project in EditableSymbolProject.AllProjects)
        {
            if (!string.Equals(project.Folder, folder, StringComparison.Ordinal))
                continue;

            var rootName = project.RootNamespace.Split(".")[^1];
            return rootName == project.RootNamespace
                       ? rootName
                       : $"{rootName}  ({project.RootNamespace})";
        }

        return folder + "  (missing)";
    }
}
