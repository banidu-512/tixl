#nullable enable
using ImGuiNET;
using System.Net;
using System.Net.Sockets;
using T3.Core.DataTypes.Vector;
using T3.Core.Show;
using T3.Core.Utils;
using T3.Editor.Collab;
using T3.Editor.Gui.Styling;
using T3.Editor.Show;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui;

/// <summary>
/// The compact show-control indicator drawn centered in the menu bar: one short role word per
/// active feature (Master, Slave, Host, Joined, Active, Standby). Hovering a segment opens a
/// detail tooltip — mode, addresses, group/room, peers — in the style of the GPU memory monitor.
/// Nothing is drawn when no show-control feature is active.
/// </summary>
internal static class ShowControlBarStatus
{
    private readonly record struct Segment(string Label, Color Color, string[] DetailLines);

    private static readonly List<string> _peerAddresses = [];

    /// <summary>Draws the indicator centered in the menu bar. Called from <see cref="AppMenuBar"/>.</summary>
    public static void DrawMenuBarIndicator()
    {
        var segments = CollectSegments();
        if (segments.Count == 0)
            return;

        var spacing = 14 * T3Ui.UiScaleFactor;
        var totalWidth = -spacing;
        foreach (var segment in segments)
            totalWidth += ImGui.CalcTextSize(segment.Label).X + spacing;

        ImGui.SameLine(0, 14 * T3Ui.UiScaleFactor);
        var startX = (ImGui.GetWindowWidth() - totalWidth) * 0.5f;
        if (startX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(startX);

        var pad = new System.Numerics.Vector2(8, 3) * T3Ui.UiScaleFactor;
        foreach (var segment in segments)
        {
            var size = ImGui.CalcTextSize(segment.Label) + pad * 2;
            var pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();

            // Subtle tinted pill behind the role word; the word itself in the status color.
            drawList.AddRectFilled(pos, pos + size,
                                   ImGui.ColorConvertFloat4ToU32(segment.Color.Fade(0.18f).Rgba),
                                   4 * T3Ui.UiScaleFactor);
            drawList.AddText(pos + pad,
                             ImGui.ColorConvertFloat4ToU32(segment.Color.Rgba),
                             segment.Label);

            ImGui.InvisibleButton(segment.Label, size);
            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(360);
                foreach (var line in segment.DetailLines)
                {
                    if (string.IsNullOrEmpty(line))
                        ImGui.TextUnformatted("");
                    else
                        ImGui.TextUnformatted(line);
                }

                CustomComponents.EndTooltip();
            }

            ImGui.SameLine(0, spacing);
        }
    }

    private static List<Segment> CollectSegments()
    {
        var segments = new List<Segment>(3);

        // Failover first: when enabled it owns the streaming role.
        if (FailoverArbiter.IsEnabled)
        {
            var role = FailoverArbiter.RoleText;
            _peerAddresses.Clear();
            FailoverArbiter.GetLivePeerAddresses(_peerAddresses);
            var peerAddress = _peerAddresses.Count > 0 ? _peerAddresses[0] : "—";

            // Green = this machine is live; blue = ready and following; magenta = a handover is happening.
            var (label, color) = role switch
                                      {
                                          "ACTIVE (streaming)" => ("Active", UiColors.StatusOkay),
                                          "Standby"            => ("Standby", UiColors.StatusActivated),
                                          "Handover"           => ("Handover", UiColors.StatusAttention),
                                          "Probing"            => ("Probing", UiColors.TextMuted),
                                          _                    => ("Failover", UiColors.TextMuted),
                                      };

            var config = UserSettings.Config;
            segments.Add(new Segment(label, color,
                                     [
                                         $"Show Failover — {role}",
                                         $"Show: {config.FailoverShowId} · Priority: {config.FailoverPriority}",
                                         $"Peer: {peerAddress}",
                                         FailoverArbiter.PeerSummary,
                                         $"Takeovers so far: {FailoverArbiter.TakeoverCount}",
                                     ]));
        }

        var boygroup = BoygroupManager.Instance;
        if (boygroup.State == BoygroupManager.BoygroupState.Server)
        {
            var ip = LocalAddress(FirstAddress(boygroup.Clients)) ?? "any";
            var detail = new List<string>
                             {
                                 "Boygrouping — Master (server)",
                                 $"Address: {ip} · Port: {UserSettings.Config.BoygroupPort}",
                                 $"Group: {boygroup.GroupName}",
                                 $"Nodes: {boygroup.Clients.Count} connected",
                             };
            foreach (var client in boygroup.Clients)
                detail.Add($"  · {client.Name} ({client.Address}) — {client.StatusText}");

            if (!string.IsNullOrEmpty(boygroup.DeploySummary))
                detail.Add(boygroup.DeploySummary);
            if (boygroup.RestartPending)
                detail.Add("Restart pending on the nodes");

            var masterColor = boygroup.RestartPending
                                  ? UiColors.StatusAttention
                                  : UiColors.StatusActivated;
            segments.Add(new Segment("Master", masterColor, [.. detail]));
        }
        else if (boygroup.State == BoygroupManager.BoygroupState.Client)
        {
            var detail = new List<string>
                             {
                                 "Boygrouping — Slave (render node)",
                                 $"Master: {boygroup.ConnectedMasterAddress ?? "?"}",
                                 $"Group: {boygroup.GroupName}",
                                 boygroup.DeploySummary.Length > 0 ? $"Deploy: {boygroup.DeploySummary}" : "",
                                 boygroup.TimeFollowSummary,
                             };
            if (!string.IsNullOrEmpty(boygroup.ErrorText))
                detail.Add(boygroup.ErrorText);

            // Green once the show files are on the node; magenta while a restart is pending or
            // something went wrong.
            var slaveColor = !string.IsNullOrEmpty(boygroup.ErrorText) || boygroup.RestartPending
                                 ? UiColors.StatusAttention
                                 : boygroup.DeploySummary.Contains("complete", StringComparison.OrdinalIgnoreCase)
                                     ? UiColors.StatusOkay
                                     : UiColors.TextMuted;
            segments.Add(new Segment("Slave", slaveColor, [.. detail]));
        }

        var collab = CollabManager.Instance;
        if (collab.IsEnabled && collab.IsSessionActive)
        {
            if (collab.State == CollabManager.SessionState.Hosting)
            {
                var ip = LocalAddress(FirstAddress(collab.Peers)) ?? "any";
                var detail = new List<string>
                                 {
                                     "Collaboration — Hosting",
                                     collab.RoomCode.Length > 0
                                         ? $"Session ID: {collab.RoomCode} · Relay: {collab.RelayEndpoint}"
                                         : $"Address: {ip} · Port: {UserSettings.Config.CollabPort}",
                                     $"Participants: {collab.Peers.Count}",
                                     $"Signed in as {collab.LocalIdentity.DisplayName}",
                                 };
                var hostColor = string.IsNullOrEmpty(collab.ErrorText)
                                    ? UiColors.StatusActivated
                                    : UiColors.StatusAttention;
                segments.Add(new Segment("Host", hostColor, [.. detail]));
            }
            else
            {
                var host = collab.ConnectedHostAddress;
                var detail = host != null
                                 ? new List<string>
                                       {
                                           "Collaboration — Connected",
                                           $"Host: {host}",
                                           $"Signed in as {collab.LocalIdentity.DisplayName}",
                                       }
                                 : new List<string>
                                       {
                                           "Collaboration — Connected via relay",
                                           $"Room: {UserSettings.Config.CollabLastRelayRoom} · Relay: {collab.RelayEndpoint}",
                                           $"Signed in as {collab.LocalIdentity.DisplayName}",
                                       };
                var joinedColor = string.IsNullOrEmpty(collab.ErrorText)
                                      ? UiColors.StatusOkay
                                      : UiColors.StatusAttention;
                segments.Add(new Segment("Joined", joinedColor, [.. detail]));
            }
        }

        return segments;
    }

    private static string? FirstAddress(IReadOnlyList<CollabPeerConnection> peers) =>
        peers.Count > 0 ? peers[0].RemoteAddress?.ToString() : null;

    private static string? FirstAddress(IReadOnlyList<BoygroupManager.ClientInfo> clients) =>
        clients.Count > 0 ? clients[0].Peer.RemoteAddress?.ToString() : null;

    /// <summary>
    /// The address the serving side is reached on: the pinned adapter when set (Settings →
    /// Network), otherwise the local interface a UDP probe toward the first connected peer would
    /// use — the same route the real show traffic takes.
    /// </summary>
    private static string? LocalAddress(string? towardAddress)
    {
        var pinned = LanInterfaces.ResolveAddress(UserSettings.Config.NetworkInterface)?.ToString();
        if (!string.IsNullOrEmpty(pinned))
            return pinned;

        if (!string.IsNullOrEmpty(towardAddress) && IPAddress.TryParse(towardAddress, out var target))
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(target, 9);
                return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
            }
            catch
            {
                // No route to that peer; the detail tooltip then shows "any".
            }
        }

        return null;
    }
}
