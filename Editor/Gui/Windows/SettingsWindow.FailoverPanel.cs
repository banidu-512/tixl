#nullable enable
using ImGuiNET;
using T3.Core.Show;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Failover" panel of the settings window: active/standby output arbitration for show-critical
/// network outputs (Artnet, sACN, NDI/Spout). Pair this with boygrouping or manual sync so both
/// machines run the same composition — the arbiter only decides who streams.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static void DrawFailoverPanel(ref bool changed)
    {
        var config = UserSettings.Config;

        // Failover is switched through the menu-bar mode selector; this panel configures the pair.

        // -------------------------------------------------------------------------------------
        // Status first: this is what the user came to check
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Status");
        FormInputs.AddVerticalSpace();

        var roleColor = FailoverArbiter.RoleText.StartsWith("ACTIVE")
                            ? UiColors.StatusActivated
                            : FailoverArbiter.RoleText is "Standby" or "Handover"
                                ? UiColors.StatusWarning
                                : UiColors.TextMuted;

        ImGui.PushStyleColor(ImGuiCol.Text, roleColor.Rgba);
        ImGui.PushFont(Fonts.FontBold);
        ImGui.TextUnformatted(FailoverArbiter.RoleText);
        ImGui.PopFont();
        ImGui.PopStyleColor();

        ImGui.TextDisabled(FailoverArbiter.PeerSummary);
        ImGui.TextDisabled($"Takeovers performed: {FailoverArbiter.TakeoverCount}");

        if (!string.IsNullOrEmpty(FailoverArbiter.LastEvent))
        {
            FormInputs.AddVerticalSpace(4);
            foreach (var failoverEvent in FailoverArbiter.RecentEvents)
            {
                ImGui.TextDisabled($"· {failoverEvent}");
            }
        }

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Pairing: what makes two machines a pair
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Pairing");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddStringInput("Show Id",
                                             ref config.FailoverShowId,
                                             null,
                                             null,
                                             "Isolates failover pairs on one network. Both machines of a pair must use the same Show Id.");

        changed |= FormInputs.AddInt("Priority",
                                     ref config.FailoverPriority,
                                     0, 255, 1,
                                     "Higher priority wins. Give your preferred master a higher value (e.g. 100 vs 50). Ties break by lower IP / lower node id.",
                                     100);

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Timing: how fast a takeover happens
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Timing");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddInt("Heartbeat (ms)",
                                     ref config.FailoverHeartbeatMs,
                                     100, 5000, 10,
                                     "How often failover partners announce each other. Shorter = faster takeover detection, slightly more LAN traffic.",
                                     500);

        changed |= FormInputs.AddInt("Timeout (× heartbeat)",
                                     ref config.FailoverTimeoutFactor,
                                     2, 20, 1,
                                     "Heartbeats that may be missed before the Standby promotes itself. 3 × 500 ms ≈ 1.5 s takeover.",
                                     3);

        changed |= FormInputs.AddCheckBox("Preempt on master return",
                                          ref config.FailoverPreempt,
                                          """
                                          When the higher-priority machine comes back, it reclaims ACTIVE through a brief
                                          handover (both sides mute output for one heartbeat window). Turn off to let the
                                          backup keep streaming until it dies.
                                          """,
                                          true);

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Network (DrawNetworkInterface draws its own section header)
        // -------------------------------------------------------------------------------------
        DrawNetworkInterface(ref changed);

        FormInputs.AddVerticalSpace();

        CustomComponents.HelpText("""
                                  How to set up a redundant pair:
                                  1. Run the same project and composition on both machines (boygrouping or manual copies).
                                  2. Enable failover on both, same Show Id, master gets the higher Priority.
                                  3. Configure your Artnet/sACN/stream outputs identically on both.
                                  The Standby machine renders silently and starts streaming within the timeout when the
                                  ACTIVE machine stops answering. Heartbeats use multicast so both machines can also be
                                  rehearsed on a single PC.
                                  """);
    }
}
