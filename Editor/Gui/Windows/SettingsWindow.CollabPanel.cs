#nullable enable
using ImGuiNET;
using Newtonsoft.Json.Linq;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Show;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Collaboration" panel of the settings window: identity (Discord login), session controls
/// (start a session and share the session ID, or join with an ID) and the participant list.
/// Direct/LAN sessions and the relay choice live under Advanced.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly LanDiscoveryScanner _discoveryScanner = new();

    private static void DrawCollabPanel(ref bool changed)
    {
        var manager = CollabManager.Instance;
        var config = UserSettings.Config;

        // Collaboration is switched through the menu-bar mode selector; this panel only
        // configures and runs sessions. Session actions below claim the collab mode, which ends
        // a boygroup/failover session on this machine (the modes are mutually exclusive).

        // -------------------------------------------------------------------------------------
        // Identity
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Identity");
        FormInputs.AddVerticalSpace();

        DrawDiscordLoginButton();

        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddStringInput("Nickname",
                                             ref config.CollabDisplayName,
                                             null,
                                             null,
                                             """
                                             Local fallback when not logged in to Discord.
                                             Falls back to your OS user name when empty.
                                             """);

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Session
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Session");
        FormInputs.AddVerticalSpace();

        changed |= DrawSecretInput("Session password", ref config.CollabPassword,
                                   "Empty = anyone with the session ID can join. Recommended when the ID is shared widely.");

        FormInputs.AddVerticalSpace();

        if (manager.IsSessionActive)
        {
            DrawActiveSession(manager);
        }
        else
        {
            FormInputs.AddVerticalSpace();
            CustomComponents.HelpText("""
                                      No session is running. Use the menu bar at the top:
                                      Collab ▸ Host session starts one and gives you a session ID;
                                      Collab ▸ Join session connects with the ID the host shared.
                                      """);
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

    /// <summary>One clipboard string carrying both things a joiner needs.</summary>
    private static string BuildInvite(CollabManager manager)
    {
        var relay = string.IsNullOrEmpty(manager.RelayEndpoint) ? RelayAddressOrDefault() : manager.RelayEndpoint;
        return $"TiXL collab invite\nRelay: {relay}\nRoom: {manager.RoomCode}";
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

    private static void ParseRelayAddress(string text, out string host, out int port)
    {
        host = string.IsNullOrWhiteSpace(text) ? CollabProtocol.DefaultRelayHost : text.Trim();
        port = CollabProtocol.DefaultRelayPort;
        var separator = host.LastIndexOf(':');
        if (separator > 0 && int.TryParse(host[(separator + 1)..], out var parsed))
        {
            port = parsed;
            host = host[..separator];
        }
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
                _discoveryScanner.Start(config.CollabPort + LanDiscoveryResponder.DiscoveryPortOffset, null, config.NetworkInterface);
                _discoveryScannerActive = true;
                _discoveryScanUntil = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            }
        }
        else
        {
            ImGui.TextDisabled("scanning...");
        }

        DrainDiscoveryResults();

        DrawDiscoveredSessionRows(manager);
    }

    private static bool _discoveryScannerActive;
    private static DateTime _discoveryScanUntil;
    private static readonly List<CollabManager.DiscoveredSession> _discovered = [];
    private static string _joinAddress = "";

    private static void DrainDiscoveryResults()
    {
        while (_discoveryScanner.Results.TryDequeue(out var result))
        {
            var announcement = result.Announcement;
            // Read type-tolerantly: the announcement comes from an untrusted UDP sender, and a
            // malformed field must be a miss, not an exception out of the frame loop.
            CollabProtocol.TryGetInt(announcement, "port", out var port);
            CollabProtocol.TryGetBool(announcement, "pwd", out var passwordProtected);
            var session = new CollabManager.DiscoveredSession
                              {
                                  Name = CollabProtocol.GetString(announcement, "name", "TiXL session"),
                                  HostName = CollabProtocol.GetString(announcement, "host", ""),
                                  Address = result.Source.Address.ToString(),
                                  Port = port > 0 ? port : CollabProtocol.DefaultTcpPort,
                                  PasswordProtected = passwordProtected,
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

    private static void DrawDiscoveredSessionRows(CollabManager manager)
    {
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
                ShowControlMode.Activate(ShowControlMode.Collab);
                manager.JoinDirect(session.Address, session.Port, UserSettings.Config.CollabPassword);
            }

            ImGui.PopID();
        }
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

        FormInputs.AddVerticalSpace();
        FormInputs.AddSectionSubHeader("Participants");
        FormInputs.AddVerticalSpace(4);

        DrawCursorColor(manager);

        var roster = manager.Roster;
        if (roster.Count == 0)
        {
            ImGui.TextDisabled("Waiting for participants...");
        }
        else
        {
            foreach (var (peerId, name) in roster)
            {
                var presence = manager.GetPresence(peerId);
                var color = presence?.AccentColor ?? new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f);
                var activity = presence == null
                                   ? ""
                                   : presence.CompositionId == Guid.Empty
                                       ? ""
                                       : presence.IsHoveringCanvas ? "editing" : "in session";

                ImGui.PushStyleColor(ImGuiCol.Text, CollabColors.ToUint(color));
                ImGui.Bullet();
                ImGui.TextUnformatted(string.IsNullOrEmpty(name) ? "unnamed participant" : name);
                ImGui.PopStyleColor();

                if (peerId == 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(host)");
                }

                if (!string.IsNullOrEmpty(activity))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(activity);
                }

                // Round trip of our own heartbeats; relay hops included, so it reads on the internet
                // path too. -1 until the first echo returns.
                var rtt = manager.GetPeerRttMs(peerId);
                if (rtt >= 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{rtt} ms");
                }

                if (manager.State == CollabManager.SessionState.Hosting && peerId != 0)
                {
                    ImGui.SameLine();
                    ImGui.PushID("kick" + peerId);
                    if (ImGui.SmallButton("Remove"))
                        _pendingKickPeerId = peerId;

                    ImGui.PopID();
                    CustomComponents.TooltipForLastItem("Removes this participant from the session. They can join again unless the session has a password.");
                }
            }

            DrawKickConfirmation(manager);
        }

        FormInputs.AddVerticalSpace();
        FormInputs.SetIndentToLeft();
        if (ImGui.Button("Leave Session"))
            manager.Leave(manual: true);

        FormInputs.SetIndentToParameters();

        FormInputs.AddVerticalSpace();
        CustomComponents.HelpText("""
                                  How it works:
                                  - Late joiners receive full snapshots of the compositions edited in the session - their local changes to those compositions are replaced by the host's state.
                                  - Graph edits are synchronized: adding and deleting operators, connections, parameter changes, renames and moving nodes.
                                  - Undo/Redo are session-wide while connected: Ctrl+Z reverts the last edit of ANY participant (the host keeps the shared history).
                                  - Parameters someone is editing are soft-locked: they appear disabled with a "(locked)" marker until released.
                                  - For sessions over the internet, the relay session ID works without port forwarding; a LAN host needs an open/forwarded port or a VPN like Tailscale.
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
    // Advanced
    // ---------------------------------------------------------------------------------------

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
                                             $"{CollabProtocol.DefaultRelayHost}:{CollabProtocol.DefaultRelayPort}",
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
        FormInputs.AddSectionSubHeader("Join by address");
        _joinAddress ??= "";
        FormInputs.DrawInputLabel("Address");
        ImGui.PushID("##collabjoin");
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, true).X);
        if (ImGui.InputText("##collabJoinAddress", ref _joinAddress, 256, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            ParseJoinAddress(_joinAddress, config.CollabPort, out var host, out var port);
            manager.JoinDirect(host, port, config.CollabPassword);
        }

        ImGui.PopID();
        ImGui.SameLine();
        if (ImGui.Button("Connect"))
        {
            ParseJoinAddress(_joinAddress, config.CollabPort, out var host, out var port);
            manager.JoinDirect(host, port, config.CollabPassword);
        }

        CustomComponents.TooltipForLastItem("Direct address of the host, e.g. 192.168.1.42 for LAN sessions or a public address / VPN IP for online sessions.");

        FormInputs.AddVerticalSpace();
        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Host on LAN", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery);
        }

        CustomComponents.TooltipForLastItem("Start a session on this machine without a relay. Others join by LAN discovery or by your address.");

        FormInputs.AddVerticalSpace();
        DrawDiscoveredSessions(manager);
    }

    private static int _pendingKickPeerId = -1;

    /// <summary>Two-step removal: the button asks, this confirms.</summary>
    private static void DrawKickConfirmation(CollabManager manager)
    {
        if (_pendingKickPeerId < 0)
            return;

        var name = manager.Roster.TryGetValue(_pendingKickPeerId, out var found) ? found : $"#{_pendingKickPeerId}";
        FormInputs.AddVerticalSpace(4);
        ImGui.TextUnformatted($"Remove {name} from the session?");

        if (ImGui.Button("Remove"))
        {
            manager.KickParticipant(_pendingKickPeerId);
            _pendingKickPeerId = -1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _pendingKickPeerId = -1;
    }

    /// <summary>
    /// Everyone picks their own cursor color; other participants see it within a moment because it
    /// rides the presence packets. Reset returns to the color derived from the user id.
    /// </summary>
    private static void DrawCursorColor(CollabManager manager)
    {
        FormInputs.DrawInputLabel("Cursor color");

        var color = manager.LocalIdentity.AccentColor;
        var edited = ColorEditButton.Draw(ref color,
                                          new Vector2(ImGui.GetFrameHeight() * 1.6f, ImGui.GetFrameHeight()));
        if ((edited & InputEditStateFlags.Modified) != 0)
            UserSettings.Config.CollabCursorColor = CollabColors.ToPacked(color);

        // Saved when the edit ends, not on every frame of a drag.
        if ((edited & InputEditStateFlags.Finished) != 0)
            UserSettings.Save();

        CustomComponents.TooltipForLastItem("The color other participants see for your cursor, selections and edit badges.");

        if (UserSettings.Config.CollabCursorColor != 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset"))
            {
                UserSettings.Config.CollabCursorColor = 0;
                UserSettings.Save();
            }
        }

        FormInputs.AddVerticalSpace();
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
        var changed = ImGui.InputText("##secret", ref input, 256, ImGuiInputTextFlags.Password);
        ImGui.PopID();
        CustomComponents.TooltipForLastItem(tooltip);

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
