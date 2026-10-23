#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using Newtonsoft.Json.Linq;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

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

        if (manager.IsSessionActive)
        {
            DrawActiveSession(manager);
        }
        else
        {
            DrawHostAndJoinControls(manager);
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

    private static void DrawHostAndJoinControls(CollabManager manager)
    {
        var config = UserSettings.Config;

        FormInputs.AddSectionSubHeader("Host");
        if (CustomComponents.DrawCtaButton("Host on LAN", Icon.None, UiColors.ForegroundFull, UiColors.StatusActivated, Color.Transparent))
        {
            manager.StartHost(config.CollabPort, config.CollabPassword, config.CollabLanDiscovery);
        }

        CustomComponents.TooltipForLastItem("Start a session others can join. You keep full control: every edit from participants is applied to your graph first and then distributed.");

        FormInputs.AddVerticalSpace();

        DrawSecretInput("Relay server", ref _relayAddress, "Address of a TiXLCollabRelay server, e.g. relay.example.com:44710. Hosts and joiners must use the same relay.");

        ImGui.PushID("##collabrelayhost");
        if (ImGui.Button("Host via relay"))
        {
            ParseRelayAddress(_relayAddress, out var host, out var port);
            manager.StartHostViaRelay(host, port);
        }

        ImGui.PopID();
        CustomComponents.TooltipForLastItem("Register a room on the relay so participants can join by room code without port forwarding.");

        FormInputs.AddVerticalSpace();

        FormInputs.AddSectionSubHeader("Join");
        _joinAddress ??= "";
        ImGui.PushID("##collabjoin");
        if (ImGui.InputText("Address", ref _joinAddress, 256, ImGuiInputTextFlags.EnterReturnsTrue))
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

        _roomCode ??= "";
        ImGui.PushID("##collabroom");
        if (ImGui.InputText("Room code", ref _roomCode, 12, ImGuiInputTextFlags.EnterReturnsTrue))
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

    private static string _joinAddress;
    private static string _roomCode;
    private static string _relayAddress = "";

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
                _discoveryScanner.Start(config.CollabPort + LanDiscoveryResponder.DiscoveryPortOffset);
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
                                  - Everyone opens the same project. Participants who join late receive all edits made during the session.
                                  - Graph edits are synchronized: adding and deleting operators, connections, parameter changes, renames and moving nodes.
                                  - Undo histories stay local by design. Only use Ctrl+Z for edits you made yourself.
                                  - For sessions over the internet, host on a machine with an open/forwarded port or use a VPN like Tailscale.
                                  """);
    }

    // ---------------------------------------------------------------------------------------
    // Secret inputs
    // ---------------------------------------------------------------------------------------

    private static bool DrawSecretInput(string label, ref string secret, string tooltip)
    {
        ImGui.PushID(label);
        var input = secret ?? "";
        var changed = ImGui.InputText(label, ref input, 256, ImGuiInputTextFlags.Password);
        ImGui.PopID();

        if (!string.IsNullOrEmpty(tooltip))
            CustomComponents.TooltipForLastItem(tooltip);

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
            ImGui.TextUnformatted($"Discord: {config.CollabDiscordUserName}");
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
                                             "From the Discord Developer Portal (Applications > Your App > OAuth2).");

        changed |= DrawSecretInput("Discord Client Secret",
                                   ref config.CollabDiscordClientSecret,
                                   "Stored only in this machine's user settings. Add a redirect URI http://127.0.0.1:<any port>/callback/ to your Discord application's OAuth2 redirects.");

        var loginState = CollabDiscordAuth.State;
        var loginBusy = loginState is CollabDiscordAuth.LoginState.WaitingForBrowser or CollabDiscordAuth.LoginState.ExchangingCode;

        if (loginBusy)
        {
            ImGui.TextDisabled(CollabDiscordAuth.StatusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                CollabDiscordAuth.Cancel();
        }
        else
        {
            if (ImGui.Button("Log in with Discord"))
                CollabDiscordAuth.StartLogin();

            if (loginState == CollabDiscordAuth.LoginState.Failed)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
                ImGui.TextUnformatted(CollabDiscordAuth.StatusText);
                ImGui.PopStyleColor();
            }
            else
            {
                CustomComponents.TooltipForLastItem("Optional: use your Discord identity in collaboration sessions instead of a local nickname.");
            }
        }
    }
}
