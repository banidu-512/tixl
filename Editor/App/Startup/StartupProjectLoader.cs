#nullable enable
using System.IO;
using ImGuiNET;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Window;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.App.Startup;

/// <summary>
/// Opens the project selected in Settings → Startup ("Project at startup") once after launch,
/// with the same behavior as clicking it in the Hub's project list.
/// </summary>
internal static class StartupProjectLoader
{
    private static bool _checked;

    /// <summary>Called once per frame; does its work on the first frame with a complete layout.</summary>
    internal static void CheckOncePerLaunch()
    {
        if (_checked || ImGui.GetFrameCount() <= 2)
            return;

        _checked = true;

        var folder = UserSettings.Config.StartupProjectFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return;

        var project = FindProject(folder);
        if (project == null)
        {
            Log.Warning($"The startup project '{folder}' no longer exists — starting with the Hub.");
            return;
        }

        if (!OpenedProject.TryCreate(project, out var openedProject, out var failureLog) || openedProject == null)
        {
            Log.Warning($"Could not load the startup project '{project.DisplayName}': {failureLog}");
            return;
        }

        var graphWindow = GetGraphWindow();
        if (graphWindow == null || !graphWindow.TrySetToProject(openedProject))
        {
            Log.Warning($"Could not open the startup project '{project.DisplayName}' in a graph window.");
            return;
        }

        Log.Info($"Startup project '{project.DisplayName}' loaded.");
    }

    private static EditableSymbolProject? FindProject(string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? normalized = null;
        foreach (var project in EditableSymbolProject.AllProjects)
        {
            normalized ??= Path.GetFullPath(folder);
            if (string.Equals(Path.GetFullPath(project.Folder), normalized, comparison))
                return project;
        }

        return null;
    }

    // Prefer the visible graph window — hidden instances can exist while the Hub is shown.
    private static GraphWindow? GetGraphWindow()
    {
        GraphWindow? graphWindow = null;
        foreach (var window in GraphWindow.GraphWindowInstances)
        {
            if (!window.Config.Visible)
                continue;

            graphWindow = window;
            break;
        }

        return graphWindow ?? (GraphWindow.GraphWindowInstances.Count > 0 ? GraphWindow.GraphWindowInstances[0] : null);
    }
}
