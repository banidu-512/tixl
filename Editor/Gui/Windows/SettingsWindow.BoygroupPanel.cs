#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Show;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Boygrouping" panel: enable this instance, choose Master or Slave, and — on a master — add the
/// TiXL instances found on the network. There is no address, password or scan step: enabled instances
/// announce themselves, and adding one invites it to connect. Deployment shares the master's output
/// setups; each slave presents the outputs bound in its own Local Bindings.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static void DrawBoygroupPanel(ref bool changed)
    {
        var manager = BoygroupManager.Instance;
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Boygrouping");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddStringInput("Node name",
                                             ref config.BoygroupClientName,
                                             null,
                                             null,
                                             """
                                             This machine's name on the network and in the master's client list.
                                             Defaults to the OS machine name.
                                             """);

        FormInputs.AddVerticalSpace();
        DrawNetworkInterface(ref changed);
        FormInputs.AddVerticalSpace();

        var enabled = config.BoygroupEnabled;
        if (FormInputs.AddCheckBox("Enable",
                                   ref enabled,
                                   """
                                   Makes this instance visible to the other TiXL instances on the network.
                                   A master can then add it as a render node.
                                   """,
                                   false))
        {
            config.BoygroupEnabled = enabled;
            changed = true;
            manager.SetEnabled(enabled);
        }

        if (!config.BoygroupEnabled)
        {
            DrawBoygroupStatus(manager);
            DrawBoygroupError(manager);
            return;
        }

        var role = config.BoygroupRole;
        if (FormInputs.AddEnumDropdown(ref role, "Role",
                                       """
                                       Master shares its output setup and show state and drives playback.
                                       Slave renders what a master sends and keeps its own display bindings.
                                       """))
        {
            config.BoygroupRole = role;
            changed = true;
            manager.ApplyRole();
        }

        FormInputs.AddVerticalSpace();
        DrawBoygroupStatus(manager);
        FormInputs.AddVerticalSpace();

        if (config.BoygroupRole == BoygroupRole.Master)
            DrawMasterSection(manager);
        else
            DrawSlaveSection(manager);

        DrawBoygroupError(manager);
    }

    private static void DrawBoygroupStatus(BoygroupManager manager)
    {
        if (manager.State == BoygroupManager.BoygroupState.Off)
            return;

        ImGui.PushStyleColor(ImGuiCol.Text,
                             manager.State == BoygroupManager.BoygroupState.WaitingForMaster
                                 ? UiColors.StatusWarning.Rgba
                                 : UiColors.StatusActivated.Rgba);
        ImGui.TextWrapped(manager.StatusText);
        ImGui.PopStyleColor();
    }

    private static void DrawBoygroupError(BoygroupManager manager)
    {
        if (string.IsNullOrEmpty(manager.ErrorText))
            return;

        FormInputs.AddVerticalSpace();
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
        ImGui.TextWrapped(manager.ErrorText);
        ImGui.PopStyleColor();
    }

    // ---------------------------------------------------------------------------------------
    // Master
    // ---------------------------------------------------------------------------------------

    private static void DrawMasterSection(BoygroupManager manager)
    {
        FormInputs.AddSectionSubHeader("Render nodes");
        FormInputs.AddVerticalSpace();

        OutputSetupHandling.TryGetActiveSetup(out var setup, out _);
        var nodes = manager.DiscoveredServers;
        var clients = manager.Clients;
        if (nodes.Count == 0 && clients.Count == 0)
            ImGui.TextDisabled("Looking for other TiXL instances on this network...");

        // Connected nodes stop announcing themselves, so they are listed first - otherwise a node
        // would disappear from this list right after being added. Snapshot: Remove mutates the list.
        foreach (var client in clients.ToArray())
        {
            ImGui.PushID("client" + client.Address + client.Name);
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextUnformatted($"{client.Name}  ({client.Address})");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, (client.IsDeployed ? UiColors.StatusActivated : UiColors.StatusWarning).Rgba);
            ImGui.TextUnformatted(client.StatusText);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                manager.RemoveClient(client);
            CustomComponents.TooltipForLastItem("Disconnect this render node and remove it from the saved list.");

            var diagnostics = DescribeClientDiagnostics(client);
            if (!string.IsNullOrEmpty(diagnostics))
            {
                FormInputs.ApplyIndent();
                CustomComponents.StylizedText(diagnostics, Fonts.FontSmall, UiColors.TextMuted);
            }

            FormInputs.ApplyIndent();
            DrawDelayControls(manager, client);

            if (client.MachineConfig?.Bindings.Count > 0)
            {
                FormInputs.ApplyIndent();
                CustomComponents.StylizedText(BoygroupManager.DescribeBindings(client.MachineConfig, setup),
                                              Fonts.FontSmall, UiColors.TextMuted);
            }

            ImGui.PopID();
        }

        foreach (var node in nodes)
        {
            if (IsConnected(manager, node))
                continue;

            ImGui.PushID(node.Address + node.Port);
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextUnformatted($"{node.Name}  ({node.Address})");
            ImGui.SameLine();
            if (!node.IsMaster)
            {
                var saved = BoygroupManager.RememberedNodes.Any(n => string.Equals(n, node.Name, StringComparison.OrdinalIgnoreCase));
                if (ImGui.SmallButton(saved ? "Reconnect" : "Add"))
                    manager.InviteClient(node);
                CustomComponents.TooltipForLastItem(saved
                                                        ? "Saved render node — asks it to connect again."
                                                        : "Ask this instance to render the show on this machine.");

                if (saved)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Forget"))
                        BoygroupManager.ForgetNode(node.Name);
                    CustomComponents.TooltipForLastItem("Stop reconnecting this node after a restart.");
                }
            }
            else
            {
                ImGui.TextDisabled("master");
            }

            ImGui.PopID();
        }

        // Saved nodes that are neither connected nor visible right now; without this they could only
        // be removed during the short time they are discovered.
        var offline = BoygroupManager.RememberedNodes
                                         .Where(n => !manager.IsClientConnected(n)
                                                     && !nodes.Any(node => string.Equals(node.Name, n, StringComparison.OrdinalIgnoreCase)))
                                         .ToList();
        if (offline.Count > 0)
        {
            FormInputs.AddVerticalSpace(6);
            ImGui.TextDisabled("Saved, not found:");
            foreach (var name in offline)
            {
                ImGui.PushID("offline" + name);
                ImGui.Bullet();
                ImGui.TextUnformatted(name);
                ImGui.SameLine();
                if (ImGui.SmallButton("Forget"))
                    BoygroupManager.ForgetNode(name);
                ImGui.PopID();
            }
        }

        DrawMasterDiagnostics(manager);

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Show control");
        FormInputs.AddVerticalSpace();

        FormInputs.SetCursorToParameterEdit();
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

        FormInputs.SetCursorToParameterEdit();
        if (ImGui.Button("Deploy & Sync"))
            manager.DeployAndSync();
        CustomComponents.TooltipForLastItem("Send the master's output setups to the added render nodes. Project files are not transferred.");

        if (!string.IsNullOrEmpty(manager.DeploySummary))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(manager.DeploySummary);
        }
    }

    /// <summary>Per-node presentation delay. Positive runs the node later, which is how projector or
    /// capture latency differences between machines are lined up.</summary>
    private static void DrawDelayControls(BoygroupManager manager, BoygroupManager.ClientInfo client)
    {
        ImGui.PushID("delay" + client.Name);
        CustomComponents.StylizedText($"Delay {client.DelayMs:+0;-0;0} ms", Fonts.FontSmall,
                                      client.DelayMs == 0 ? UiColors.TextMuted : UiColors.StatusActivated);
        ImGui.SameLine();

        if (ImGui.SmallButton("-10"))
            manager.SetClientDelay(client, client.DelayMs - 10);

        ImGui.SameLine();
        if (ImGui.SmallButton("+10"))
            manager.SetClientDelay(client, client.DelayMs + 10);

        if (client.DelayMs != 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
                manager.SetClientDelay(client, 0);
        }

        CustomComponents.TooltipForLastItem($"Shift this node's presentation in time (up to ±{BoygroupManager.MaxSyncDelayMs} ms). "
                                            + "Positive values render later, negative values earlier; the master re-syncs the node immediately.");
        ImGui.PopID();
    }

    private static string DescribeClientDiagnostics(BoygroupManager.ClientInfo client)
    {
        var parts = new List<string>();
        if (client.RttMs >= 0)
            parts.Add($"RTT {client.RttMs:0} ms");

        parts.Add($"clock {client.ClockOffsetMs:+0.0;-0.0;0} ms");
        if (client.SyncErrorMs > -0.5)
            parts.Add($"sync {client.SyncErrorMs:+0.0;-0.0;0} ms");
        if (client.SyncAgeMs >= 0)
            parts.Add(client.SyncAgeMs < 1000 ? $"synced {client.SyncAgeMs:0} ms ago" : $"synced {client.SyncAgeMs / 1000:0} s ago");
        if (client.ClientFps > 0)
            parts.Add($"{client.ClientFps:0} fps");
        if (!string.IsNullOrEmpty(client.ClientVersion))
            parts.Add($"v{client.ClientVersion}");

        return string.Join(" · ", parts);
    }

    private static void DrawMasterDiagnostics(BoygroupManager manager)
    {
        var config = UserSettings.Config;
        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Diagnostics");
        FormInputs.AddVerticalSpace();

        var network = string.IsNullOrWhiteSpace(config.NetworkInterface) ? "Automatic" : config.NetworkInterface;
        FormInputs.SetCursorToParameterEdit();
        ImGui.TextDisabled($"Port {config.BoygroupPort} · {network}");

        var uptime = TimeSpan.FromSeconds(manager.SessionUptimeSeconds);
        FormInputs.SetCursorToParameterEdit();
        ImGui.TextDisabled($"Uptime {uptime.ToString(@"hh\:mm\:ss")} · master {manager.EditorFps:0} fps");

        FormInputs.SetCursorToParameterEdit();
        ImGui.TextDisabled($"Connected {manager.Clients.Count} · saved {BoygroupManager.RememberedNodes.Count}"
                           + $" · discovered {manager.DiscoveredServers.Count}");
    }

    private static bool IsConnected(BoygroupManager manager, BoygroupManager.DiscoveredServer node)
    {
        foreach (var client in manager.Clients)
        {
            if (client.Address == node.Address || string.Equals(client.Name, node.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Slave
    // ---------------------------------------------------------------------------------------

    private static void DrawSlaveSection(BoygroupManager manager)
    {
        if (manager.State == BoygroupManager.BoygroupState.WaitingForMaster)
        {
            var remembered = UserSettings.Config.BoygroupMasterName;
            CustomComponents.HelpText(string.IsNullOrEmpty(remembered)
                                          ? """
                                            Looking for a master on the network. An enabled slave joins the first master that
                                            appears; it can also be added from a master's render-node list.
                                            """
                                          : $"""
                                             Looking for master '{remembered}'. An enabled slave reconnects to it as soon as it
                                             appears, and to any other master while this one is absent.
                                             """);
            return;
        }

        if (manager.State != BoygroupManager.BoygroupState.Client)
            return;

        ImGui.Bullet();
        ImGui.TextUnformatted(string.IsNullOrEmpty(manager.MasterName) ? $"Group: {manager.GroupName}" : $"Master: {manager.MasterName} · group {manager.GroupName}");
        ImGui.Bullet();
        ImGui.TextUnformatted(manager.TimeFollowSummary);
        ImGui.Bullet();
        ImGui.TextUnformatted(manager.DeploySummary);

        FormInputs.AddVerticalSpace(6);
        FormInputs.SetCursorToParameterEdit();
        var sync = manager.SyncErrorMs > -0.5 ? $"{manager.SyncErrorMs:+0.0;-0.0;0} ms" : "—";
        ImGui.TextDisabled($"Clock offset {manager.ClockOffsetMs:+0.0;-0.0;0} ms · sync error {sync}"
                           + $" · delay {manager.ClientDelayMs:+0;-0;0} ms · {manager.EditorFps:0} fps");

        FormInputs.AddVerticalSpace();
        CustomComponents.HelpText("""
                                  The master's output setups are applied here automatically. Which output reaches
                                  which display, stream or laser is set in Output Setup → Local Bindings on this
                                  machine and never overwritten by the master.
                                  """);
    }
}
