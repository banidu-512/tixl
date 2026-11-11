#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Collab;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.Windows.Layouts;
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

    // ---------------------------------------------------------------------------------------
    // Small drawing helpers: a consistent, quieter look across all three tabs
    // ---------------------------------------------------------------------------------------

    private static readonly System.Numerics.Vector4 _accentColor = new(0.62f, 0.42f, 0.85f, 1f);

    /// <summary>A section title with an accent bar to its left and breathing room above it.</summary>
    private static void SectionHeader(string title)
    {
        FormInputs.AddVerticalSpace(10);
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(title);
        var barWidth = 3f * T3Ui.UiScaleFactor;
        dl.AddRectFilled(new Vector2(pos.X, pos.Y + 1), new Vector2(pos.X + barWidth, pos.Y + size.Y - 1),
                         UiColors.StatusActivated.Fade(0.9f), 1.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + barWidth + 6f * T3Ui.UiScaleFactor);
        ImGui.TextUnformatted(title);
        FormInputs.AddVerticalSpace(2);
    }

    /// <summary>A small rounded status pill (online/offline, controllable/spectating, ...).</summary>
    private static void Badge(string text, Color color)
    {
        var dl = ImGui.GetWindowDrawList();
        var padding = new Vector2(6, 1.5f) * T3Ui.UiScaleFactor;
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var pos = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(pos, pos + size, color.Fade(0.18f), size.Y * 0.5f);
        dl.AddText(pos + padding, color, text);
        ImGui.Dummy(size);
    }

    /// <summary>Muted helper text with a small left indent, wrapped to the content width.</summary>
    private static void Hint(string text)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4 * T3Ui.UiScaleFactor);
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }


    /// <summary>The window's three areas, matching the party/remote/boygroup split.</summary>
    private enum Tab
    {
        Party,
        Remote,
        Boygroup,
    }

    /// <summary>The Party tab's sub-tabs: the session view and the chat.</summary>
    private enum PartyTab
    {
        Info,
        Chat,
    }

    private static Tab _selectedTab = Tab.Party;
    private static PartyTab _partyTab = PartyTab.Info;

    /// <summary>Selects a tab by name (Party, Remote, Boygroup, or the Party sub-tabs Session/Chat);
    /// used by the debug bridge.</summary>
    internal bool TrySelectTab(string name)
    {
        if (string.Equals(name, "Chat", StringComparison.OrdinalIgnoreCase))
        {
            _selectedTab = Tab.Party;
            _partyTab = PartyTab.Chat;
            return true;
        }

        if (string.Equals(name, "Session", StringComparison.OrdinalIgnoreCase))
        {
            _selectedTab = Tab.Party;
            _partyTab = PartyTab.Info;
            return true;
        }

        if (!Enum.TryParse<Tab>(name, true, out var tab))
            return false;

        _selectedTab = tab;
        return true;
    }

    protected override void DrawContent()
    {
        var collab = CollabManager.Instance;
        UpdateRemoteJoin();
        DrainRemoteUiActions();
        if (!collab.IsEnabled)
        {
            ImGui.TextDisabled("Collaboration is turned off.");
            ImGui.TextDisabled("Enable it in Settings > Collaboration.");
            return;
        }

        DrawTabButtons();
        FormInputs.AddVerticalSpace(4);
        DrawSubTabButtons(collab);
        FormInputs.AddVerticalSpace(2);

        switch (_selectedTab)
        {
            case Tab.Party:
                if (_partyTab == PartyTab.Chat)
                    DrawChatTab(collab);
                else
                    DrawPartyTab(collab);

                break;

            case Tab.Remote:
                DrawRemoteTab(collab);
                break;

            case Tab.Boygroup:
                DrawBoygroupTab(collab);
                break;
        }
    }

    /// <summary>The active tab's sub-tabs, drawn as a small breadcrumb row under the main tabs.</summary>
    private static void DrawSubTabButtons(CollabManager collab)
    {
        switch (_selectedTab)
        {
            case Tab.Party:
            {
                var infoLabel = collab.State == CollabManager.SessionState.Off ? "Party search" : "Party infos";
                SubTab(ref _partyTab, PartyTab.Chat, "Chat");
                ImGui.SameLine(0, 4);
                SubTab(ref _partyTab, PartyTab.Info, infoLabel);
                break;
            }

            case Tab.Remote:
            case Tab.Boygroup:
                ImGui.TextDisabled("Management");
                break;
        }
    }

    private static void SubTab(ref PartyTab current, PartyTab value, string label)
    {
        var active = current == value;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, UiColors.StatusActivated.Rgba);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiColors.StatusActivated.Rgba);
        }

        if (ImGui.Button(label))
            current = value;

        if (active)
            ImGui.PopStyleColor(2);
    }

    /// <summary>Three small toggle buttons in the top-right corner, like the party-matching
    /// reference. The active one is highlighted; a click switches the tab. SetCursorPosX takes
    /// window-LOCAL coordinates - screen-space math here placed the row off-window.</summary>
    private static void DrawTabButtons()
    {
        var labels = new[] { "Party", "Remote", "Boygroup" };
        var tabs = new[] { Tab.Party, Tab.Remote, Tab.Boygroup };

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

        // -- Type: Controllable (everyone edits) or Spectating (the host drops their changes).
        FormInputs.AddVerticalSpace(6);
        ImGui.TextDisabled("Type");
        var isHosting = collab.State == CollabManager.SessionState.Hosting;
        var controllable = isHosting ? collab.ParticipantsCanEdit : UserSettings.Config.CollabPartyControllable;

        if (ImGui.RadioButton("Controllable", controllable) && !controllable)
        {
            UserSettings.Config.CollabPartyControllable = true;
            UserSettings.Save();
            if (isHosting)
                collab.SetParticipantsCanEdit(true);
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Spectating", !controllable) && controllable)
        {
            UserSettings.Config.CollabPartyControllable = false;
            UserSettings.Save();
            if (isHosting)
                collab.SetParticipantsCanEdit(false);
        }

        CustomComponents.TooltipForLastItem("""
                                            Controllable: everyone can change the shared project.
                                            Spectating: participants see your cursors, presence and chat, but
                                            the host drops their graph changes.
                                            """);

        // -- Reach: Local (LAN) or Online (through the backend party server).
        FormInputs.AddVerticalSpace(6);
        ImGui.TextDisabled("Reach");
        var online = UserSettings.Config.CollabPartyOnline;
        var local = !online;

        if (ImGui.Checkbox("Local (same network)", ref local) && local)
        {
            UserSettings.Config.CollabPartyOnline = false;
            UserSettings.Save();
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Online (through the server)", ref online) && online)
        {
            UserSettings.Config.CollabPartyOnline = true;
            UserSettings.Save();
        }

        CustomComponents.TooltipForLastItem("""
                                            Local: people on the same network join directly (no internet).
                                            Online: the party is listed on the backend server and joiners
                                            connect over WebRTC from anywhere.
                                            """);

        // -- Form the party.
        FormInputs.AddVerticalSpace(6);
        var wantOnline = UserSettings.Config.CollabPartyOnline;
        var canForm = !wantOnline || CollabWebLobby.IsConfigured;
        var projectName = ProjectView.Focused?.OpenedProject.Package.DisplayName ?? "";

        ImGui.BeginDisabled(!canForm);
        if (ImGui.Button("Form Party", new Vector2(140, 0)))
        {
            ShowControlMode.Activate(ShowControlMode.Collab);
            if (wantOnline)
            {
                collab.StartHostViaBackend();
            }
            else
            {
                collab.StartHost(UserSettings.Config.CollabPort,
                                 UserSettings.Config.CollabPassword ?? "",
                                 UserSettings.Config.CollabLanDiscovery,
                                 projectName.Length > 0 ? projectName : null);
            }

            _selectedTab = Tab.Party;
            _partyTab = PartyTab.Info;
        }

        ImGui.EndDisabled();
        if (!canForm)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Set the Backend URL in Settings first");
        }

        // The project context note: parties are formed for the open project and outlive browsing.
        FormInputs.AddVerticalSpace(6);
        ImGui.TextDisabled("The party stays alive while you browse other projects,");
        ImGui.TextDisabled("until you close the party, unload the project or exit TiXL.");
    }

    // ---------------------------------------------------------------------------------------
    // Remote tab: pair this computer, and manage the owner's other installations
    // ---------------------------------------------------------------------------------------

    private static string _pairCodeInput = "";
    private static string _pairError = "";
    private static string _pairingCode = "";
    private static DateTime _pairingExpires = DateTime.MinValue;
    private static string _pairingStatus = "";
    private static long _nextPairPollTicks;
    private static long _nextDeviceRefreshTicks;
    private static string _remoteClaim = "";
    private static long _nextConnectPollTicks;

    private static void DrawRemoteTab(CollabManager collab)
    {
        DrainRemoteUiActions();
        UpdateDeviceList();
        DrawThisComputer();
        FormInputs.AddVerticalSpace(8);
        ImGui.Separator();
        FormInputs.AddVerticalSpace(8);
        DrawMyComputers(collab);
    }

    /// <summary>Installation side: pairing, consent and the name the owner sees.</summary>
    private static void DrawThisComputer()
    {
        SectionHeader("This computer");

        if (CollabRemote.IsPaired)
        {
            ImGui.TextDisabled("Paired with");
            ImGui.SameLine();
            ImGui.TextColored(UiColors.StatusActivated, CollabRemote.PairedOwner);

            var allow = UserSettings.Config.CollabRemoteAllow;
            if (ImGui.Checkbox("Allow remote sessions on this computer", ref allow))
            {
                UserSettings.Config.CollabRemoteAllow = allow;
                UserSettings.Save();
            }

            CustomComponents.TooltipForLastItem("""
                                                While this is on, the owner can connect to this
                                                computer and work on the running project. Turning it
                                                off makes the computer look offline to them.
                                                """);

            var name = UserSettings.Config.CollabRemoteName;
            ImGui.SetNextItemWidth(-110);
            if (ImGui.InputTextWithHint("##remoteName", CollabRemote.DeviceName, ref name, 48))
            {
                UserSettings.Config.CollabRemoteName = name;
                UserSettings.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("Forget pairing"))
                CollabRemote.ForgetInstallation();

            if (CollabRemote.InstallationStatus.Length > 0)
                ImGui.TextDisabled(CollabRemote.InstallationStatus);

            return;
        }

        CustomComponents.HelpText("""
                                  To let yourself (or a colleague) reach this computer, pair it once:
                                  on the controlling machine open this tab, log in with Discord and press
                                  "Add remote TiXL". Then type the 6-digit code it shows here.
                                  """);

        ImGui.SetNextItemWidth(96);
        ImGui.InputTextWithHint("##pairCode", "6-digit code", ref _pairCodeInput, 6);
        ImGui.SameLine();
        ImGui.BeginDisabled(_pairCodeInput.Trim().Length < 6);
        if (ImGui.Button("Pair this computer"))
        {
            var code = _pairCodeInput.Trim();
            _pairError = "";
            _ = Task.Run(async () =>
                             {
                                 var error = await CollabRemote.PairInstallationAsync(code, CancellationToken.None);
                                 _remoteUiActions.Enqueue(() =>
                                                          {
                                                              if (error != null)
                                                                  _pairError = error;
                                                              else
                                                                  _pairCodeInput = "";
                                                          });
                             });
        }

        ImGui.EndDisabled();
        if (_pairError.Length > 0)
            ImGui.TextColored(UiColors.StatusWarning, _pairError);
    }

    /// <summary>Controller side: the owner's installations with connect controls.</summary>
    private static void DrawMyComputers(CollabManager collab)
    {
        SectionHeader("My computers");

        if (!CollabDiscordAuth.HasValidToken)
        {
            CustomComponents.HelpText("""
                                      Log in with Discord (Settings > Collaboration) to see and connect
                                      to your paired computers.
                                      """);
            return;
        }

        if (ImGui.Button("Add remote TiXL"))
            StartPairing();

        if (_pairingCode.Length > 0)
            DrawPairingCode();

        if (CollabRemote.IsLoading)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("refreshing...");
        }

        if (CollabRemote.ListError.Length > 0)
        {
            ImGui.TextColored(UiColors.StatusWarning, CollabRemote.ListError);
            return;
        }

        var devices = CollabRemote.Devices;
        if (devices.Count == 0)
        {
            ImGui.TextDisabled("No computers paired yet.");
            return;
        }

        FormInputs.AddVerticalSpace(4);
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("##remoteDevices", 4, flags))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 14);
        ImGui.TableSetupColumn("Computer", ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableHeadersRow();

        foreach (var device in devices)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(device.Online ? UiColors.StatusActivated : UiColors.TextMuted, "●");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(device.Name) ? device.Id : device.Name);
            ImGui.SameLine();
            if (device.Online)
                Badge("online", UiColors.StatusActivated);
            else
                Badge(LastSeen(device.LastSeen), UiColors.TextMuted);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(device.Project) ? "--" : device.Project);

            ImGui.TableNextColumn();
            ImGui.PushID(device.Id);
            if (device.Room.Length > 0)
            {
                if (ImGui.SmallButton("Join session"))
                    collab.JoinViaBackend(device.Room);

                ImGui.SameLine();
                if (ImGui.SmallButton("Stop"))
                    DisconnectFrom(device.Id);
            }
            else
            {
                ImGui.BeginDisabled(!device.Online || !device.Enabled);
                if (ImGui.SmallButton("Connect"))
                    ConnectTo(device);
                ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.SmallButton("Rename"))
                    _renameDeviceId = device.Id;
            }

            ImGui.PopID();
            DrawRenamePopup(device);
        }

        ImGui.EndTable();
    }

    /// <summary>Opens the Remote tab and starts a pairing (debug bridge / menu action).</summary>
    internal static void StartRemotePairing()
    {
        _selectedTab = Tab.Remote;
        if (_pairingCode.Length > 0)
            return;

        StartPairing();
    }

    /// <summary>The code currently shown on the Remote tab ("" when none).</summary>
    internal static string RemotePairingCode => _pairingCode;

    /// <summary>Connects to a paired computer by id (debug bridge).</summary>
    internal static void ConnectRemoteDevice(string deviceId)
    {
        foreach (var device in CollabRemote.Devices)
        {
            if (device.Id != deviceId)
                continue;

            ConnectTo(device);
            return;
        }

        // Not cached yet (e.g. a fresh editor): fetch the list, then connect when it arrives.
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await CollabRemote.RefreshDevicesAsync(CancellationToken.None);
                                 _remoteUiActions.Enqueue(() =>
                                                              {
                                                                  foreach (var device in CollabRemote.Devices)
                                                                  {
                                                                      if (device.Id != deviceId)
                                                                          continue;

                                                                      ConnectTo(device);
                                                                      return;
                                                                  }

                                                                  _pairError = $"No paired computer '{deviceId}' in the device list";
                                                              });
                             }
                             catch (Exception e)
                             {
                                 _remoteUiActions.Enqueue(() => _pairError = e.Message);
                             }
                         });
    }

    private static string _renameDeviceId = "";
    private static string _renameInput = "";

    private static void DrawRenamePopup(CollabRemoteApi.Device device)
    {
        if (_renameDeviceId != device.Id)
            return;

        if (_renameInput.Length == 0)
            _renameInput = device.Name;

        ImGui.OpenPopup("Rename computer");
        if (!ImGui.BeginPopup("Rename computer"))
            return;

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##rename", ref _renameInput, 48);
        if (ImGui.Button("Save"))
        {
            var name = _renameInput.Trim();
            _renameDeviceId = "";
            if (name.Length > 0)
                RunOwnerAction(token => CollabRemoteApi.SetAsync(token, device.Id, null, name, CancellationToken.None));

            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _renameDeviceId = "";
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static string LastSeen(long unixSeconds)
    {
        if (unixSeconds <= 0)
            return "never";

        var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (ago.TotalMinutes < 1)
            return "offline just now";
        if (ago.TotalHours < 1)
            return $"offline {ago.TotalMinutes:0} min";

        return $"offline {ago.TotalHours:0} h";
    }

    /// <summary>Starts a pairing and shows the code; polled until the installation redeems it.</summary>
    private static void StartPairing()
    {
        _pairingStatus = "Creating a code...";
        _pairError = "";
        var token = CollabDiscordAuth.GetValidAccessToken();
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 var start = await CollabRemoteApi.StartPairingAsync(token, CancellationToken.None);
                                 _remoteUiActions.Enqueue(() =>
                                                          {
                                                              _pairingCode = start.Code;
                                                              _pairingExpires = start.ExpiresUtc.ToLocalTime();
                                                              _pairingStatus = "Waiting for the other computer...";
                                                              _nextPairPollTicks = 0;
                                                          });
                             }
                             catch (Exception e)
                             {
                                 _remoteUiActions.Enqueue(() =>
                                                          {
                                                              _pairingStatus = "";
                                                              _pairError = e.Message;
                                                          });
                             }
                         });
    }

    private static void DrawPairingCode()
    {
        var remaining = _pairingExpires - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            _pairingCode = "";
            _pairingStatus = "";
            _pairError = "The code expired - press Add remote TiXL again.";
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("code");
        ImGui.SameLine();
        ImGui.PushFont(Fonts.FontBold);
        ImGui.TextColored(_accentColor, _pairingCode);
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextDisabled($"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00} left  ·  {_pairingStatus}");

        if (Environment.TickCount64 >= _nextPairPollTicks)
        {
            _nextPairPollTicks = Environment.TickCount64 + 2000;
            var token = CollabDiscordAuth.GetValidAccessToken();
            var code = _pairingCode;
            _ = Task.Run(async () =>
                             {
                                 try
                                 {
                                     var status = await CollabRemoteApi.PairStatusAsync(token, code, CancellationToken.None);
                                     if (status.State != CollabRemoteApi.PairState.Waiting)
                                     {
                                         _remoteUiActions.Enqueue(() =>
                                                                  {
                                                                      _pairingCode = "";
                                                                      _pairingStatus = "";
                                                                      _pairError = status.State == CollabRemoteApi.PairState.Paired
                                                                                       ? $"Paired with {status.DeviceName}"
                                                                                       : "The code expired";
                                                                  });
                                     }
                                 }
                                 catch
                                 {
                                     // Transient; the next poll retries.
                                 }
                             });
        }
    }

    /// <summary>Owner: asks a device to host, then joins the room it reports.</summary>
    private static void ConnectTo(CollabRemoteApi.Device device)
    {
        var token = CollabDiscordAuth.GetValidAccessToken();
        _pairError = "";
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 var result = await CollabRemoteApi.ConnectAsync(token, device.Id, CancellationToken.None);
                                 _remoteUiActions.Enqueue(() =>
                                                          {
                                                              _remoteClaim = result.Claim;
                                                              _remoteDeviceId = device.Id;
                                                              _nextConnectPollTicks = Environment.TickCount64 + 2500;
                                                          });
                             }
                             catch (Exception e)
                             {
                                 _remoteUiActions.Enqueue(() => _pairError = e.Message);
                             }
                         });
    }

    private static string _remoteDeviceId = "";

    private static void DisconnectFrom(string deviceId)
    {
        RunOwnerAction(token => CollabRemoteApi.DisconnectAsync(token, deviceId, CancellationToken.None));
        if (_remoteDeviceId == deviceId && CollabManager.Instance.State != CollabManager.SessionState.Off)
            CollabManager.Instance.Leave(manual: true);
    }

    private static void RunOwnerAction(Func<string, Task> action)
    {
        var token = CollabDiscordAuth.GetValidAccessToken();
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await action(token);
                             }
                             catch (Exception e)
                             {
                                 _remoteUiActions.Enqueue(() => _pairError = e.Message);
                             }
                         });
    }

    /// <summary>Per-frame: joins the remote party once the installation reports its room.</summary>
    internal static void UpdateRemoteJoin()
    {
        if (_remoteClaim.Length == 0 || Environment.TickCount64 < _nextConnectPollTicks)
            return;

        _nextConnectPollTicks = Environment.TickCount64 + 2500;
        var collab = CollabManager.Instance;
        if (collab.State != CollabManager.SessionState.Off)
            return;

        var deviceId = _remoteDeviceId;
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 var token = CollabDiscordAuth.GetValidAccessToken();
                                 if (token.Length == 0)
                                     return;

                                 var devices = await CollabRemoteApi.ListAsync(token, CancellationToken.None);
                                 foreach (var device in devices)
                                 {
                                     if (device.Id != deviceId || device.Room.Length == 0)
                                         continue;

                                     var room = device.Room;
                                     var claim = _remoteClaim;
                                     _remoteUiActions.Enqueue(() =>
                                                              {
                                                                  _remoteClaim = "";
                                                                  CollabManager.Instance.JoinViaBackend(room);
                                                              });
                                     await CollabRemoteApi.AttachAsync(token, deviceId, claim, CancellationToken.None);
                                     return;
                                 }
                             }
                             catch
                             {
                                 // The installation may still be starting; the next poll retries.
                             }
                         });
    }

    /// <summary>UI-thread queue for the remote calls' results (they run on pool threads).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _remoteUiActions = new();

    internal static void DrainRemoteUiActions()
    {
        while (_remoteUiActions.TryDequeue(out var action))
            action();
    }

    /// <summary>Refreshes the device list while the Remote tab is visible.</summary>
    private static void UpdateDeviceList()
    {
        if (_selectedTab != Tab.Remote || Environment.TickCount64 < _nextDeviceRefreshTicks)
            return;

        _nextDeviceRefreshTicks = Environment.TickCount64 + 4000;
        _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await CollabRemote.RefreshDevicesAsync(CancellationToken.None);
                             }
                             catch
                             {
                                 // Reported through ListError.
                             }
                         });
    }

    // ---------------------------------------------------------------------------------------
    // Boygroup tab: the show distribution's state, with a shortcut to its settings
    // ---------------------------------------------------------------------------------------

    private static void DrawBoygroupTab(CollabManager collab)
    {
        var boygroup = BoygroupManager.Instance;
        SectionHeader("Boygrouping");

        var state = boygroup.State;
        if (state == BoygroupManager.BoygroupState.Off)
        {
            ImGui.TextDisabled("Boygrouping is not running on this machine.");
            CustomComponents.HelpText("""
                                      Boygrouping distributes a show from this master to the render
                                      nodes of the installation. Configure the role and the node
                                      discovery in the settings.
                                      """);
        }
        else
        {
            FormInputs.DrawInputLabel("Role");
            ImGui.TextColored(UiColors.StatusActivated, state == BoygroupManager.BoygroupState.Server ? "Master" : "Render node");

            var summary = boygroup.InputRelaySummary;
            if (!string.IsNullOrEmpty(summary))
                ImGui.TextDisabled(summary);
        }

        FormInputs.AddVerticalSpace(8);
        if (ImGui.Button("Boygroup settings"))
        {
            var settings = WindowManager.SettingsWindow;
            settings.Config.Visible = true;
            settings.TrySelectCategory("Boygroup");
        }

        if (state != BoygroupManager.BoygroupState.Off)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop boygrouping"))
                boygroup.Leave(manual: true);
        }
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

            // The shared feed is the SELECTED operator's video (webcam / NDI / Spout / file) - never
            // an output window; everyone renders their own output locally.
            var shareFeed = UserSettings.Config.CollabSharePreview;
            if (ImGui.Checkbox("Share a video feed with the party", ref shareFeed))
            {
                UserSettings.Config.CollabSharePreview = shareFeed;
                UserSettings.Save();
                if (!shareFeed)
                    collab.StopSharingFeed();
            }

            CustomComponents.TooltipForLastItem("""
                                                Select a webcam, NDI, Spout or video operator in the graph,
                                                then press Share: its picture is sent to every participant as
                                                a live video track. Nothing is sent without a chosen operator.
                                                """);

            if (ImGui.SmallButton("Share selected operator"))
                collab.ShareSelectedOperatorAsFeed();

            if (collab.SharedFeedName.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"sharing {collab.SharedFeedName}  ·  {collab.VideoStreamer?.ViewerCount ?? 0} watching");
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop##feed"))
                    collab.StopSharingFeed();
            }
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
        SectionHeader(collab.State == CollabManager.SessionState.Hosting ? "Hosting" : "Session");
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
        SectionHeader($"Participants ({collab.Roster.Count + 1})");

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
                Badge(room.Editable ? "Controllable" : "Spectating",
                      room.Editable ? UiColors.StatusActivated : UiColors.StatusWarning);

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
                _selectedTab = Tab.Party;
            _partyTab = PartyTab.Info;
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