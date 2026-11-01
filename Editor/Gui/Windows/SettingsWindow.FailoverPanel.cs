#nullable enable
using ImGuiNET;
using T3.Core.Show;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Show;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Failover" panel of the settings window: active/standby output arbitration for show-critical
/// network outputs (Artnet, sACN, NDI/Spout). Pair this with boygrouping or manual sync so both
/// machines run the same composition — the arbiter only decides who streams.
/// </summary>
internal sealed partial class SettingsWindow
{
    private const int SimulatedCrashSeconds = 10;

    private static void DrawFailoverPanel(ref bool changed)
    {
        var config = UserSettings.Config;

        changed |= FormInputs.AddCheckBox("Enable",
                                          ref config.FailoverEnabled,
                                          """
                                          Two machines run the same composition; exactly one (ACTIVE) streams network
                                          outputs. The active machine also hosts a collaboration session and the
                                          standby joins it automatically, so both stay on the same show state. If
                                          the active node dies, the Standby takes over automatically and streams until
                                          the master returns — then the master streams again and the backup steps
                                          down and follows. Artnet, sACN and stream outputs (NDI/Spout) honor this;
                                          local display windows keep rendering on both machines.
                                          """,
                                          false);

        if (!config.FailoverEnabled)
            return;

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

        changed |= FormInputs.AddInt("Heartbeat interval (ms)",
                                     ref config.FailoverHeartbeatMs,
                                     100, 5000, 10,
                                     "How often failover partners announce each other. Shorter = faster takeover detection, slightly more LAN traffic.",
                                     500);

        changed |= FormInputs.AddInt("Timeout (× heartbeat)",
                                     ref config.FailoverTimeoutFactor,
                                     2, 20, 1,
                                     "Heartbeats that may be missed before the Standby promotes itself. 3 × 100 ms ≈ 0.3 s takeover.",
                                     3);

        changed |= FormInputs.AddInt("Stall window (ms)",
                                     ref config.FailoverStallMs,
                                     100, 5000, 50,
                                     """
                                     The active machine steps down when it has not rendered a frame for this long:
                                     a frozen render is an output fault, even while the process is still alive.
                                     This is the instant path; the timeout above only covers real silence.
                                     """,
                                     300);

        if (ImGui.Button("Use fast timing (100 ms × 3 ≈ 0.3 s)"))
        {
            config.FailoverHeartbeatMs = 100;
            config.FailoverTimeoutFactor = 3;
            changed = true;
        }

        changed |= FormInputs.AddCheckBox("Fence outputs when the standby goes silent",
                                          ref config.FailoverFencing,
                                          """
                                          The active machine mutes before the standby's timeout expires if the standby
                                          stops acknowledging heartbeats. This is what makes fast takeovers safe: a
                                          network partition can never leave two masters on the same DMX universe.
                                          A dead standby also mutes the active machine — use "Resume solo" below when
                                          the backup is known to be gone.
                                          """,
                                          true);

        changed |= FormInputs.AddCheckBox("Preempt on master return",
                                          ref config.FailoverPreempt,
                                          """
                                          When the higher-priority machine comes back, it reclaims ACTIVE through a brief
                                          handover (both sides mute output for one heartbeat window). Turn off to let the
                                          backup keep streaming until it dies.
                                          """,
                                          true);

        FormInputs.AddVerticalSpace();
        DrawNetworkInterface(ref changed);

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Status");
        FormInputs.AddVerticalSpace();

        var roleColor = FailoverArbiter.RoleText.StartsWith("ACTIVE")
                            ? UiColors.StatusActivated
                            : FailoverArbiter.RoleText is "Standby" or "Handover"
                                ? UiColors.StatusWarning
                                : FailoverArbiter.RoleText.StartsWith("FAULT") || FailoverArbiter.RoleText.StartsWith("FENCED")
                                    ? UiColors.StatusError
                                    : UiColors.Text;

        ImGui.PushStyleColor(ImGuiCol.Text, roleColor.Rgba);
        ImGui.Bullet();
        ImGui.TextUnformatted($"This machine: {FailoverArbiter.RoleText}");
        ImGui.PopStyleColor();

        ImGui.Bullet();
        ImGui.TextUnformatted($"Health: {FailoverArbiter.HealthText}");

        ImGui.Bullet();
        ImGui.TextUnformatted($"Lease: {FailoverArbiter.LeaseText}");

        var role = FailoverArbiter.Role;
        if (role is FailoverArbiter.NodeRole.Active or FailoverArbiter.NodeRole.Contested)
        {
            if (ImGui.Button("Step down now"))
                FailoverArbiter.RequestStepDown("settings panel");
        }
        else if (role == FailoverArbiter.NodeRole.Standby)
        {
            if (ImGui.Button("Take over now"))
                FailoverArbiter.RequestTakeover("settings panel");
        }
        else if (role == FailoverArbiter.NodeRole.Fenced)
        {
            if (ImGui.Button("Resume solo (override)"))
                FailoverArbiter.RequestResumeSolo("operator override");
        }

        if (FailoverArbiter.IsSimulatingCrash)
        {
            ImGui.TextDisabled($"Simulated crash, back in {(FailoverArbiter.SimulatedCrashRemainingMs + 999) / 1000} s");
        }
        else if (ImGui.Button($"Simulate Crash ({SimulatedCrashSeconds} s)"))
        {
            FailoverArbiter.SimulateCrash(SimulatedCrashSeconds);
        }

        CustomComponents.TooltipForLastItem("""
                                            Makes this machine act like a crashed process: no heartbeats, no goodbye,
                                            the collaboration session drops and all network output stops. The partner
                                            has to notice on its own. Afterwards this machine rejoins as if freshly started.
                                            """);

        ImGui.Bullet();
        ImGui.TextUnformatted($"Sync: {FailoverCollabLink.Instance.StatusText}");

        ImGui.Bullet();
        ImGui.TextUnformatted(FailoverArbiter.PeerSummary);

        ImGui.Bullet();
        ImGui.TextUnformatted($"Takeovers performed: {FailoverArbiter.TakeoverCount}");

        if (!string.IsNullOrEmpty(FailoverArbiter.LastEvent))
        {
            FormInputs.AddVerticalSpace();
            ImGui.TextDisabled("Recent events:");
            foreach (var failoverEvent in FailoverArbiter.RecentEvents)
            {
                ImGui.TextDisabled($"  {failoverEvent}");
            }
        }

        FormInputs.AddVerticalSpace();
        CustomComponents.HelpText("""
                                  How to set up a redundant pair:
                                  1. Enable failover on both machines, same Show Id; give the preferred master the
                                     higher Priority (e.g. 100 vs 50).
                                  2. That's it: the ACTIVE machine hosts a collaboration session and the Standby
                                     joins it automatically — use the same collab port and password on both.
                                     Edits on the master reach the backup live, so a takeover continues the same
                                     show. When the master returns it reclaims the output (preempt) and the
                                     backup steps down and follows it again.
                                  3. Configure your Artnet/sACN/stream outputs identically on both.
                                  The active machine reacts instantly to its own faults: a frozen render or a failing
                                  DMX send steps it down and the standby promotes in one frame. Pure silence is
                                  covered by the timeout, and the lease mutes the old master before that timeout
                                  expires — so a takeover can never leave two masters on the same universe.
                                  Heartbeats use multicast so both machines can also be rehearsed on a single PC,
                                  and the collaboration session carries a second health channel so a broken
                                  multicast path alone never causes a takeover.
                                  Transient problems heal themselves: a faulted node rejoins after ~2 s, a fenced
                                  master resumes when acknowledgements return, and heartbeat sockets are re-bound
                                  when an adapter is replugged or re-addressed.
                                  Timeline playback (play/pause, position) is not synced by the session — start
                                  playback on the backup too, or keep playing on both.
                                  """);
    }
}
