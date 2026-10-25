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

        var nodes = manager.DiscoveredServers;
        if (nodes.Count == 0)
        {
            ImGui.TextDisabled("Looking for other TiXL instances on this network...");
        }
        else
        {
            foreach (var node in nodes)
            {
                ImGui.PushID(node.Address + node.Port);
                var connected = IsConnected(manager, node.Name);
                FormInputs.SetCursorToParameterEdit();

                if (connected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
                    ImGui.TextUnformatted($"{node.Name}  ({node.Address})");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.TextDisabled("added");
                }
                else
                {
                    ImGui.TextUnformatted($"{node.Name}  ({node.Address})");
                    if (!node.IsMaster)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Add"))
                            manager.InviteClient(node);
                        CustomComponents.TooltipForLastItem("Ask this instance to render the show on this machine.");
                    }
                    else
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("master");
                    }
                }

                ImGui.PopID();
            }
        }

        FormInputs.AddVerticalSpace();
        DrawConnectedClients(manager);

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

    private static bool IsConnected(BoygroupManager manager, string name)
    {
        foreach (var client in manager.Clients)
        {
            if (string.Equals(client.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void DrawConnectedClients(BoygroupManager manager)
    {
        if (manager.Clients.Count == 0)
            return;

        OutputSetupHandling.TryGetActiveSetup(out var setup, out _);
        ImGui.TextUnformatted("Added render nodes:");
        foreach (var client in manager.Clients)
        {
            var statusColor = client.IsDeployed ? UiColors.StatusActivated : UiColors.StatusWarning;
            ImGui.Bullet();
            ImGui.TextUnformatted($"{client.Name}  ({client.Address})");

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, statusColor.Rgba);
            ImGui.TextUnformatted(client.StatusText);
            ImGui.PopStyleColor();

            if (client.MachineConfig?.Bindings.Count > 0)
            {
                FormInputs.ApplyIndent();
                CustomComponents.StylizedText(BoygroupManager.DescribeBindings(client.MachineConfig, setup),
                                              Fonts.FontSmall, UiColors.TextMuted);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Slave
    // ---------------------------------------------------------------------------------------

    private static void DrawSlaveSection(BoygroupManager manager)
    {
        if (manager.State == BoygroupManager.BoygroupState.WaitingForMaster)
        {
            CustomComponents.HelpText("""
                                      This instance is visible on the network. Add it from the render-node list on a
                                      master (Settings → Boygrouping → Master) to start receiving that show.
                                      """);
            return;
        }

        if (manager.State != BoygroupManager.BoygroupState.Client)
            return;

        ImGui.Bullet();
        ImGui.TextUnformatted($"Group: {manager.GroupName}");
        ImGui.Bullet();
        ImGui.TextUnformatted(manager.TimeFollowSummary);
        ImGui.Bullet();
        ImGui.TextUnformatted(manager.DeploySummary);

        FormInputs.AddVerticalSpace();
        CustomComponents.HelpText("""
                                  The master's output setups are applied here automatically. Which output reaches
                                  which display, stream or laser is set in Output Setup → Local Bindings on this
                                  machine and never overwritten by the master.
                                  """);
    }
}
