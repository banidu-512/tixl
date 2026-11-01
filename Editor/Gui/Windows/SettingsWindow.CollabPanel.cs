#nullable enable
using ImGuiNET;
using Newtonsoft.Json.Linq;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Collaboration" panel of the settings window: identity (Discord login button), session
/// controls (host / join with LAN discovery or direct address), and the participant list.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly LanDiscoveryScanner _discoveryScanner = new();

    private static void DrawCollabPanel(ref bool changed)
    {
        var manager = CollabManager.Instance;
        var config = UserSettings.Config;

        // A pending confirmation must not survive into the next session.
        if (!manager.IsSessionActive)
            _pendingKickPeerId = -1;

        // -------------------------------------------------------------------------------------
        // Identity
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Identity");
        FormInputs.AddVerticalSpace();

        DrawDiscordLoginButton();

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Session
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Session");
        FormInputs.AddVerticalSpace();

        changed |= DrawSecretInput("Session password", ref config.CollabPassword,
                                   "Empty = anyone who knows the session ID can join. Recommended for sessions with an open ID.");

        FormInputs.AddVerticalSpace();

        if (manager.IsSessionActive)
        {
            DrawActiveSession(manager);
        }
        else
        {
            DrawHostAndJoinControls(manager, ref changed);
        }

        if (!string.IsNullOrEmpty(manager.ErrorText))
        {
            FormInputs.AddVerticalSpace();
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
            ImGui.TextWrapped(manager.ErrorText);
            ImGui.PopStyleColor();
        }

        FormInputs.AddVerticalSpace();
        DrawAdvancedConnectionSettings(manager, ref changed);
    }

    // ---------------------------------------------------------------------------------------
    // Hosting / joining
    // ---------------------------------------------------------------------------------------

    /// <summary>Start a session to get a session ID, or enter someone else's ID to join.</summary>
    private static void DrawHostAndJoinControls(CollabManager manager, ref bool changed)
    {
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Host");
        DrawShareProjectChoice(ref changed);
        FormInputs.AddVerticalSpace();

        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Start Session", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            ParseRelayAddress(config.CollabRelayAddress, out var host, out var port);
            manager.StartHostViaRelay(host, port, CollabManager.ConfiguredShareProject);
        }

        CustomComponents.TooltipForLastItem("Start a session and get a session ID. Share the ID with the people you want to collaborate with. You keep full control: every edit from participants is applied to your graph first and then distributed.");

        FormInputs.AddVerticalSpace();

        FormInputs.AddSectionSubHeader("Join");
        _roomCode ??= "";
        FormInputs.DrawInputLabel("Session ID");
        ImGui.PushID("##collabroom");
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, true).X * 0.5f);
        var submitted = ImGui.InputText("##collabRoomCode", ref _roomCode, 12,
                                        ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.CharsNoBlank);
        ImGui.PopID();
        ImGui.SameLine();
        if (ImGui.Button("Join Session") || submitted)
        {
            ParseRelayAddress(config.CollabRelayAddress, out var host, out var port);
            manager.JoinViaRelay(host, port, _roomCode, config.CollabPassword);
        }

        CustomComponents.TooltipForLastItem("Join the session with the ID the host shared.");

        FormInputs.AddVerticalSpace(5);
        FormInputs.SetCursorToParameterEdit();
        DrawPasteInviteButton(manager, ref changed);
    }

    /// <summary>Reads a copied invite and joins it, adopting the relay address the invite carries.</summary>
    private static void DrawPasteInviteButton(CollabManager manager, ref bool changed)
    {
        var config = UserSettings.Config;
        if (ImGui.Button("Paste Invite and Join"))
        {
            if (TryParseInvite(ImGui.GetClipboardText(), out var relayAddress, out var roomCode))
            {
                if (!string.IsNullOrEmpty(relayAddress))
                {
                    config.CollabRelayAddress = relayAddress;
                    changed = true;
                }

                _roomCode = roomCode;
                _inviteMessage = "";
                ParseRelayAddress(config.CollabRelayAddress, out var host, out var port);
                manager.JoinViaRelay(host, port, roomCode, config.CollabPassword);
            }
            else
            {
                _inviteMessage = "The clipboard does not contain a TiXL collab invite.";
            }
        }

        CustomComponents.TooltipForLastItem("Reads the relay address and session ID from a copied invite, then joins.");

        if (!string.IsNullOrEmpty(_inviteMessage))
        {
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextDisabled(_inviteMessage);
        }
    }

    /// <summary>
    /// Everything the session ID flow does not need: a custom relay, and direct or LAN connections
    /// for setups without a relay.
    /// </summary>
    private static void DrawAdvancedConnectionSettings(CollabManager manager, ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        var config = UserSettings.Config;

        FormInputs.AddVerticalSpace(5);
        FormInputs.AddSectionSubHeader("Relay");
        changed |= FormInputs.AddStringInput("Relay server",
                                             ref config.CollabRelayAddress,
                                             "relay.example.com:44710",
                                             null,
                                             """
                                             Address of a TiXLCollabRelay server, e.g. relay.example.com:44710.
                                             Hosts and joiners must use the same relay. Empty uses the default relay.
                                             """);

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Peer-to-Peer");
        changed |= FormInputs.AddCheckBox("Use peer-to-peer connections",
                                          ref config.CollabWebRtcEnabled,
                                          """
                                          Relay sessions first authenticate over the relay, then try a direct WebRTC path
                                          between the machines. The session falls back to the relay when a direct path is
                                          not possible, so this can stay on everywhere.
                                          """,
                                          true);

        changed |= FormInputs.AddCheckBox("Share preview with participants",
                                          ref config.CollabSharePreview,
                                          """
                                          Streams the host's rendered output to the participants over the
                                          peer-to-peer video track. Applies to peers that join afterwards.
                                          """,
                                          true);

        changed |= FormInputs.AddStringInput("STUN/TURN servers",
                                             ref config.CollabWebRtcIceServers,
                                             "stun:stun.l.google.com:19302",
                                             null,
                                             """
                                             Comma-separated STUN/TURN URLs used to establish the direct path.
                                             Empty uses a public STUN server. A TURN server is only needed
                                             between peers that cannot reach each other directly.
                                             """);

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Direct and LAN");
        changed |= FormInputs.AddInt("Port",
                                     ref config.CollabPort,
                                     1024, 65535, 1,
                                     "TCP port used by direct and LAN sessions. Pick something above 1024 that is not used by other software.",
                                     CollabProtocol.DefaultTcpPort);

        changed |= FormInputs.AddCheckBox("Announce session on LAN",
                                          ref config.CollabLanDiscovery,
                                          """
                                          Lets other TiXL instances on the local network discover a LAN session
                                          automatically. Turn off when hosting on a shared network.
                                          """,
                                          true);

        FormInputs.AddVerticalSpace();
        DrawNetworkInterface(ref changed);

        if (manager.IsSessionActive)
            return;

        FormInputs.AddVerticalSpace();
        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Host on LAN", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery, CollabManager.ConfiguredShareProject);
        }

        CustomComponents.TooltipForLastItem("Start a session on this machine without a relay. Others join by LAN discovery or by your address.");

        FormInputs.AddVerticalSpace();

        _joinAddress ??= "";
        FormInputs.DrawInputLabel("Address");
        ImGui.PushID("##collabjoin");
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, true).X);
        if (ImGui.InputText("##collabJoinAddress", ref _joinAddress, 256, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ParseJoinAddress(_joinAddress, config.CollabPort, out var host, out var port);
            manager.JoinDirect(host, port, config.CollabPassword);
        }

        ImGui.PopID();
        CustomComponents.TooltipForLastItem("Direct address of the host, e.g. 192.168.1.42 for LAN sessions or a public address / VPN IP for online sessions.");
        ImGui.SameLine();
        if (ImGui.Button("Connect"))
        {
            ParseJoinAddress(_joinAddress, config.CollabPort, out var host, out var port);
            manager.JoinDirect(host, port, config.CollabPassword);
        }

        CustomComponents.TooltipForLastItem("Join the host at the address above.");

        DrawDiscoveredSessions(manager);
    }

    private static string _joinAddress = "";
    private static string _roomCode = "";
    private static string _inviteMessage = "";

    private const string CurrentProjectLabel = "Current project";
    private static readonly List<string> _shareProjectChoices = [];

    /// <summary>
    /// Which project the host shares. The focused project is the default; selecting another one is
    /// persisted and opened when hosting starts, so a session can be prepared from any project tab.
    /// </summary>
    private static void DrawShareProjectChoice(ref bool changed)
    {
        var config = UserSettings.Config;

        _shareProjectChoices.Clear();
        _shareProjectChoices.Add(CurrentProjectLabel);
        foreach (var project in EditableSymbolProject.AllProjects)
        {
            var name = project.DisplayName;
            if (!string.IsNullOrEmpty(name) && !_shareProjectChoices.Contains(name))
                _shareProjectChoices.Add(name);
        }

        var selected = string.IsNullOrEmpty(config.CollabShareProject) ? CurrentProjectLabel : config.CollabShareProject;
        if (!_shareProjectChoices.Contains(selected))
            _shareProjectChoices.Add(selected); // The saved project is not loaded right now — keep it readable.

        // AddDropdown uses a fixed internal combo id; the Network section above has one in the same
        // panel, so scope this one to keep the two apart.
        ImGui.PushID("##shareProject");
        var dropdownChanged = FormInputs.AddDropdown(ref selected, _shareProjectChoices, "Share project",
                                    """
                                    The project participants receive and edit together.
                                    'Current project' follows whichever project is focused; choosing another one opens it when hosting starts.
                                    """);
        ImGui.PopID();

        if (!dropdownChanged)
            return;

        config.CollabShareProject = selected == CurrentProjectLabel ? "" : selected;
        changed = true;
    }

    private static void ParseRelayAddress(string text, out string host, out int port)
    {
        host = string.IsNullOrWhiteSpace(text) ? CollabProtocol.DefaultRelayHost : text.Trim();
        port = CollabProtocol.DefaultRelayPort;
        if (string.IsNullOrEmpty(host))
        {
            host = "127.0.0.1";
            return;
        }

        var separator = host.LastIndexOf(':');
        if (separator > 0 && int.TryParse(host[(separator + 1)..], out var parsed))
        {
            port = parsed;
            host = host[..separator];
        }
    }

    /// <summary>The configured relay, or the default relay when the field is empty.</summary>
    private static string RelayAddressOrDefault()
    {
        var relay = UserSettings.Config.CollabRelayAddress;
        if (!string.IsNullOrWhiteSpace(relay))
            return relay.Trim();

        var host = string.IsNullOrEmpty(CollabProtocol.DefaultRelayHost) ? "127.0.0.1" : CollabProtocol.DefaultRelayHost;
        return $"{host}:{CollabProtocol.DefaultRelayPort}";
    }

    /// <summary>One clipboard string carrying both things a joiner needs.</summary>
    private static string BuildInvite(CollabManager manager)
    {
        var relay = string.IsNullOrEmpty(manager.RelayEndpoint) ? RelayAddressOrDefault() : manager.RelayEndpoint;
        return $"TiXL collab invite\nRelay: {relay}\nRoom: {manager.RoomCode}";
    }

    /// <summary>Reads an invite copied by <see cref="BuildInvite"/>; the relay line is optional.</summary>
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

    private static void ParseJoinAddress(string text, int defaultPort, out string host, out int port)
    {
        host = text?.Trim() ?? "";
        port = defaultPort;
        var separator = host.LastIndexOf(':');
        if (separator > 0 && int.TryParse(host[(separator + 1)..], out var parsed))
        {
            port = parsed;
            host = host[..separator];
        }
    }

    private static void DrawDiscoveredSessions(CollabManager manager)
    {
        var config = UserSettings.Config;
        FormInputs.AddVerticalSpace();
        FormInputs.DrawInputLabel("Sessions on LAN");

        if (!_discoveryScannerActive)
        {
            if (ImGui.SmallButton("Scan"))
            {
                _discovered.Clear();
                _discoveryScanner.Start(config.CollabPort + LanDiscoveryResponder.DiscoveryPortOffset,
                                        config.NetworkInterface);
                _discoveryScannerActive = true;
                _discoveryScanUntil = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            }
        }
        else
        {
            ImGui.TextDisabled("scanning...");
        }

        DrainDiscoveryResults();

        if (_discovered.Count == 0)
            return;

        FormInputs.AddVerticalSpace(5);
        foreach (var session in _discovered)
        {
            FormInputs.SetCursorToParameterEdit();
            ImGui.Bullet();
            ImGui.TextUnformatted($"{session.Name}  ({session.Address}:{session.Port})");
            if (session.PasswordProtected)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("password");
            }

            ImGui.SameLine();
            ImGui.PushID(session.Address + session.Port);
            if (ImGui.SmallButton("Join"))
            {
                manager.JoinDirect(session.Address, session.Port, config.CollabPassword);
            }

            ImGui.PopID();
        }
    }

    private static bool _discoveryScannerActive;
    private static DateTime _discoveryScanUntil;
    private static readonly List<CollabManager.DiscoveredSession> _discovered = [];

    private static void DrainDiscoveryResults()
    {
        while (_discoveryScanner.Results.TryDequeue(out var result))
        {
            var announcement = result.Announcement;
            var session = new CollabManager.DiscoveredSession
                              {
                                  Name = CollabProtocol.GetString(announcement, "name", "TiXL session"),
                                  HostName = CollabProtocol.GetString(announcement, "host", ""),
                                  Address = result.Source.Address.ToString(),
                                  Port = announcement["port"]?.Value<int>() ?? CollabProtocol.DefaultTcpPort,
                                  PasswordProtected = announcement["pwd"]?.Value<bool>() ?? false,
                              };

            if (_discovered.Any(d => d.Address == session.Address && d.Port == session.Port))
                continue;

            _discovered.Add(session);
        }

        if (_discoveryScannerActive && DateTime.UtcNow > _discoveryScanUntil)
        {
            _discoveryScanner.Stop();
            _discoveryScannerActive = false;
        }

        _discovered.RemoveAll(d => d.Seen < DateTime.UtcNow - TimeSpan.FromSeconds(15));
    }

    // ---------------------------------------------------------------------------------------
    // Active session
    // ---------------------------------------------------------------------------------------

    private static void DrawActiveSession(CollabManager manager)
    {
        FormInputs.DrawInputLabel("Status");
        CustomComponents.StylizedText(manager.StatusText, Fonts.FontNormal, UiColors.StatusActivated);

        if (!string.IsNullOrEmpty(manager.RoomCode))
        {
            FormInputs.DrawInputLabel("Session ID");
            ImGui.TextUnformatted(manager.RoomCode);
            CustomComponents.TooltipForLastItem("Share this ID with the people you want to collaborate with.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy ID"))
                ImGui.SetClipboardText(manager.RoomCode);
            CustomComponents.TooltipForLastItem("Copies the session ID.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Invite"))
                ImGui.SetClipboardText(BuildInvite(manager));
            CustomComponents.TooltipForLastItem("Copies the relay address and room code as a single invite. The other person can use \"Paste Invite and Join\" under Join.");
        }

        if (!string.IsNullOrEmpty(manager.SharedProjectName))
        {
            FormInputs.DrawInputLabel("Sharing");
            ImGui.TextDisabled(CollabManager.ShortProjectName(manager.SharedProjectName));
        }

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Participants");
        DrawParticipantTable(manager);

        FormInputs.AddVerticalSpace();
        FormInputs.SetCursorToParameterEdit();
        if (ImGui.Button("Leave Session"))
            manager.Leave(manual: true);

        FormInputs.AddVerticalSpace();
        CustomComponents.HelpText("""
                                  How it works:
                                  - Late joiners receive full snapshots of the compositions edited in the session - their local changes to those compositions are replaced by the host's state.
                                  - Graph edits are synchronized: adding and deleting operators, connections, parameter changes, renames and moving nodes.
                                  - Undo/Redo are session-wide while connected: Ctrl+Z reverts the last edit of ANY participant (the host keeps the shared history).
                                  - Parameters someone is editing are soft-locked: they appear disabled with a "(locked)" marker until released.
                                  - For sessions over the internet, host on a machine with an open/forwarded port, use a VPN like Tailscale, or host via a relay room.
                                  """);

        if (!string.IsNullOrEmpty(manager.UndoNotice))
        {
            FormInputs.AddVerticalSpace();
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
            ImGui.TextUnformatted(manager.UndoNotice);
            ImGui.PopStyleColor();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Participants
    // ---------------------------------------------------------------------------------------

    private static int _pendingKickPeerId = -1;

    private sealed record ParticipantRow(int PeerId, string Name, bool IsSelf, bool IsHost);

    /// <summary>
    /// The session roster as a table: who is here, which path they use, how fast it is and what they
    /// are doing. The host gets a Kick action; there is no ban, so a removed participant can rejoin
    /// unless the session password keeps them out.
    /// </summary>
    private static void DrawParticipantTable(CollabManager manager)
    {
        if (manager.State == CollabManager.SessionState.Hosting && manager.Roster.Count == 0)
        {
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextDisabled("Waiting for participants...");
            return;
        }

        var rows = BuildParticipantRows(manager);
        var isHost = manager.State == CollabManager.SessionState.Hosting;

        const ImGuiTableFlags flags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                                      | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.NoSavedSettings;

        // Size the table to its rows. With the default (remaining space) the table clips its rows
        // in a short Settings window and the panel never scrolls to reveal them.
        var tableHeight = ImGui.GetFrameHeightWithSpacing() * (rows.Count + 1) + 8;

        if (ImGui.BeginTable("collabParticipants", 5, flags, new System.Numerics.Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Participant", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGui.TableSetupColumn("RTT", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
                DrawParticipantRow(manager, row, isHost);

            ImGui.EndTable();
        }

        DrawKickConfirmation(manager, rows);

        FormInputs.AddVerticalSpace(4);
        CustomComponents.HelpText("A kicked participant is disconnected from this session but not banned; they can join again unless the session is password protected.");
    }

    private static List<ParticipantRow> BuildParticipantRows(CollabManager manager)
    {
        var selfName = manager.LocalIdentity.DisplayName;
        if (string.IsNullOrEmpty(selfName))
            selfName = "You";

        // The local row is synthesized: the roster only contains the other participants, and on a
        // client it already contains the host.
        var rows = new List<ParticipantRow>
                       {
                           new(manager.MyPeerId, selfName, true, manager.MyPeerId == 0)
                       };

        foreach (var (peerId, name) in manager.Roster.ToArray())
        {
            if (peerId == manager.MyPeerId)
                continue;

            rows.Add(new ParticipantRow(peerId, name, false, peerId == 0));
        }

        rows.Sort((a, b) =>
                      {
                          if (a.IsHost != b.IsHost)
                              return a.IsHost ? -1 : 1;

                          return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                      });
        return rows;
    }

    private static void DrawParticipantRow(CollabManager manager, ParticipantRow row, bool isHost)
    {
        ImGui.PushID("collabPeer" + row.PeerId);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        var accent = row.IsSelf
                         ? manager.LocalIdentity.AccentColor
                         : manager.GetPresence(row.PeerId)?.AccentColor ?? new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, CollabColors.ToUint(accent));
        ImGui.Bullet();
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextUnformatted(string.IsNullOrEmpty(row.Name) ? "unnamed participant" : row.Name);

        if (row.IsHost)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(host)");
        }

        if (row.IsSelf && !string.Equals(row.Name, "You", StringComparison.Ordinal))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(you)");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.TextDisabled(DescribeParticipantLink(manager, row));

        ImGui.TableSetColumnIndex(2);
        var rtt = row.IsSelf ? -1 : manager.GetPeerRttMs(row.PeerId);
        ImGui.TextDisabled(rtt >= 0 ? $"{rtt} ms" : "—");

        ImGui.TableSetColumnIndex(3);
        ImGui.TextDisabled(DescribeParticipantActivity(manager, row));

        ImGui.TableSetColumnIndex(4);
        if (isHost && !row.IsSelf)
        {
            if (ImGui.SmallButton("Kick"))
                _pendingKickPeerId = row.PeerId;

            CustomComponents.TooltipForLastItem("Remove this participant from the session. They can rejoin unless the session is password protected.");
        }

        ImGui.PopID();
    }

    private static string DescribeParticipantLink(CollabManager manager, ParticipantRow row)
    {
        if (row.IsSelf)
            return "—";

        var link = manager.DescribePeerLink(row.PeerId);
        if (!string.IsNullOrEmpty(link))
            return link;

        if (!HasPeerConnection(manager, row.PeerId))
            return "—";

        return string.IsNullOrEmpty(manager.RelayEndpoint) ? "direct" : "relay";
    }

    private static bool HasPeerConnection(CollabManager manager, int peerId)
    {
        foreach (var peer in manager.Peers)
        {
            if (peer.AssignedId == peerId)
                return true;
        }

        return false;
    }

    private static string DescribeParticipantActivity(CollabManager manager, ParticipantRow row)
    {
        if (row.IsSelf)
            return "—";

        var presence = manager.GetPresence(row.PeerId);
        if (presence == null || presence.CompositionId == Guid.Empty)
            return "—";

        return presence.IsHoveringCanvas ? "editing" : "in session";
    }

    private static void DrawKickConfirmation(CollabManager manager, List<ParticipantRow> rows)
    {
        if (_pendingKickPeerId < 0)
            return;

        ParticipantRow? pending = null;
        foreach (var row in rows)
        {
            if (row.PeerId != _pendingKickPeerId)
                continue;

            pending = row;
            break;
        }

        if (pending == null || pending.IsSelf)
        {
            _pendingKickPeerId = -1;
            return;
        }

        FormInputs.AddVerticalSpace(4);
        ImGui.TextUnformatted($"Remove {pending.Name} from the session?");
        if (ImGui.Button("Kick"))
        {
            manager.KickParticipant(pending.PeerId);
            _pendingKickPeerId = -1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _pendingKickPeerId = -1;
    }

    // ---------------------------------------------------------------------------------------
    // Secret inputs
    // ---------------------------------------------------------------------------------------

    private static bool DrawSecretInput(string label, ref string secret, string tooltip)
    {
        FormInputs.DrawInputLabel(label);

        ImGui.PushID(label);
        var input = secret ?? "";
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(tooltip, false, true).X);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5 * T3Ui.UiScaleFactor);
        var changed = ImGui.InputText("##secret", ref input, 256, ImGuiInputTextFlags.Password);
        ImGui.PopStyleVar();
        FormInputs.AppendTooltip(tooltip);
        ImGui.PopID();

        if (changed)
            secret = input;

        return changed;
    }

    // ---------------------------------------------------------------------------------------
    // Discord
    // ---------------------------------------------------------------------------------------

    private static void DrawDiscordLoginButton()
    {
        var loginState = CollabDiscordAuth.State;
        var loginBusy = loginState is CollabDiscordAuth.LoginState.WaitingForBrowser or CollabDiscordAuth.LoginState.ExchangingCode;

        FormInputs.SetCursorToParameterEdit();
        ImGui.BeginDisabled(loginBusy);
        if (ImGui.Button("Log In with Discord"))
            CollabDiscordAuth.StartLogin();

        ImGui.EndDisabled();
        CustomComponents.TooltipForLastItem("Opens the Discord login in your browser. The account must be a member of the TiXL Discord server.");

        if (loginBusy)
        {
            FormInputs.SetCursorToParameterEdit();
            ImGui.TextDisabled(CollabDiscordAuth.StatusText);
        }
        else if (loginState == CollabDiscordAuth.LoginState.Failed)
        {
            FormInputs.SetCursorToParameterEdit();
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
            ImGui.TextWrapped(CollabDiscordAuth.StatusText);
            ImGui.PopStyleColor();
        }
        else if (!string.IsNullOrEmpty(UserSettings.Config.CollabDiscordUserId))
        {
            FormInputs.SetCursorToParameterEdit();
            CustomComponents.StylizedText(CollabManager.Instance.LocalIdentity.DisplayName, Fonts.FontNormal, UiColors.StatusActivated);
        }
    }
}
