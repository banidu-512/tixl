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

        DrawSessionHeader(collab);

        ImGui.Dummy(new Vector2(4, 6));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(4, 6));

        if (collab.State == CollabManager.SessionState.Off)
        {
            ImGui.TextDisabled("Not in a session. Host one below or join from the");
            ImGui.TextDisabled("hub's 'Online projects' list.");
            ImGui.Dummy(new Vector2(4, 8));
            DrawHostControls(collab);
            return;
        }

        DrawRoster(collab);

        ImGui.Dummy(new Vector2(4, 6));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(4, 6));

        DrawChat(collab);
    }

    private static void DrawSessionHeader(CollabManager collab)
    {
        FormInputs.AddSectionHeader(collab.State == CollabManager.SessionState.Hosting ? "Hosting" : "Session");

        ImGui.TextWrapped(collab.StatusText);

        if (collab.State != CollabManager.SessionState.Off)
        {
            if (!string.IsNullOrEmpty(collab.ErrorText))
                ImGui.TextColored(UiColors.StatusWarning, collab.ErrorText);

            ImGui.Dummy(new Vector2(4, 4));
            if (ImGui.Button("Leave session"))
                collab.Leave();
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

        ImGui.Dummy(new Vector2(4, 4));
        if (ImGui.Button("Host session on this machine"))
        {
            collab.StartHost(UserSettings.Config.CollabPort,
                             UserSettings.Config.CollabPassword ?? "",
                             UserSettings.Config.CollabLanDiscovery);
        }
    }

    private static void DrawRoster(CollabManager collab)
    {
        FormInputs.AddSectionHeader($"Participants ({collab.Roster.Count + 1})");

        var dl = ImGui.GetWindowDrawList();
        var isHost = collab.State == CollabManager.SessionState.Hosting;

        DrawNameChip(dl, "You" + (isHost ? "  (host)" : ""), UiColors.StatusActivated);

        foreach (var (_, name) in collab.Roster)
        {
            DrawNameChip(dl, name + (!isHost ? "  (host)" : ""), UiColors.Text);
        }
    }

    private static void DrawNameChip(ImDrawListPtr dl, string label, Color color)
    {
        var size = ImGui.CalcTextSize(label);
        var pos = ImGui.GetCursorScreenPos();
        var padding = new Vector2(6, 3);
        dl.AddRectFilled(pos, pos + size + padding * 2, UiColors.ForegroundFull.Fade(0.08f), 4);
        dl.AddText(pos + padding, color, label);
        ImGui.Dummy(size + padding * 2 + new Vector2(4, 0));
        ImGui.SameLine();
    }

    private void DrawChat(CollabManager collab)
    {
        FormInputs.AddSectionHeader("Chat");

        var log = collab.ChatLog;
        var height = Math.Max(80, ImGui.GetContentRegionAvail().Y - 30);
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

        ImGui.SetNextItemWidth(-70);
        var send = ImGui.InputTextWithHint("##chat", "Message…", ref _chatInput, 256,
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
