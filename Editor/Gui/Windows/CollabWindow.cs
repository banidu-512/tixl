#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// Session home while collaborating: state and controls (host/join/leave), the participant
/// roster, and the session chat. Joining usually starts from the hub's "Online projects"
/// list; this window is where an active session is managed.
/// </summary>
[HelpUiID("CollabWindow")]
internal sealed class CollabWindow : Window
{
    internal CollabWindow()
    {
        Config.Title = "Collab";
        Config.Visible = false;
        WindowSizeOverride = new Vector2(380, 480);
    }

    internal override IReadOnlyList<Window> GetInstances() => [this];

    private string _chatInput = "";
    private int _lastChatCount;
    private bool _scrollToBottom = true;

    protected override void DrawContent()
    {
        var collab = CollabManager.Instance;
        if (!collab.IsEnabled)
        {
            ImGui.TextDisabled("Collaboration is turned off.");
            ImGui.TextDisabled("Enable it in Settings > Collaboration.");
            return;
        }

        DrawSessionHeader(collab);
        DrawDivider();

        if (collab.State == CollabManager.SessionState.Off)
        {
            ImGui.TextDisabled("Not in a session. Host one below or join from the");
            ImGui.TextDisabled("hub's 'Online projects' list.");
            FormInputs.AddVerticalSpace(8);
            DrawHostControls(collab);
            return;
        }

        DrawHostPreview(collab);
        DrawRoster(collab);
        DrawDivider();
        DrawChat(collab);
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

            FormInputs.AddVerticalSpace(4);
            if (ImGui.Button("Leave Session"))
                collab.Leave(manual: true);
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
        if (ImGui.Button("Host Session on This Machine"))
        {
            collab.StartHost(UserSettings.Config.CollabPort,
                             UserSettings.Config.CollabPassword ?? "",
                             UserSettings.Config.CollabLanDiscovery,
                             CollabManager.ConfiguredShareProject);
        }
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

        // Only the host can remove someone; the confirmation follows below the chips.
        if (CollabManager.Instance.State == CollabManager.SessionState.Hosting && CustomComponents.DrawMenuItem(3, "Kick"))
            _pendingKickPeerId = _menuPeerId;
    }

    private static int _pendingKickPeerId = -1;

    /** The participant whose chip opened the context menu, read by the menu's static draw callback. */
    private static int _menuPeerId;

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

    private void DrawChat(CollabManager collab)
    {
        FormInputs.AddSectionHeader("Chat");

        var log = collab.ChatLog;
        var uiScale = T3Ui.UiScaleFactor;
        var height = Math.Max(80 * uiScale, ImGui.GetContentRegionAvail().Y - 30 * uiScale);
        if (ImGui.BeginChild("##chatlog", new Vector2(0, height), ImGuiChildFlags.Borders))
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
}
