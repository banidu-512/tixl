#nullable enable
using System;
using System.Linq;
using ImGuiNET;
using T3.Core.Utils;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui.Windows;

internal sealed partial class SettingsWindow
{
    private const string AutomaticInterfaceLabel = "Automatic";

    /// <summary>
    /// Shared by the Collaboration, Failover and Boygrouping panels: one adapter per machine keeps
    /// sessions, discovery and failover heartbeats on the show network instead of a VPN or Wi-Fi.
    /// </summary>
    private static void DrawNetworkInterface(ref bool changed)
    {
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Network");
        FormInputs.AddVerticalSpace();

        var interfaces = LanInterfaces.GetUsable();
        var selected = interfaces.FirstOrDefault(i => string.Equals(i.Name, config.NetworkInterface,
                                                                     StringComparison.OrdinalIgnoreCase));
        var preview = selected?.DisplayName
                      ?? (string.IsNullOrWhiteSpace(config.NetworkInterface)
                              ? AutomaticInterfaceLabel
                              : $"{config.NetworkInterface} (not connected)");

        const string tooltip = """
                               The adapter TiXL binds to for hosting collaboration and boygrouping,
                               LAN discovery and failover heartbeats.
                               Automatic uses all interfaces and follows the OS route. Pick your show
                               network adapter when the machine also has Wi-Fi, VPN or Hyper-V adapters.
                               Takes effect the next time a session or server starts.
                               """;

        FormInputs.DrawInputLabel("Interface");
        var inputSize = FormInputs.GetAvailableInputSize(tooltip, false, true);
        ImGui.SetNextItemWidth(inputSize.X);
        if (ImGui.BeginCombo("##networkinterface", preview, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(AutomaticInterfaceLabel, string.IsNullOrWhiteSpace(config.NetworkInterface)))
            {
                config.NetworkInterface = "";
                changed = true;
            }

            foreach (var lanInterface in interfaces)
            {
                if (!ImGui.Selectable(lanInterface.DisplayName, selected?.Name == lanInterface.Name))
                    continue;

                config.NetworkInterface = lanInterface.Name;
                changed = true;
            }

            ImGui.EndCombo();
        }

        FormInputs.AppendTooltip(tooltip);

        if (interfaces.Count == 0)
        {
            CustomComponents.HelpText("No active IPv4 network adapter was found. TiXL binds all interfaces until one is available.");
        }
        else if (selected == null && !string.IsNullOrWhiteSpace(config.NetworkInterface))
        {
            CustomComponents.HelpText($"The saved adapter '{config.NetworkInterface}' is not connected. TiXL binds all interfaces until it returns.");
        }
    }
}
