#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Show;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Boygrouping" panel of the settings window: distribute a show to render nodes, vvvv-style —
/// a server instance deploys project files and live graph/output state to clients, controls
/// playback, and each client renders only the outputs assigned to it in the Output Setup.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static void DrawBoygroupPanel(ref bool changed)
    {
        var manager = BoygroupManager.Instance;
        var config = UserSettings.Config;

        // -------------------------------------------------------------------------------------
        // Identity & connection
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Identity");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddStringInput("Node name",
                                             ref config.BoygroupClientName,
                                             null,
                                             null,
                                             """
                                             This machine's name in a boygroup group. Defaults to the OS machine name.
                                             Output Setup assignments reference this name.
                                             """);

        changed |= FormInputs.AddInt("Port",
                                     ref config.BoygroupPort,
                                     1024, 65535, 1,
                                     "TCP port for boygroup sessions. Must match between server and clients.",
                                     44720);

        changed |= FormInputs.AddStringInput("Password",
                                             ref config.CollabPassword,
                                             null,
                                             null,
                                             "Optional. Required on the server and all clients of a group. Strongly recommended: deploying writes project files on clients.");

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Session
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Session");
        FormInputs.AddVerticalSpace();

        if (manager.State == BoygroupManager.BoygroupState.Off)
        {
            _boygroupGroup ??= UserSettings.Config.BoygroupGroups.FirstOrDefault() ?? "Main";

            if (CustomComponents.DrawCtaButton("Start Server", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
            {
                manager.StartServer(config.BoygroupPort, config.CollabPassword, _boygroupGroup);
            }

            CustomComponents.TooltipForLastItem("This machine becomes the boygroup server: it deploys files and live state to clients and drives playback.");

            FormInputs.AddVerticalSpace();
            ImGui.TextUnformatted("Join a group:");
            ImGui.PushID("##boygroupgroup");
            if (ImGui.InputText("Group", ref _boygroupGroup, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                manager.JoinServer("127.0.0.1", config.BoygroupPort, _boygroupGroup, config.CollabPassword);
            }

            ImGui.PopID();
            ImGui.SameLine();
            if (ImGui.Button("Join"))
            {
                manager.JoinServer(_boygroupServerAddress ?? "127.0.0.1", config.BoygroupPort, _boygroupGroup, config.CollabPassword);
            }

            CustomComponents.TooltipForLastItem("Address of the boygroup server. Clients on the same network also appear below via discovery.");

            DrawDiscoveredServers(manager, config.BoygroupPort, ref changed);
        }
        else
        {
            DrawBoygroupSession(manager);
        }

        if (!string.IsNullOrEmpty(manager.ErrorText))
        {
            FormInputs.AddVerticalSpace();
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
            ImGui.TextWrapped(manager.ErrorText);
            ImGui.PopStyleColor();
        }

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Groups (multiboygrouping)
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Groups (multiboygrouping)");
        CustomComponents.HelpText("""
                                  A server can host several groups (e.g. 'Wall', 'Floor'). Clients join one group each;
                                  deploy and playback controls act per group. A client may also connect to two different
                                  servers by running the join twice with different group names on separate ports.
                                  """);
        FormInputs.AddVerticalSpace();

        var groups = config.BoygroupGroups;
        var removeIndex = -1;
        for (var index = 0; index < groups.Count; index++)
        {
            ImGui.PushID(index);
            var group = groups[index];
            if (ImGui.InputText("##group", ref group, 64))
                groups[index] = group;

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove") && groups.Count > 1)
                removeIndex = index;

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            groups.RemoveAt(removeIndex);
            changed = true;
        }

        if (ImGui.SmallButton("Add group"))
        {
            groups.Add($"Group {groups.Count + 1}");
            changed = true;
        }

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Deploy options
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Deployment");
        changed |= FormInputs.AddCheckBox("Mirror project folder",
                                          ref config.BoygroupDeployMirror,
                                          """
                                          When deploying, also delete files on clients that the server's project does not
                                          have. Off by default: extra client-side files are only reported, never removed.
                                          """,
                                          false);

        changed |= FormInputs.AddCheckBox("Auto-join after restart",
                                          ref config.BoygroupAutoJoin,
                                          """
                                          Clients remember their server and group and rejoin automatically on launch —
                                          used after a deployment restarts the client to apply new files.
                                          """,
                                          true);
    }

    private static string? _boygroupGroup;
    private static string? _boygroupServerAddress;

    private static void DrawBoygroupSession(BoygroupManager manager)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
        ImGui.TextUnformatted(manager.StatusText);
        ImGui.PopStyleColor();

        FormInputs.AddVerticalSpace();

        if (manager.State == BoygroupManager.BoygroupState.Server)
        {
            DrawBoygroupClients(manager);

            FormInputs.AddVerticalSpace();
            FormInputs.AddSectionSubHeader("Playback control");
            FormInputs.AddVerticalSpace();

            if (ImGui.Button("Play all"))
                manager.SendPlayControl(true);

            ImGui.SameLine();
            if (ImGui.Button("Pause all"))
                manager.SendPlayControl(false);

            ImGui.SameLine();
            if (ImGui.Button("Sync time now"))
                manager.SendTimeSyncNow();

            ImGui.SameLine();
            ImGui.TextDisabled(manager.TimeFollowSummary);

            FormInputs.AddVerticalSpace();
        }
        else
        {
            ImGui.Bullet();
            ImGui.TextUnformatted($"Group: {manager.GroupName}");
            ImGui.Bullet();
            ImGui.TextUnformatted(manager.TimeFollowSummary);
            ImGui.Bullet();
            ImGui.TextUnformatted(manager.DeploySummary);
        }

        FormInputs.AddVerticalSpace();
        if (ImGui.Button("Deploy & Sync"))
            manager.DeployAndSync();

        ImGui.SameLine();
        if (ImGui.Button("Leave"))
            manager.Leave();

        if (manager.State == BoygroupManager.BoygroupState.Client && manager.RestartPending)
        {
            FormInputs.AddVerticalSpace();
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
            ImGui.TextUnformatted("Deployment finished — restart to apply new files...");
            ImGui.PopStyleColor();
        }
    }

    private static void DrawBoygroupClients(BoygroupManager manager)
    {
        var clients = manager.Clients;
        if (clients.Count == 0)
        {
            ImGui.TextDisabled("No clients connected. Start TiXL on the render nodes and join this server.");
            return;
        }

        ImGui.TextUnformatted("Clients:");
        foreach (var client in clients)
        {
            var statusColor = client.IsDeployed ? UiColors.StatusActivated : UiColors.StatusWarning;
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.Text.Rgba);
            ImGui.Bullet();
            ImGui.TextUnformatted($"{client.Name}  ({client.Address})  group: {client.Group}");
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, statusColor.Rgba);
            ImGui.TextUnformatted(client.StatusText);
            ImGui.PopStyleColor();
        }
    }

    private static bool _boygroupDiscoveryActive;
    private static DateTime _boygroupDiscoveryUntil;
    private static readonly List<BoygroupManager.DiscoveredServer> _boygroupDiscovered = [];

    private static void DrawDiscoveredServers(BoygroupManager manager, int discoveryPort, ref bool changed)
    {
        FormInputs.AddVerticalSpace();
        ImGui.TextUnformatted("Servers on this network:");
        ImGui.SameLine();

        if (!_boygroupDiscoveryActive)
        {
            if (ImGui.SmallButton("Scan"))
            {
                _boygroupDiscovered.Clear();
                manager.StartDiscovery();
                _boygroupDiscoveryActive = true;
                _boygroupDiscoveryUntil = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            }
        }
        else
        {
            ImGui.TextDisabled("scanning...");
        }

        manager.DrainDiscovery();

        if (_boygroupDiscoveryActive && DateTime.UtcNow > _boygroupDiscoveryUntil)
        {
            manager.StopDiscovery();
            _boygroupDiscoveryActive = false;
        }

        foreach (var server in manager.DiscoveredServers)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted($"{server.ServerName}  ({server.Address}:{server.Port})  groups: {string.Join(", ", server.Groups)}");

            ImGui.SameLine();
            ImGui.PushID(server.Address + server.Port);
            if (ImGui.SmallButton("Join"))
            {
                manager.JoinServer(server.Address, server.Port, _boygroupGroup ?? server.Groups.FirstOrDefault() ?? "Main",
                                   UserSettings.Config.CollabPassword);
            }

            ImGui.PopID();
        }
    }
}
