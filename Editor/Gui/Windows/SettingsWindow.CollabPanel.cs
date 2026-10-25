#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The "Collaboration" panel of the settings window: identity (nickname and Discord login), session
/// controls (host / join with LAN discovery or direct address), and the participant list.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly LanDiscoveryScanner _discoveryScanner = new();

    private static void DrawCollabPanel(ref bool changed)
    {
        var manager = CollabManager.Instance;
        var config = UserSettings.Config;

        // -------------------------------------------------------------------------------------
        // Identity
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Identity");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddStringInput("Nickname",
                                             ref config.CollabDisplayName,
                                             null,
                                             null,
                                             """
                                             How other participants see you.
                                             Falls back to your Discord name (after login), then your OS user name.
                                             """);

        DrawDiscordSection(ref changed);

        FormInputs.AddVerticalSpace();

        // -------------------------------------------------------------------------------------
        // Session
        // -------------------------------------------------------------------------------------
        FormInputs.AddSectionSubHeader("Session");
        FormInputs.AddVerticalSpace();

        changed |= FormInputs.AddInt("Port",
                                     ref config.CollabPort,
                                     1024, 65535, 1,
                                     "TCP port used by collaboration sessions. Pick something above 1024 that is not used by other software.",
                                     CollabProtocol.DefaultTcpPort);

        changed |= FormInputs.AddCheckBox("Announce session on LAN",
                                          ref config.CollabLanDiscovery,
                                          """
                                          Lets other TiXL instances on the local network discover this session
                                          automatically. Turn off when hosting on a shared network.
                                          """,
                                          true);

        changed |= DrawSecretInput("Session password", ref config.CollabPassword,
                                   "Empty = anyone on the network can join. Recommended when hosting on shared networks.");

        FormInputs.AddVerticalSpace();
        DrawNetworkInterface(ref changed);
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
    }

    // ---------------------------------------------------------------------------------------
    // Hosting / joining
    // ---------------------------------------------------------------------------------------

    private static void DrawHostAndJoinControls(CollabManager manager, ref bool changed)
    {
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Host");
        DrawShareProjectChoice(ref changed);
        FormInputs.AddVerticalSpace();

        FormInputs.SetCursorToParameterEdit();
        if (CustomComponents.DrawCtaButton("Host on LAN", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery, CollabManager.ConfiguredShareProject);
        }

        CustomComponents.TooltipForLastItem("Start a session others can join. You keep full control: every edit from participants is applied to your graph first and then distributed.");

        FormInputs.AddVerticalSpace();

        DrawSecretInput("Relay server", ref _relayAddress, "Address of a TiXLCollabRelay server, e.g. relay.example.com:44710. Hosts and joiners must use the same relay.");

        FormInputs.SetCursorToParameterEdit();
        ImGui.PushID("##collabrelayhost");
        if (ImGui.Button("Host via relay"))
        {
            ParseRelayAddress(_relayAddress, out var host, out var port);
            manager.StartHostViaRelay(host, port, CollabManager.ConfiguredShareProject);
        }

        ImGui.PopID();
        CustomComponents.TooltipForLastItem("Register a room on the relay so participants can join by room code without port forwarding.");

        FormInputs.AddVerticalSpace();

        FormInputs.AddSectionSubHeader("Join");
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

        FormInputs.AddVerticalSpace();

        _roomCode ??= "";
        FormInputs.DrawInputLabel("Room code");
        ImGui.PushID("##collabroom");
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(null, false, true).X);
        if (ImGui.InputText("##collabRoomCode", ref _roomCode, 12, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ParseRelayAddress(_relayAddress, out var host, out var port);
            manager.JoinViaRelay(host, port, _roomCode, config.CollabPassword);
        }

        ImGui.PopID();
        ImGui.SameLine();
        if (ImGui.Button("Join via relay"))
        {
            ParseRelayAddress(_relayAddress, out var host, out var port);
            manager.JoinViaRelay(host, port, _roomCode, config.CollabPassword);
        }

        CustomComponents.TooltipForLastItem("Join a relay-hosted session with the room code the host shared.");

        DrawDiscoveredSessions(manager);
    }

    private static string _joinAddress = "";
    private static string _roomCode = "";
    private static string _relayAddress = "";

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
        host = text?.Trim() ?? "";
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
        ImGui.TextUnformatted("Sessions on this network:");
        ImGui.SameLine();

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

        FormInputs.AddVerticalSpace();
        foreach (var session in _discovered)
        {
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
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
        ImGui.TextUnformatted(manager.StatusText);
        ImGui.PopStyleColor();

        if (!string.IsNullOrEmpty(manager.RoomCode))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Room code:");
            ImGui.SameLine();
            ImGui.TextUnformatted(manager.RoomCode);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Share this code with the people you want to collaborate with.");
        }

        if (!string.IsNullOrEmpty(manager.SharedProjectName))
        {
            ImGui.TextDisabled($"Sharing: {CollabManager.ShortProjectName(manager.SharedProjectName)}");
        }

        FormInputs.AddVerticalSpace();

        var roster = manager.Roster;
        if (roster.Count == 0)
        {
            ImGui.TextDisabled("Waiting for participants...");
        }
        else
        {
            ImGui.TextUnformatted("Participants:");
            foreach (var (peerId, name) in roster)
            {
                var presence = manager.GetPresence(peerId);
                var color = presence?.AccentColor ?? new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f);
                var location = presence == null
                                   ? ""
                                   : presence.CompositionId == Guid.Empty
                                       ? ""
                                       : presence.IsHoveringCanvas ? "editing" : "in session";

                ImGui.PushStyleColor(ImGuiCol.Text, CollabColors.ToUint(color));
                ImGui.Bullet();
                ImGui.TextUnformatted(string.IsNullOrEmpty(name) ? "unnamed peer" : name);
                ImGui.PopStyleColor();

                if (!string.IsNullOrEmpty(location))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(location);
                }
            }
        }

        FormInputs.AddVerticalSpace();
        FormInputs.SetIndentToLeft();
        if (ImGui.Button("Leave session"))
            manager.Leave();

        FormInputs.SetIndentToParameters();

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
    // Secret inputs
    // ---------------------------------------------------------------------------------------

    private static bool DrawSecretInput(string label, ref string secret, string tooltip)
    {
        FormInputs.DrawInputLabel(label);

        ImGui.PushID(label);
        var input = secret ?? "";
        ImGui.SetNextItemWidth(FormInputs.GetAvailableInputSize(tooltip, false, true).X);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
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

    private static void DrawDiscordSection(ref bool changed)
    {
        var config = UserSettings.Config;

        if (!string.IsNullOrEmpty(config.CollabDiscordUserId))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
            var guild = string.IsNullOrEmpty(CollabDiscordAuth.GuildName) ? "" : $" · {CollabDiscordAuth.GuildName}";
            ImGui.TextUnformatted($"Discord: {config.CollabDiscordUserName}{guild}");
            ImGui.PopStyleColor();
            if (ImGui.SmallButton("Log out"))
            {
                config.CollabDiscordUserId = "";
                config.CollabDiscordUserName = "";
                config.CollabDiscordUserAvatar = "";
                CollabManager.Instance.RefreshLocalIdentity();
                changed = true;
            }

            return;
        }

        changed |= FormInputs.AddStringInput("Discord Client ID",
                                             ref config.CollabDiscordClientId,
                                             null,
                                             null,
                                             """
                                             From the Discord Developer Portal (Applications > Your App > OAuth2).
                                             Leave empty if the build ships TiXL's own application id.
                                             """);

        CustomComponents.HelpText("""
                                  One-time setup: create a Discord application, then add this exact redirect URI
                                  under OAuth2 → Redirects in the Developer Portal. No client secret is needed
                                  (the login uses PKCE).
                                  """);
        FormInputs.ApplyIndent();
        ImGui.TextWrapped(CollabDiscordAuth.RedirectUri);
        if (ImGui.SmallButton("Copy redirect URI"))
            ImGui.SetClipboardText(CollabDiscordAuth.RedirectUri);
        CustomComponents.TooltipForLastItem("Discord matches the redirect URI verbatim, so the port is fixed.");

        FormInputs.AddVerticalSpace(6);
        CustomComponents.HelpText("""
                                  Logging in opens Discord in your browser. The account you sign in with must be a
                                  member of the TiXL Discord server — membership is what the login verifies.
                                  """);
        FormInputs.SetCursorToParameterEdit();
        if (ImGui.Button("Join the Discord server"))
            CollabDiscordAuth.OpenInvite();
        CustomComponents.TooltipForLastItem("Opens the server invite in your browser so you can join before logging in.");

        FormInputs.AddVerticalSpace(6);
        var loginState = CollabDiscordAuth.State;
        var loginBusy = loginState is CollabDiscordAuth.LoginState.WaitingForBrowser or CollabDiscordAuth.LoginState.ExchangingCode;
        var canLogIn = CollabDiscordAuth.HasDefaultClient || !string.IsNullOrWhiteSpace(config.CollabDiscordClientId);

        FormInputs.SetCursorToParameterEdit();
        if (loginBusy)
        {
            ImGui.TextDisabled(CollabDiscordAuth.StatusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                CollabDiscordAuth.Cancel();
        }
        else
        {
            ImGui.BeginDisabled(!canLogIn);
            if (ImGui.Button(CollabDiscordAuth.NeedsGuildJoin ? "Check membership again" : "Log in with Discord"))
                CollabDiscordAuth.StartLogin();
            ImGui.EndDisabled();

            if (loginState == CollabDiscordAuth.LoginState.Failed)
            {
                FormInputs.AddVerticalSpace(6);
                ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
                ImGui.TextWrapped(CollabDiscordAuth.StatusText);
                ImGui.PopStyleColor();
            }
            else if (!canLogIn)
            {
                ImGui.TextDisabled("Enter the client ID first");
            }
            else
            {
                CustomComponents.TooltipForLastItem("Opens discord.com in your browser. The account must be a member of the TiXL Discord server.");
            }
        }
    }
}
