#nullable enable
using ImGuiNET;
using Newtonsoft.Json.Linq;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

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

        // The settings switch ends a running session (CollabManager.Update enforces it), so the
        // session controls must not be reachable while collaboration is off.
        var enabled = config.CollabEnabled;
        if (FormInputs.AddCheckBox("Enable",
                                   ref enabled,
                                   """
                                   Turns collaboration on. When off, no session is hosted or joined,
                                   nothing is discovered on the network, and a running session ends.
                                   """,
                                   true))
        {
            config.CollabEnabled = enabled;
            changed = true;
        }

        if (!config.CollabEnabled)
            return;

        FormInputs.AddVerticalSpace();

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
        FormInputs.AddVerticalSpace();

        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Start Session", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            ParseRelayAddress(config.CollabRelayAddress, out var host, out var port);
            manager.StartHostViaRelay(host, port);
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

        if (ImGui.Button("Paste Invite and Join"))
        {
            if (TryParseInvite(ImGui.GetClipboardText(), out var relayAddress, out var room))
            {
                if (!string.IsNullOrEmpty(relayAddress))
                {
                    config.CollabRelayAddress = relayAddress;
                    changed = true;
                }

                _roomCode = room;
                _inviteMessage = "";
                ParseRelayAddress(config.CollabRelayAddress, out var host, out var port);
                manager.JoinViaRelay(host, port, room, config.CollabPassword);
            }
            else
            {
                _inviteMessage = "The clipboard does not contain a TiXL collab invite.";
            }
        }

        CustomComponents.TooltipForLastItem("Reads the relay address and session ID from a copied invite, then joins.");

        if (!string.IsNullOrEmpty(_inviteMessage))
        {
            ImGui.TextDisabled(_inviteMessage);
        }
    }

    private static string _roomCode = "";
    private static string _joinAddress = "";
    private static string _inviteMessage = "";

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
            }
        }

        FormInputs.AddVerticalSpace();
        FormInputs.SetIndentToLeft();
        if (ImGui.Button("Leave Session"))
            manager.Leave();

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
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery);
        }

        FormInputs.AddVerticalSpace();
        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Host on LAN", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery);
        }

        CustomComponents.TooltipForLastItem("Start a session on this machine without a relay. Others join by LAN discovery or by your address.");

        FormInputs.AddVerticalSpace();
        DrawDiscoveredSessions(manager);
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
