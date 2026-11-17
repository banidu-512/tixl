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

        // Boygrouping is switched through the menu-bar mode selector; this panel configures the
        // role and connection and runs the session. Starting or joining claims the boygroup mode,
        // which ends a failover/collab session on this machine (the modes are mutually exclusive).

        FormInputs.AddVerticalSpace();

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
        // Session: role, then either the start/join controls or the live session
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Session");
        FormInputs.AddVerticalSpace();

        FormInputs.DrawInputLabel("Role");
        CustomComponents.TooltipForLastItem("A master hosts the show and deploys to render nodes; a slave renders what the master sends.");
        FormInputs.SetCursorToParameterEdit();
        var roleChanged = false;
        if (ImGui.RadioButton("Master (server)", config.BoygroupIsMaster))
        {
            config.BoygroupIsMaster = true;
            roleChanged = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        if (ImGui.RadioButton("Slave (render node)", !config.BoygroupIsMaster))
        {
            config.BoygroupIsMaster = false;
            roleChanged = true;
        }

        if (roleChanged && config.BoygroupEnabled)
        {
            UserSettings.Save();
            manager.Leave();
            manager.ApplyEnabledSetting();
        }

        changed |= roleChanged;

        FormInputs.AddVerticalSpace();

        if (manager.State == BoygroupManager.BoygroupState.Off)
        {
            _boygroupGroup ??= UserSettings.Config.BoygroupGroups.FirstOrDefault() ?? "Main";
            _boygroupServerAddress ??= "127.0.0.1";

            // The master's one-click path
            FormInputs.DrawInputLabel("Master");
            FormInputs.SetCursorToParameterEdit();
            if (CustomComponents.DrawCtaButton("Start Server", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
            {
                ShowControlMode.Activate(ShowControlMode.Boygroup);
                manager.StartServer(config.BoygroupPort, config.CollabPassword, _boygroupGroup);
            }

            CustomComponents.TooltipForLastItem("This machine becomes the boygroup server: it deploys files and live state to clients and drives playback. Uses the group entered below.");

            FormInputs.AddVerticalSpace();

            // The client's path: address + group, then join
            changed |= FormInputs.AddStringInput("Server address",
                                                 ref _boygroupServerAddress,
                                                 null,
                                                 null,
                                                 "IP or host name of the boygroup master. Servers on this network are discovered automatically below.");

            var joinGroup = _boygroupGroup ?? "Main";
            FormInputs.DrawInputLabel("Group");
            FormInputs.SetCursorToParameterEdit();
            ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, true).X);
            ImGui.PushID("##boygroupgroup");
            if (ImGui.InputText("##group", ref joinGroup, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _boygroupGroup = joinGroup;
                ShowControlMode.Activate(ShowControlMode.Boygroup);
                manager.JoinServer(_boygroupServerAddress, config.BoygroupPort, joinGroup, config.CollabPassword);
            }

            ImGui.PopID();
            _boygroupGroup = joinGroup;

            FormInputs.AddVerticalSpace(4);
            FormInputs.SetCursorToParameterEdit();
            if (ImGui.Button("Join Server"))
            {
                ShowControlMode.Activate(ShowControlMode.Boygroup);
                manager.JoinServer(_boygroupServerAddress, config.BoygroupPort, _boygroupGroup ?? "Main", config.CollabPassword);
            }

            CustomComponents.TooltipForLastItem("Joins the master at the address above as a render node.");

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
        changed |= FormInputs.AddCheckBox("Deploy & sync when a node joins",
                                          ref config.BoygroupAutoDeployOnJoin,
                                          """
                                          The master runs Deploy & Sync automatically shortly after a render node
                                          joins the group, so an added slave is current without anyone clicking.
                                          Nodes that are already up to date are left running (no restart).
                                          """,
                                          true);

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

        // Where the slave takes its playback clock from. "Internal" needs nothing beyond TiXL
        // (NTP exchange, ~2 ms phase on a wired LAN); "PTP (OS clock)" trusts that a discipline
        // daemon keeps this machine's clock on the show reference, and applies anchors directly.
        var syncMode = config.BoygroupClockSync;
        var syncChoices = new List<string> { UserSettings.SyncInternal, UserSettings.SyncPtp };
        if (FormInputs.AddDropdown(ref syncMode, syncChoices, "Clock sync",
                                   """
                                   Internal: TiXL measures the clock offset to the master itself (no extra
                                   software, about two milliseconds of phase on a wired LAN). PTP: trust this
                                   machine's OS clock, which a PTP discipline daemon (linuxptp, w32time with
                                   a PTP source, ...) keeps on the show reference - use when the venue runs PTP.
                                   """))
        {
            config.BoygroupClockSync = syncMode;
            UserSettings.Save();
            changed = true;
        }

        DrawNetworkInterface(ref changed);
    }

    private static string? _boygroupGroup;
    private static string? _boygroupServerAddress;

    private static void DrawBoygroupSession(BoygroupManager manager)
    {
        FormInputs.DrawInputLabel("State");
        FormInputs.SetCursorToParameterEdit();
        CustomComponents.StylizedText(manager.StatusText, Fonts.FontNormal, UiColors.StatusActivated);

        FormInputs.AddVerticalSpace();

        if (manager.State == BoygroupManager.BoygroupState.Server)
        {
            FormInputs.AddVerticalSpace();
            FormInputs.DrawFieldSetHeader("Connected clients");
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
            FormInputs.DrawInputLabel("Group");
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextUnformatted(manager.GroupName);

            FormInputs.DrawInputLabel("Clock");
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextDisabled(manager.TimeFollowSummary);

            FormInputs.DrawInputLabel("Deploy");
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextDisabled(manager.DeploySummary);
        }

        FormInputs.AddVerticalSpace();
        if (ImGui.Button("Deploy & Sync"))
            manager.DeployAndSync();

        ImGui.SameLine();
        if (ImGui.Button("Leave"))
            manager.Leave(manual: true);

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

        foreach (var client in clients)
        {
            var statusColor = client.IsDeployed ? UiColors.StatusActivated : UiColors.StatusWarning;
            ImGui.Bullet();
            ImGui.TextUnformatted($"{client.Name}  ·  {client.Group}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({client.Address})");
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
        FormInputs.DrawFieldSetHeader("Servers on this network");
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
            ImGui.TextUnformatted(server.ServerName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({server.Address}:{server.Port}) · {string.Join(", ", server.Groups)}");

            ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X
                           - ImGui.CalcTextSize("Join").X - ImGui.GetStyle().FramePadding.X * 4);
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
