#nullable enable
using System.Collections.Generic;
using System;
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Show;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// The collab home, in three tabs like a party-matching screen: <b>Party</b> (the session
/// browser and hosting controls), <b>Session</b> (participants, listing, whispers, leave) and
/// <b>Chat</b>. Configuration (identity, password, backend) stays in the Settings window.
/// </summary>
[HelpUiID("CollabWindow")]
internal sealed class CollabWindow : Window
{
    internal CollabWindow()
    {
        Config.Title = "Collab";
        Config.Visible = false;
        WindowSizeOverride = new Vector2(640, 660);
    }

    internal override IReadOnlyList<Window> GetInstances() => [this];

    private enum Tab
    {
        Party,
        Session,
        Chat,
    }

    private static Tab _selectedTab = Tab.Party;

    /// <summary>Selects a tab by name (Party, Session, Chat); used by the debug bridge.</summary>
    internal bool TrySelectTab(string name)
    {
        if (!Enum.TryParse<Tab>(name, true, out var tab))
            return false;

        _selectedTab = tab;
        return true;
    }

    protected override void DrawContent()
    {
        var collab = CollabManager.Instance;
        if (!collab.IsEnabled)
        {
            ImGui.TextDisabled("Collaboration is turned off.");
            ImGui.TextDisabled("Enable it in Settings > Collaboration.");
            return;
        }

        DrawTabButtons();
        FormInputs.AddVerticalSpace(6);

        switch (_selectedTab)
        {
            case Tab.Party:
                DrawPartyTab(collab);
                break;
            case Tab.Session:
                DrawSessionTab(collab);
                break;
            case Tab.Chat:
                DrawChatTab(collab);
                break;
        }
    }

    /// <summary>Three small toggle buttons in the top-right corner, like the party-matching
    /// reference. The active one is highlighted; a click switches the tab. SetCursorPosX takes
    /// window-LOCAL coordinates - screen-space math here placed the row off-window.</summary>
    private static void DrawTabButtons()
    {
        var labels = new[] { "Party", "Session", "Chat" };
        var tabs = new[] { Tab.Party, Tab.Session, Tab.Chat };

        var widthTotal = 0f;
        foreach (var label in labels)
            widthTotal += ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 6;

        // Window-local coordinates; TiXL's zero padding makes the window rect the content rect.
        ImGui.SetCursorPosX(ImGui.GetWindowSize().X - 12 - widthTotal);

        for (var i = 0; i < tabs.Length; i++)
        {
            var isActive = _selectedTab == tabs[i];
            if (isActive)
                ImGui.PushStyleColor(ImGuiCol.Button, UiColors.StatusActivated.Rgba);

            if (ImGui.Button(labels[i]))
                _selectedTab = tabs[i];

            if (isActive)
                ImGui.PopStyleColor();

            if (i < tabs.Length - 1)
                ImGui.SameLine(0, 0);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Party tab: the session browser plus the hosting controls
    // ---------------------------------------------------------------------------------------

    private static void DrawPartyTab(CollabManager collab)
    {
        if (collab.State == CollabManager.SessionState.Off)
        {
            DrawHostControls(collab);
            FormInputs.AddVerticalSpace(8);
            DrawSessionBrowser(collab);
        }
        else
        {
            ImGui.TextWrapped(collab.StatusText);
            FormInputs.AddVerticalSpace(4);
            ImGui.TextDisabled("Leave the current session to browse and join another one.");
        }
    }

    private static void DrawHostControls(CollabManager collab)
    {
        var password = UserSettings.Config.CollabPassword ?? "";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##collab-pwd", "Session password (optional)", ref password, 64);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            UserSettings.Config.CollabPassword = password;
            UserSettings.Save();
        }

        FormInputs.AddVerticalSpace(4);
        var canEdit = collab.ParticipantsCanEdit;
        if (ImGui.Checkbox("Participants can edit (off = spectators)", ref canEdit))
            collab.SetParticipantsCanEdit(canEdit);

        CustomComponents.TooltipForLastItem("""
                                            On: everyone can change the shared project. Off: participants are
                                            spectators - they see your cursors, presence and chat, but the host
                                            drops their graph changes.
                                            """);

        FormInputs.AddVerticalSpace(4);
        var projectName = ProjectView.Focused?.OpenedProject.Package.DisplayName ?? "";
        if (ImGui.Button("Form Party (via backend)", new Vector2(190, 0)))
        {
            collab.StartHostViaBackend();
            _selectedTab = Tab.Session;
        }

        CustomComponents.TooltipForLastItem("""
                                            Hosts a relay-free session: joiners ask first, you confirm in the
                                            popup, and the session runs over WebRTC. Needs the backend URL in
                                            Settings.
                                            """);

        ImGui.SameLine();
        if (ImGui.Button("Form Party (relay)", new Vector2(150, 0)))
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            ParseRelayAddress(UserSettings.Config.CollabRelayAddress, out var relayHost, out var relayPort);
            collab.StartHostViaRelay(relayHost, relayPort);
            _selectedTab = Tab.Session;
        }

        ImGui.SameLine();
        if (ImGui.Button("Form Party (LAN)", new Vector2(130, 0)))
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            collab.StartHost(UserSettings.Config.CollabPort,
                             UserSettings.Config.CollabPassword ?? "",
                             UserSettings.Config.CollabLanDiscovery,
                             projectName.Length > 0 ? projectName : null);
            _selectedTab = Tab.Session;
        }

        // The project context note: parties are formed for the open project and outlive browsing.
        FormInputs.AddVerticalSpace(6);
        ImGui.TextDisabled("The party stays alive while you browse other projects,");
        ImGui.TextDisabled("until you close the party, unload the project or exit TiXL.");
    }

    // ---------------------------------------------------------------------------------------
    // Session tab: status, participants, listing, whispers, leave
    // ---------------------------------------------------------------------------------------

    private static void DrawSessionTab(CollabManager collab)
    {
        DrawSessionHeader(collab);
        DrawDivider();

        if (collab.State == CollabManager.SessionState.Off)
        {
            ImGui.TextDisabled("No session is running. Host one on the Party tab");
            ImGui.TextDisabled("or join a listed session there.");
            return;
        }

        // Spectator state: the host toggles, guests see the badge.
        if (collab.State == CollabManager.SessionState.Hosting)
        {
            var canEdit = collab.ParticipantsCanEdit;
            if (ImGui.Checkbox("Participants can edit (off = spectators)", ref canEdit))
                collab.SetParticipantsCanEdit(canEdit);
        }
        else if (collab.ReadOnlyForParticipants)
        {
            ImGui.TextColored(UiColors.StatusWarning, "Read-only session: the host set participants to spectators.");
        }

        DrawHostPreview(collab);
        DrawRoster(collab);
        DrawDivider();
        DrawJoinRequests(collab);
        DrawListingControls(collab);
        DrawWhispers(collab);
        DrawDivider();
        DrawLeaveButton(collab);
    }

    /// <summary>Host: the pending join requests, also shown in the popup window.</summary>
    private static void DrawJoinRequests(CollabManager collab)
    {
        var requests = collab.PendingJoinRequests;
        if (requests.Count == 0 || collab.State != CollabManager.SessionState.Hosting)
            return;

        FormInputs.AddSectionHeader("Join requests");
        FormInputs.AddVerticalSpace(4);

        // The button handlers remove from the list while this loop is iterating it.
        foreach (var request in requests.ToArray())
        {
            ImGui.PushID(request.TicketId);
            ImGui.TextUnformatted(request.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Confirm"))
                collab.ApproveJoin(request.TicketId);

            ImGui.SameLine();
            if (ImGui.SmallButton("Decline"))
                collab.RejectJoin(request.TicketId, block: false);

            ImGui.PopID();
        }

        FormInputs.AddVerticalSpace(4);
    }

    private static void DrawLeaveButton(CollabManager collab)
    {
        if (ImGui.Button("Leave Session"))
            collab.Leave(manual: true);
    }

    private static void DrawListingControls(CollabManager collab)
    {
        // Host + relay or backend: the listing is editable while the session runs.
        var isBackend = collab.RelayEndpoint == CollabWebLobby.BackendUrl && CollabWebLobby.IsConfigured;
        if (collab.State != CollabManager.SessionState.Hosting
            || (collab.RelayEndpoint.Length == 0 && !isBackend))
            return;

        var config = UserSettings.Config;
        FormInputs.AddSectionHeader("Session browser entry");
        FormInputs.AddVerticalSpace(4);
        FormInputs.AddCheckBox("Listed in session browser", ref config.CollabListSession,
                               """
                               On: the session shows up in other people's session browser with the
                               title and purpose below. Off: only people with the session ID can
                               find it. Applies right away; the session keeps running either way.
                               """,
                               false);
        FormInputs.AddStringInput("Title", ref config.CollabSessionTitle,
                                  $"{collab.LocalIdentity.DisplayName}'s session",
                                  null, "What the session browser shows.");
        DrawPurposePicker(ref config.CollabSessionPurpose);
        if (ImGui.SmallButton("Update entry"))
        {
            collab.UpdateSessionListing(config.CollabSessionTitle, config.CollabSessionPurpose,
                                        ProjectView.Focused?.OpenedProject.Package.DisplayName ?? "",
                                        config.CollabListSession);
        }

        CustomComponents.TooltipForLastItem("Sends the title, purpose and visibility above to the backend or relay so other people's browsers see them.");
    }

    /// <summary>The purpose combo, standalone (moved from the settings panel's shared form).</summary>
    private static void DrawPurposePicker(ref string purpose)
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##partyPurpose", purpose))
        {
            foreach (var option in SessionPurposes)
            {
                if (ImGui.Selectable(option, option == purpose))
                    purpose = option;
            }

            ImGui.EndCombo();
        }
    }

    private static void DrawWhispers(CollabManager collab)
    {
        if (collab.State != CollabManager.SessionState.Hosting)
            return;

        FormInputs.AddSectionHeader("Whispers");
        FormInputs.AddVerticalSpace(4);
        var whispers = collab.LobbyWhispers;
        if (whispers.Count == 0)
        {
            ImGui.TextDisabled("People can whisper you from the Party tab before they join.");
        }
        else
        {
            foreach (var whisper in whispers)
            {
                if (whisper.FromHost)
                    ImGui.TextColored(_whisperOwnColor, "You");
                else
                    ImGui.TextUnformatted(whisper.From);

                ImGui.SameLine();
                ImGui.TextWrapped(whisper.Text);
            }

            _replyToName = whispers.Last(w => !w.FromHost).From;
            FormInputs.AddVerticalSpace(4);
            ImGui.SetNextItemWidth(-72);
            ImGui.InputTextWithHint("##whisperReply", $"Reply to {_replyToName}...", ref _whisperReplyInput, 256);
            ImGui.SameLine();
            ImGui.BeginDisabled(_whisperReplyInput.Length == 0);
            if (ImGui.Button("Send##whisper"))
            {
                collab.ReplyToLobbyWhisper(_replyToName, _whisperReplyInput);
                _whisperReplyInput = "";
            }

            ImGui.EndDisabled();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Chat tab
    // ---------------------------------------------------------------------------------------

    private string _chatInput = "";
    private int _lastChatCount;
    private bool _scrollToBottom = true;

    private void DrawChatTab(CollabManager collab)
    {
        if (collab.State == CollabManager.SessionState.Off)
        {
            ImGui.TextDisabled("Chat needs a session. Host or join one first.");
            return;
        }

        DrawChat(collab);
    }

    private void DrawChat(CollabManager collab)
    {
        var log = collab.ChatLog;
        var uiScale = T3Ui.UiScaleFactor;
        var height = Math.Max(80 * uiScale, ImGui.GetContentRegionAvail().Y - 30 * uiScale);
        if (ImGui.BeginChild("##collabwindowchat", new Vector2(0, height), ImGuiChildFlags.Borders))
        {
            if (_scrollToBottom || log.Count != _lastChatCount)
            {
                ImGui.SetScrollHereY(1f);
                _lastChatCount = log.Count;
            }

            foreach (var entry in log)
            {
                if (entry.Name == null)
                {
                    ImGui.TextColored(UiColors.TextMuted, $"  {entry.Text}");
                }
                else
                {
                    ImGui.TextColored(UiColors.StatusActivated, entry.Name);
                    ImGui.SameLine();
                    ImGui.TextWrapped(entry.Text);
                }
            }
        }
        ImGui.EndChild();

        ImGui.SetNextItemWidth(-70 * uiScale);
        var send = ImGui.InputTextWithHint("##chat", "Message...", ref _chatInput, 256,
                                           ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            collab.SendChat(_chatInput);
            _chatInput = "";
            _scrollToBottom = true;
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Shared session pieces (header, preview, roster)
    // ---------------------------------------------------------------------------------------

    private static void DrawDivider()
    {
        FormInputs.AddVerticalSpace(6);
        ImGui.Separator();
        FormInputs.AddVerticalSpace(6);
    }

    private static void DrawSessionHeader(CollabManager collab)
    {
        FormInputs.AddSectionHeader(collab.State == CollabManager.SessionState.Hosting ? "Hosting" : "Session");
        ImGui.TextWrapped(collab.StatusText);

        if (collab.State != CollabManager.SessionState.Off)
        {
            if (collab.IsStreamingPreview)
                ImGui.TextDisabled($"Preview: {collab.PreviewEncoderName}");
            if (!string.IsNullOrEmpty(collab.ErrorText))
                ImGui.TextColored(UiColors.StatusWarning, collab.ErrorText);
        }
    }

    /// <summary>The host's rendered output, once the video track has delivered a frame.</summary>
    private static void DrawHostPreview(CollabManager collab)
    {
        if (collab.VideoReceiver is not { } receiver
            || !receiver.TryGetTexture(out var view, out var width, out var height)
            || view == null
            || width <= 0
            || height <= 0)
            return;

        FormInputs.AddSectionHeader("Host Preview");
        var available = ImGui.GetContentRegionAvail();
        var scale = available.X / width;
        var maxHeight = available.Y * 0.45f;
        if (height * scale > maxHeight)
            scale = maxHeight / height;

        ImGui.Image((IntPtr)view.ImGuiTextureId, new Vector2(width * scale, height * scale));
        FormInputs.AddVerticalSpace(4);
    }

    private static void DrawRoster(CollabManager collab)
    {
        FormInputs.AddSectionHeader($"Participants ({collab.Roster.Count + 1})");

        var dl = ImGui.GetWindowDrawList();
        var isHost = collab.State == CollabManager.SessionState.Hosting;
        if (!isHost)
            _pendingKickPeerId = -1;

        DrawNameChip(dl, "You" + (isHost ? "  (host)" : ""), UiColors.StatusActivated, true);

        foreach (var (peerId, name) in collab.Roster)
        {
            var link = collab.DescribePeerLink(peerId);
            var suffix = link.Length > 0 ? $"  · {link}" : "";
            var isSpectated = CollabSpectator.IsSpectatingPeer(peerId);
            var isAddingOperator = collab.GetPresence(peerId)?.IsAddingOperator ?? false;
            DrawNameChip(dl, name + suffix + (!isHost ? "  (host)" : "") + (isSpectated ? "  · following" : "") + (isAddingOperator ? "  · adding operator" : ""),
                         isSpectated ? UiColors.StatusAutomated : UiColors.Text, false);

            _menuPeerId = peerId;
            ImGui.PushID(peerId);
            CustomComponents.ContextMenuForItem(DrawPeerMenu, name, "##peerMenu");
            ImGui.PopID();
        }

        DrawKickConfirmation(collab);
    }

    private static void DrawKickConfirmation(CollabManager collab)
    {
        if (_pendingKickPeerId < 0)
            return;

        if (!collab.Roster.TryGetValue(_pendingKickPeerId, out var name))
        {
            _pendingKickPeerId = -1;
            return;
        }

        FormInputs.AddVerticalSpace(4);
        ImGui.TextUnformatted($"Remove {name} from the session?");
        if (ImGui.Button("Kick"))
        {
            collab.KickParticipant(_pendingKickPeerId);
            _pendingKickPeerId = -1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _pendingKickPeerId = -1;
    }

    private static void DrawPeerMenu()
    {
        if (CollabSpectator.IsSpectatingPeer(_menuPeerId))
        {
            if (CustomComponents.DrawMenuItem(1, "Unfollow"))
                CollabSpectator.Stop();
        }
        else if (CustomComponents.DrawMenuItem(2, "Follow"))
        {
            CollabSpectator.Start(_menuPeerId);
        }

        if (CollabManager.Instance.State == CollabManager.SessionState.Hosting && CustomComponents.DrawMenuItem(3, "Kick"))
            _pendingKickPeerId = _menuPeerId;

        if (CollabManager.Instance.State == CollabManager.SessionState.Hosting && CustomComponents.DrawMenuItem(4, "Make Host"))
            _pendingHostTransferPeerId = _menuPeerId;
    }

    private static void DrawHostTransferConfirmation(CollabManager collab)
    {
        if (_pendingHostTransferPeerId < 0)
            return;

        var name = collab.Roster.TryGetValue(_pendingHostTransferPeerId, out var found) ? found : $"#{_pendingHostTransferPeerId}";
        FormInputs.AddVerticalSpace(4);
        ImGui.TextWrapped($"Make {name} the host? The session continues on their machine and you stay in it as a participant.");

        if (ImGui.Button("Make Host"))
        {
            collab.TransferHostTo(_pendingHostTransferPeerId);
            _pendingHostTransferPeerId = -1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _pendingHostTransferPeerId = -1;
    }

    private static int _pendingKickPeerId = -1;
    private static int _pendingHostTransferPeerId = -1;
    private static int _menuPeerId;
    private static string _whisperReplyInput = "";
    private static string _replyToName = "";
    private static readonly System.Numerics.Vector4 _whisperOwnColor = new(0.55f, 0.8f, 0.55f, 1f);

    /// <summary>Draws a chip after the previous one, wrapping to a new line when the row is full.</summary>
    private static void DrawNameChip(ImDrawListPtr dl, string label, Color color, bool isFirst)
    {
        var uiScale = T3Ui.UiScaleFactor;
        var gap = 4 * uiScale;
        var padding = new Vector2(6, 3) * uiScale;
        var size = ImGui.CalcTextSize(label) + padding * 2;

        var rightEdge = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (!isFirst && ImGui.GetItemRectMax().X + gap + size.X <= rightEdge)
            ImGui.SameLine(0, gap);

        var pos = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(pos, pos + size, UiColors.ForegroundFull.Fade(0.08f), 4 * uiScale);
        dl.AddText(pos + padding, color, label);
        ImGui.Dummy(size);
    }

    // ---------------------------------------------------------------------------------------
    // Party browser (moved from the settings panel: the party-matching table)
    // ---------------------------------------------------------------------------------------

    private static string _lobbyFilter = "";
    private static string _lobbyPurposeFilter = "All";
    private static string _lobbySelectedCode = "";
    private static bool _lobbyLoadedOnce;
    private static string _lobbyWhisperInput = "";

    internal static readonly string[] SessionPurposes = ["Jam", "Show", "Teaching", "Help"];

    private static void DrawSessionBrowser(CollabManager collab)
    {
        FormInputs.AddSectionHeader("Open sessions");
        FormInputs.AddVerticalSpace(4);

        ParseRelayAddress(UserSettings.Config.CollabRelayAddress, out var relayHost, out var relayPort);
        var useBackend = CollabWebLobby.IsConfigured;
        if (useBackend)
        {
            if (!_lobbyLoadedOnce)
            {
                _lobbyLoadedOnce = true;
                CollabWebLobby.Refresh();
            }
            else if (!CollabWebLobby.IsRefreshing && DateTime.UtcNow - CollabWebLobby.LastRefreshUtc > TimeSpan.FromSeconds(15))
            {
                CollabWebLobby.Refresh();
            }
        }
        else if (!_lobbyLoadedOnce)
        {
            _lobbyLoadedOnce = true;
            CollabLobby.Refresh(relayHost, relayPort);
        }
        else if (!CollabLobby.IsRefreshing && DateTime.UtcNow - CollabLobby.LastRefreshUtc > TimeSpan.FromSeconds(15))
        {
            CollabLobby.Refresh(relayHost, relayPort);
        }

        var rooms = useBackend ? CollabWebLobby.Rooms : CollabLobby.Rooms;

        var fontSize = ImGui.GetFontSize();
        FormInputs.SetIndentToLeft();
        ImGui.SetNextItemWidth(fontSize * 11);
        ImGui.InputTextWithHint("##lobbyFilter", "Name or title", ref _lobbyFilter, 64);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(fontSize * 7);
        if (ImGui.BeginCombo("##lobbyPurpose", _lobbyPurposeFilter))
        {
            if (ImGui.Selectable("All", _lobbyPurposeFilter == "All"))
                _lobbyPurposeFilter = "All";

            foreach (var option in SessionPurposes)
            {
                if (ImGui.Selectable(option, option == _lobbyPurposeFilter))
                    _lobbyPurposeFilter = option;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(useBackend ? CollabWebLobby.IsRefreshing : CollabLobby.IsRefreshing);
        if (ImGui.Button("Refresh"))
        {
            if (useBackend)
                CollabWebLobby.Refresh();
            else
                CollabLobby.Refresh(relayHost, relayPort);
        }

        ImGui.EndDisabled();

        var autoMatch = FindAutoMatch(rooms);
        ImGui.SameLine();
        ImGui.BeginDisabled(autoMatch == null);
        if (ImGui.Button("Auto match") && autoMatch != null)
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            CollabLobbyWhisper.Reset();
            JoinListedRoom(collab, autoMatch);
        }

        ImGui.EndDisabled();

        if (useBackend ? CollabWebLobby.IsRefreshing : CollabLobby.IsRefreshing)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("loading...");
        }

        if (!string.IsNullOrEmpty(useBackend ? CollabWebLobby.ErrorText : CollabLobby.ErrorText))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusWarning.Rgba);
            ImGui.TextWrapped(useBackend ? CollabWebLobby.ErrorText : CollabLobby.ErrorText);
            ImGui.PopStyleColor();
        }

        LobbyRoom? selected = null;
        var joinNow = false;
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        var tableHeight = ImGui.GetFrameHeightWithSpacing() * 9;
        if (ImGui.BeginTable("##collabLobby", 7, tableFlags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Party ID", ImGuiTableColumnFlags.WidthFixed, fontSize * 4.5f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, fontSize * 6f);
            ImGui.TableSetupColumn("Race", ImGuiTableColumnFlags.WidthFixed, fontSize * 3.2f);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, fontSize * 4.2f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.28f);
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthFixed, fontSize * 4.5f);
            ImGui.TableHeadersRow();

            foreach (var room in rooms)
            {
                if (_lobbyPurposeFilter != "All" && !string.Equals(room.Purpose, _lobbyPurposeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_lobbyFilter.Length > 0
                    && !room.HostName.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase)
                    && !room.Title.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase)
                    && !room.Project.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isSelected = room.Code == _lobbySelectedCode;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // The party id doubles as the row selector (spanning all cells), like picking a
                // line in the matching list.
                ImGui.PushID(room.Code);
                if (ImGui.Selectable(room.Code, isSelected,
                                     ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _lobbySelectedCode = room.Code;
                    isSelected = true;
                    joinNow = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                }

                ImGui.PopID();
                if (isSelected)
                    selected = room;

                // Type: whether participants can edit (the host's spectator toggle).
                ImGui.TableNextColumn();
                ImGui.TextColored(room.Editable ? UiColors.StatusActivated : UiColors.StatusWarning,
                                  room.Editable ? "Controllable" : "Spectating");

                // Race: the host's platform.
                ImGui.TableNextColumn();
                var platform = room.Platform.Length > 0 ? room.Platform : "--";
                ImGui.TextUnformatted(platform);

                // Location: the account's region.
                ImGui.TableNextColumn();
                var region = room.Region.Length > 0 ? room.Region : "--";
                ImGui.TextUnformatted(region);

                // Name: the host, with the unverified marker.
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(room.HostName) ? "unnamed" : room.HostName);
                if (!room.HostVerified)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("?");
                    CustomComponents.TooltipForLastItem("This name is not confirmed by a Discord login.");
                }

                // Title, with a password marker and the project as a tooltip.
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(room.PasswordProtected ? $"{room.Title}  (password)" : room.Title);
                if (!string.IsNullOrEmpty(room.Project))
                    CustomComponents.TooltipForLastItem($"Project: {room.Project}");

                // Members: joined / capacity.
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{room.Members}/{room.MaxMembers}");
            }

            ImGui.EndTable();
        }

        if (rooms.Count == 0 && !(useBackend ? CollabWebLobby.IsRefreshing : CollabLobby.IsRefreshing)
            && string.IsNullOrEmpty(useBackend ? CollabWebLobby.ErrorText : CollabLobby.ErrorText))
            ImGui.TextDisabled(useBackend ? "No listed sessions on the backend right now." : "No listed sessions on this relay right now.");

        ImGui.BeginDisabled(selected == null);
        if (ImGui.Button("Join Session") || (joinNow && selected != null))
        {
            if (selected != null)
            {
                ShowControlMode.Activate(ShowControlMode.Collab);
                CollabLobbyWhisper.Reset();
                JoinListedRoom(collab, selected);
                _selectedTab = Tab.Session;
            }
        }

        ImGui.EndDisabled();
        if (selected is { PasswordProtected: true })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("uses the session password from Settings");
        }

        // Whisper the host of the picked session before joining.
        if (selected != null)
        {
            FormInputs.AddVerticalSpace(4);
            if (useBackend)
                CollabLobbyWhisper.PollBackend(selected.Code, collab.LocalIdentity.DisplayName);

            var whispers = CollabLobbyWhisper.Messages;
            if (whispers.Count > 0)
            {
                foreach (var whisper in whispers)
                {
                    if (whisper.Mine)
                        ImGui.TextColored(_whisperOwnColor, "You");
                    else if (whisper.From.Length > 0)
                        ImGui.TextUnformatted(selected.HostName);
                    else
                        ImGui.TextColored(UiColors.StatusWarning.Rgba, "!");

                    ImGui.SameLine();
                    ImGui.TextWrapped(whisper.Text);
                }
            }

            ImGui.SetNextItemWidth(-150);
            ImGui.InputTextWithHint("##lobbyWhisper", $"Whisper {selected.HostName}...", ref _lobbyWhisperInput, 256);
            ImGui.SameLine();
            ImGui.BeginDisabled(_lobbyWhisperInput.Length == 0);
            if (ImGui.Button("Whisper") && selected != null)
            {
                if (useBackend)
                {
                    CollabLobbyWhisper.SendViaBackend(selected.Code, collab.LocalIdentity.DisplayName, _lobbyWhisperInput);
                }
                else
                {
                    CollabLobbyWhisper.Send(CollabLobby.RelayEndpoint, selected.Code,
                                            collab.LocalIdentity.DisplayName, collab.LocalIdentity.Lang,
                                            _lobbyWhisperInput);
                }

                _lobbyWhisperInput = "";
            }

            ImGui.EndDisabled();
            ImGui.TextDisabled("Asks the host something before you join. They can answer on the Session tab.");
        }

        FormInputs.SetIndentToParameters();
    }

    /// <summary>The first listed session that passes the current filters; null when none does.</summary>
    private static LobbyRoom? FindAutoMatch(IReadOnlyList<LobbyRoom> rooms)
    {
        foreach (var room in rooms)
        {
            if (_lobbyPurposeFilter != "All" && !string.Equals(room.Purpose, _lobbyPurposeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_lobbyFilter.Length > 0
                && !room.HostName.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase)
                && !room.Title.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase)
                && !room.Project.Contains(_lobbyFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            return room;
        }

        return null;
    }

    /// <summary>
    /// Joins a room from either listing source: a backend row with a relay address joins on that
    /// relay; a row without one is a relay-free backend session and joins over WebRTC; the relay
    /// list joins on the relay the browser read it from.
    /// </summary>
    private static void JoinListedRoom(CollabManager manager, LobbyRoom room)
    {
        if (room.Relay.Length > 0)
        {
            ParseJoinAddress(room.Relay, CollabProtocol.DefaultRelayPort, out var joinHost, out var joinPort);
            manager.JoinViaRelay(joinHost, joinPort, room.Code, UserSettings.Config.CollabPassword ?? "");
            return;
        }

        if (room.Relay.Length == 0 && CollabWebLobby.IsConfigured)
        {
            manager.JoinViaBackend(room.Code);
            return;
        }

        ParseRelayAddress(CollabLobby.RelayEndpoint, out var relayHost, out var relayPort);
        manager.JoinViaRelay(relayHost, relayPort, room.Code, UserSettings.Config.CollabPassword ?? "");
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
}