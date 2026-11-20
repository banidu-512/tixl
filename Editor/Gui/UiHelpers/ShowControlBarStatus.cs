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

    /// <summary>
    /// Draws the mode selector centered in the menu bar: one pill naming the current show-control
    /// mode and the machine's role in it. Clicking opens the mode menu (Off / Boygrouping /
    /// Failover / Collaboration — mutually exclusive); hovering shows the live details.
    /// </summary>
    public static void DrawMenuBarIndicator()
    {
        var mode = ShowControlMode.Current;
        var role = ShowControlMode.RoleLabel;
        var label = mode == ShowControlMode.Off
                        ? "Editor mode"
                        : role.Length > 0
                            ? $"{ModeDisplayName(mode)} · {role}"
                            : ModeDisplayName(mode);

        var (color, detailLines) = DescribeCurrent(mode);

        var pad = new System.Numerics.Vector2(8, 3) * T3Ui.UiScaleFactor;
        var size = ImGui.CalcTextSize(label) + pad * 2;
        var totalWidth = size.X;
        ImGui.SameLine(0, 14 * T3Ui.UiScaleFactor);
        var startX = (ImGui.GetWindowWidth() - totalWidth) * 0.5f;
        if (startX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(startX);

        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Subtle tinted pill behind the mode word; the word itself in the status color.
        drawList.AddRectFilled(pos, pos + size,
                               ImGui.ColorConvertFloat4ToU32(color.Fade(0.18f).Rgba),
                               4 * T3Ui.UiScaleFactor);
        drawList.AddText(pos + pad,
                         ImGui.ColorConvertFloat4ToU32(color.Rgba),
                         label);

        ImGui.InvisibleButton("##showMode", size);
        if (ImGui.IsItemHovered() && detailLines.Length > 0)
        {
            CustomComponents.BeginTooltip(360);
            foreach (var line in detailLines)
            {
                if (string.IsNullOrEmpty(line))
                    ImGui.TextUnformatted("");
                else
                    ImGui.TextUnformatted(line);
            }

            CustomComponents.EndTooltip();
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            ImGui.OpenPopup("##showModeMenu");

        var modeMenuOpen = ImGui.BeginPopup("##showModeMenu");
        if (!modeMenuOpen)
        {
            DrawJoinRoomModal();
            return;
        }

        ImGui.TextDisabled("Show-control mode - one at a time");
        ImGui.Separator();

        // Menu clicks only record what was chosen. Switching inside the open menu would tear down a
        // live session - and with it whatever window is mid-render this frame - while ImGui still
        // holds the menu's window stack, which left the frame's ID/style stacks unbalanced.
        string? requestedMode = null;
        bool? requestedMaster = null;
        var requestedJoinLast = false;
        var requestedJoinDialog = false;

        // Plain editing: no show-control feature runs.
        if (ImGui.Selectable("Editor mode", mode == ShowControlMode.Off))
            requestedMode = ShowControlMode.Off;

        if (ImGui.IsItemHovered())
        {
            CustomComponents.BeginTooltip(300);
            ImGui.TextWrapped("No show-control feature runs on this machine.");
            CustomComponents.EndTooltip();
        }

        // Boygrouping carries a role: pick master or slave right here.
        if (ImGui.BeginMenu("Boygroup"))
        {
            var isMaster = UserSettings.Config.BoygroupIsMaster;
            if (ImGui.Selectable("as Master", mode == ShowControlMode.Boygroup && isMaster))
            {
                requestedMode = ShowControlMode.Boygroup;
                requestedMaster = true;
            }

            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Serve the show: deploy files and live state to the render nodes and drive playback.");
                CustomComponents.EndTooltip();
            }

            if (ImGui.Selectable("as Slave", mode == ShowControlMode.Boygroup && !isMaster))
            {
                requestedMode = ShowControlMode.Boygroup;
                requestedMaster = false;
            }

            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Render what the master sends: join (or wait for) a master and follow its clock.");
                CustomComponents.EndTooltip();
            }

            ImGui.EndMenu();
        }

        // Failover carries a preference like boygrouping's role: Main wins the election, Backup
        // takes over when Main dies. The arbiter still decides who streams at runtime.
        if (ImGui.BeginMenu("Failover"))
        {
            const int mainPriority = 100;
            const int backupPriority = 50;

            if (ImGui.Selectable("as Main", mode == ShowControlMode.Failover && UserSettings.Config.FailoverPriority > backupPriority))
                requestedMode = ShowControlMode.Failover;

            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Preferred active machine: streams the show as long as it is alive (priority 100).");
                CustomComponents.EndTooltip();
            }

            if (ImGui.Selectable("as Backup", mode == ShowControlMode.Failover && UserSettings.Config.FailoverPriority <= backupPriority))
                requestedMode = ShowControlMode.Failover + ":backup";

            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Standby machine: renders silently and takes over automatically when Main dies (priority 50).");
                CustomComponents.EndTooltip();
            }

            ImGui.EndMenu();
        }

        // Collaboration carries a choice like boygrouping's role: host a session or join one.
        if (ImGui.BeginMenu("Collab"))
        {
            if (ImGui.Selectable("Host session", mode == ShowControlMode.Collab))
                requestedMode = ShowControlMode.Collab;

            if (ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Start a session and share the session ID.");
                CustomComponents.EndTooltip();
            }

            var lastRoom = UserSettings.Config.CollabLastRelayRoom;
            if (!string.IsNullOrEmpty(lastRoom) && ImGui.Selectable($"Join last session ({lastRoom})", false))
                requestedJoinLast = true;

            if (!string.IsNullOrEmpty(lastRoom) && ImGui.IsItemHovered())
            {
                CustomComponents.BeginTooltip(300);
                ImGui.TextWrapped("Rejoin the session this machine joined last.");
                CustomComponents.EndTooltip();
            }

            if (ImGui.Selectable("Join session..."))
                requestedJoinDialog = true;

            ImGui.EndMenu();
        }

        ImGui.EndPopup();

        // Everything happens here, with the menu stack closed again.
        if (requestedMode != null)
            ApplyModeRequest(requestedMode, requestedMaster);

        if (requestedJoinLast)
            JoinLastSession();

        if (requestedJoinDialog)
            _openJoinRoomModal = true;

        DrawJoinRoomModal();
    }

    /// <summary>Applies a menu choice once the menu is closed. "Failover:backup" carries the Backup preference.</summary>
    private static void ApplyModeRequest(string requestedMode, bool? requestedMaster)
    {
        const int mainPriority = 100;
        const int backupPriority = 50;

        var mode = requestedMode;
        if (mode.StartsWith(ShowControlMode.Failover + ":", StringComparison.Ordinal))
        {
            mode = ShowControlMode.Failover;
            UserSettings.Config.FailoverPriority = requestedMode.EndsWith(":backup", StringComparison.Ordinal) ? backupPriority : mainPriority;
            UserSettings.Save();
        }

        if (requestedMaster is { } isMaster)
            ShowControlMode.SetBoygroupRole(isMaster);

        if (mode == ShowControlMode.Boygroup && ShowControlMode.Current == ShowControlMode.Boygroup && requestedMaster != null)
            return; // The role switch already re-applied the session.

        var error = ShowControlMode.SwitchTo(mode);
        if (error != null)
            Log.Warning($"Show mode: {error}");
    }

    private static void JoinLastSession()
    {
        var lastRoom = UserSettings.Config.CollabLastRelayRoom;
        if (string.IsNullOrEmpty(lastRoom))
            return;

        ShowControlMode.Activate(ShowControlMode.Collab);
        var (host, port) = ParseRelayEndpoint(UserSettings.Config.CollabLastRelayHost);
        CollabManager.Instance.JoinViaRelay(host, port, lastRoom, UserSettings.Config.CollabPassword ?? "");
    }

    /// <summary>The join modal lives at menu-bar id scope, outside the mode popup.</summary>
    private static void DrawJoinRoomModal()
    {
        if (_openJoinRoomModal)
        {
            _openJoinRoomModal = false;
            ImGui.OpenPopup("Join session");
        }

    var joinCenter = ImGui.GetWindowWidth() * 0.5f;
    ImGui.SetNextWindowPos(ImGui.GetWindowPos() + new System.Numerics.Vector2(joinCenter - 140, ImGui.GetFrameHeight() + 8),
                           ImGuiCond.Appearing);
    if (ImGui.BeginPopupModal("Join session", ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.TextDisabled("Enter the session ID the host shared, or paste an invite.");
        _joinRoomInput ??= "";
        var submit = ImGui.InputText("##roomCode", ref _joinRoomInput, 12,
                                     ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.CharsNoBlank);
        if (ImGui.Button("Paste"))
        {
            var clipboard = ImGui.GetClipboardText();
            if (TryParseInvite(clipboard, out var inviteRelay, out var inviteRoom))
            {
                if (!string.IsNullOrEmpty(inviteRelay))
                    UserSettings.Config.CollabRelayAddress = inviteRelay;

                _joinRoomInput = inviteRoom;
            }
            else
            {
                _joinRoomInput = clipboard?.Trim() ?? "";
            }
        }

        CustomComponents.TooltipForLastItem("Reads a copied invite (relay + session ID) or a plain session ID from the clipboard.");

        if (ImGui.Button("Join") || submit)
        {
            var room = _joinRoomInput.Trim();
            if (room.Length > 0)
            {
                ShowControlMode.Activate(ShowControlMode.Collab);
                var (host, port) = ParseRelayEndpoint(UserSettings.Config.CollabRelayAddress);
                CollabManager.Instance.JoinViaRelay(host, port, room, UserSettings.Config.CollabPassword ?? "");
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
    }

    /// <summary>Reads a copied collab invite ("Relay:" and "Room:" lines); the relay line is optional.</summary>
    private static bool TryParseInvite(string? text, out string relay, out string room)
    {
        relay = "";
        room = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Relay:", StringComparison.OrdinalIgnoreCase))
                relay = trimmed[6..].Trim();
            else if (trimmed.StartsWith("Room:", StringComparison.OrdinalIgnoreCase))
                room = trimmed[5..].Trim();
        }

        return !string.IsNullOrEmpty(room);
    }

    private static bool _openJoinRoomModal;
    private static string _joinRoomInput;

    private static (string Host, int Port) ParseRelayEndpoint(string? configured)
    {
        var text = configured?.Trim();
        if (string.IsNullOrEmpty(text))
            return (CollabProtocol.DefaultRelayHost, CollabProtocol.DefaultRelayPort);

        var separator = text.LastIndexOf(':');
        if (separator > 0 && int.TryParse(text[(separator + 1)..], out var port))
            return (text[..separator], port);

        return (text, CollabProtocol.DefaultRelayPort);
    }

    private static string ModeDisplayName(string mode) => mode switch
                                                             {
                                                                 ShowControlMode.Boygroup => "Boygroup",
                                                                 ShowControlMode.Failover => "Failover",
                                                                 ShowControlMode.Collab   => "Collab",
                                                                 _                        => "Editor mode",
                                                             };

    /// <summary>Status color and hover detail for the current mode; falls back to the pill word when nothing runs.</summary>
    private static (Color Color, string[] Lines) DescribeCurrent(string mode)
    {
        var segments = CollectSegments();
        if (segments.Count > 0)
            return (segments[0].Color, segments[0].DetailLines);

        return (UiColors.TextMuted, mode == ShowControlMode.Off
                                         ? []
                                         : [$"{ModeDisplayName(mode)} is enabled, but no session is running yet."]);
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
