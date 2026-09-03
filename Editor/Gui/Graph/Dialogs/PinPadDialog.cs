using ImGuiNET;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Commands;

namespace T3.Editor.Gui.Dialogs;

/// <summary>
/// Numeric pinpad used to lock selected operators with a PIN and to unlock them again.
/// Set-PIN mode is a two-step entry (new PIN, then confirm); Unlock mode verifies a
/// single entry against the stored hash. Target operators are stored as ids and
/// re-resolved on submit, so a stale dialog cannot mutate a vanished selection.
/// Repeated wrong attempts trigger a short escalating timeout so the lock cannot be
/// opened by mashing guesses.
/// </summary>
internal sealed class PinPadDialog : ModalDialog
{
    internal void OpenForLock(List<SymbolUi.Child> childUis)
    {
        if (!TryCollectTargets(childUis, out var compositionSymbolId, out var childIds))
            return;

        _compositionSymbolId = compositionSymbolId;
        _targetChildIds = childIds;
        _mode = Modes.SetPin;
        ShowNextFrame();
    }

    internal void OpenForUnlock(List<SymbolUi.Child> childUis)
    {
        var lockedChildUis = childUis.FindAll(c => c.IsLocked);
        if (!TryCollectTargets(lockedChildUis, out var compositionSymbolId, out var childIds))
            return;

        _compositionSymbolId = compositionSymbolId;
        _targetChildIds = childIds;
        _mode = Modes.Unlock;
        ShowNextFrame();
    }

    protected override void OnShowNextFrame()
    {
        _pin = string.Empty;
        _pinToConfirm = string.Empty;
        _error = string.Empty;
        _stage = Stages.EnterNewPin;
        _failedAttempts = 0;
        _cooldownUntil = 0;
        _lastDigit = '\0';
        _lastDigitAt = double.NegativeInfinity;
        _lastWrongPinAt = double.NegativeInfinity;
        _isOpen = true;
    }

    internal void Draw()
    {
        if (_targetChildIds.Count == 0)
            return;

        var scale = T3Ui.UiScaleFactor;
        var buttonSize = new Vector2(54, 34) * scale;
        var spacing = 6 * scale;
        var keypadWidth = 3 * buttonSize.X + 2 * spacing;
        DialogSize = new Vector2(keypadWidth / scale + 2 * 20, 100);

        var title = _mode == Modes.SetPin ? "Lock with PIN" : "Unlock";
        if (BeginDialog(title))
        {
            DrawHeading();
            DrawPinDisplay(keypadWidth, scale);
            DrawMessageLine();
            FormInputs.AddVerticalSpace(6);
            DrawKeypad(buttonSize, spacing, scale);

            FormInputs.AddVerticalSpace(6);
            if (CustomComponents.DrawCtaButton("Cancel", Icon.None, CustomComponents.ButtonStates.Default))
            {
                ImGui.CloseCurrentPopup();
            }

            if (_pin.Length > 0)
            {
                ImGui.SameLine();
                if (CustomComponents.DrawCtaButton("Clear", Icon.None, CustomComponents.ButtonStates.Default))
                {
                    ClearEntry();
                }
            }

            HandleKeyboardInput();

            EndDialogContent();
        }
        else if (_isOpen)
        {
            // Dialog closed without a decision (Escape, window X) - drop the stale targets
            _isOpen = false;
            _targetChildIds = [];
        }

        EndDialog();
    }

    private void DrawHeading()
    {
        var heading = _mode switch
                          {
                              Modes.SetPin when _stage == Stages.EnterNewPin => "Enter New PIN",
                              Modes.SetPin                                 => "Confirm PIN",
                              _                                            => "Enter PIN to Unlock",
                          };
        Icon.Locked.DrawAtCursor(UiColors.StatusAttention);
        ImGui.SameLine();
        CustomComponents.StylizedText(heading, Fonts.FontBold, UiColors.Text);

        var targetCount = _targetChildIds.Count;
        var hint = _mode == Modes.SetPin
                       ? _stage == Stages.EnterNewPin
                             ? $"Locking {targetCount} operator{(targetCount == 1 ? "" : "s")} - moving and editing will be disabled and the undo history cleared."
                             : "Repeat the PIN to confirm."
                       : $"Enter the PIN to unlock {targetCount} operator{(targetCount == 1 ? "" : "s")}. All locked operators sharing the PIN are released together.";
        FormInputs.AddHint(hint);
    }

    private void DrawPinDisplay(float width, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var displayPos = ImGui.GetCursorScreenPos();
        var displaySize = new Vector2(width, 30 * scale);

        // Wrong-PIN feedback: the display shakes sideways and flashes
        var sinceWrongPin = (float)(ImGui.GetTime() - _lastWrongPinAt);
        var wrongPinFlash = WrongPinFlashDuration - sinceWrongPin;
        var shake = 0f;
        if (wrongPinFlash > 0)
        {
            shake = MathF.Sin(sinceWrongPin * 55f) * 6f * scale * (wrongPinFlash / WrongPinFlashDuration);
        }

        displayPos = new Vector2(displayPos.X + shake, displayPos.Y);
        var flashColor = UiColors.StatusAttention.Fade(0.9f);

        drawList.AddRectFilled(displayPos, displayPos + displaySize, UiColors.BackgroundButton.Fade(0.5f), 6 * scale);
        drawList.AddRect(displayPos, displayPos + displaySize,
                         wrongPinFlash > 0 ? flashColor : UiColors.WidgetActiveLine.Fade(0.15f), 6 * scale);

        var dotRadius = 3 * scale;
        var gap = 14 * scale;
        var startX = displayPos.X + (displaySize.X - (SymbolUi.Child.MaxPinLength - 1) * gap) / 2;
        var centerY = displayPos.Y + displaySize.Y / 2;
        for (var i = 0; i < SymbolUi.Child.MaxPinLength; i++)
        {
            var center = new Vector2(startX + i * gap, centerY);
            if (i < _pin.Length)
            {
                drawList.AddCircleFilled(center, dotRadius, wrongPinFlash > 0 ? flashColor : UiColors.ForegroundFull);
            }
            else
            {
                drawList.AddCircle(center, dotRadius, UiColors.TextMuted.Fade(0.35f));
            }
        }

        // Briefly echo the last entered digit so keys and clicks are visibly registered
        var sinceDigit = (float)(ImGui.GetTime() - _lastDigitAt);
        if (_lastDigit != '\0' && sinceDigit < DigitEchoDuration)
        {
            var echoFade = 1 - sinceDigit / DigitEchoDuration;
            var echoSize = 17f * scale;
            var echoPos = new Vector2(displayPos.X + displaySize.X - 16 * scale, centerY - echoSize * 0.55f);
            drawList.AddText(ImGui.GetFont(), echoSize, echoPos, UiColors.TextMuted.Fade(echoFade * 0.9f),
                             _lastDigit.ToString());
        }

        ImGui.Dummy(displaySize);
    }

    /// <summary>Always reserves one line so the keypad doesn't jump when a message appears.</summary>
    private void DrawMessageLine()
    {
        ImGui.PushFont(Fonts.FontSmall);
        var message = GetStatusMessage();
        if (string.IsNullOrEmpty(message))
        {
            ImGui.Dummy(new Vector2(1, ImGui.GetTextLineHeight()));
        }
        else
        {
            var isWarning = !string.IsNullOrEmpty(_error) || IsCoolingDown();
            ImGui.PushStyleColor(ImGuiCol.Text, isWarning ? UiColors.StatusAttention.Rgba : UiColors.TextMuted.Rgba);
            ImGui.TextUnformatted(message);
            ImGui.PopStyleColor();
        }

        ImGui.PopFont();
    }

    private string GetStatusMessage()
    {
        if (IsCoolingDown())
            return $"Too many wrong attempts - waiting {CooldownRemainingSeconds:0}s...";

        if (!string.IsNullOrEmpty(_error))
            return _error;

        return _mode switch
                   {
                       Modes.SetPin when _stage == Stages.EnterNewPin => $"Use {SymbolUi.Child.MinPinLength} to {SymbolUi.Child.MaxPinLength} digits, then press OK.",
                       Modes.SetPin                                   => "Waiting for the repeated PIN.",
                       _                                              => string.Empty,
                   };
    }

    private void DrawKeypad(Vector2 buttonSize, float spacing, float scale)
    {
        ImGui.PushFont(Fonts.FontLarge);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * scale);
        ImGui.PushStyleColor(ImGuiCol.Button, UiColors.BackgroundButton.Fade(0.6f).Rgba);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiColors.BackgroundHover.Rgba);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiColors.StatusActivated.Fade(0.8f).Rgba);

        var coolingDown = IsCoolingDown();
        if (coolingDown)
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.4f);

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                if (col > 0)
                    ImGui.SameLine(0, spacing);

                var index = row * 3 + col;
                switch (index)
                {
                    case 9:
                        if (ImGui.Button("Del", buttonSize))
                        {
                            RemoveLastDigit();
                        }

                        break;
                    case 10:
                        if (ImGui.Button("0", buttonSize))
                        {
                            AppendDigit("0");
                        }

                        break;
                    case 11:
                        DrawOkButton(buttonSize);
                        break;
                    default:
                        var digit = (index + 1).ToString();
                        if (ImGui.Button(digit, buttonSize))
                        {
                            AppendDigit(digit);
                        }

                        break;
                }
            }
        }

        if (coolingDown)
            ImGui.PopStyleVar();

        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        ImGui.PopFont();
    }

    private void DrawOkButton(Vector2 buttonSize)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, UiColors.StatusControlled.Fade(0.35f).Rgba);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiColors.StatusControlled.Fade(0.6f).Rgba);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiColors.StatusControlled.Fade(0.8f).Rgba);
        if (ImGui.Button("OK", buttonSize))
        {
            Submit();
        }

        ImGui.PopStyleColor(3);
    }

    private void HandleKeyboardInput()
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            if (ImGui.IsKeyPressed(ImGuiKey._0 + digit, repeat: false)
                || ImGui.IsKeyPressed(ImGuiKey.Keypad0 + digit, repeat: false))
            {
                AppendDigit(digit.ToString());
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Backspace))
        {
            RemoveLastDigit();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
        {
            Submit();
        }
    }

    private void AppendDigit(string digit)
    {
        if (IsCoolingDown() || _pin.Length >= SymbolUi.Child.MaxPinLength)
            return;

        _pin += digit;
        _error = string.Empty;
        _lastDigit = digit[0];
        _lastDigitAt = ImGui.GetTime();
    }

    private void RemoveLastDigit()
    {
        if (_pin.Length > 0)
            _pin = _pin[..^1];
    }

    private void ClearEntry()
    {
        _pin = string.Empty;
        _lastDigit = '\0';
        _error = string.Empty;
    }

    private void Submit()
    {
        if (IsCoolingDown())
            return;

        if (!SymbolUi.Child.IsValidPinLength(_pin))
        {
            _error = $"Enter {SymbolUi.Child.MinPinLength} to {SymbolUi.Child.MaxPinLength} digits.";
            return;
        }

        var childUis = ResolveTargetChildUis();
        if (childUis.Count == 0)
        {
            // The selection vanished while the dialog was open - nothing to lock or unlock
            CloseAndReset();
            return;
        }

        switch (_mode)
        {
            case Modes.SetPin:
                if (_stage == Stages.EnterNewPin)
                {
                    _pinToConfirm = _pin;
                    _pin = string.Empty;
                    _error = string.Empty;
                    _lastDigit = '\0';
                    _stage = Stages.ConfirmPin;
                    return;
                }

                if (_pin != _pinToConfirm)
                {
                    // A mismatch is embarrassing, not an attack - shake, but don't count it
                    _lastWrongPinAt = ImGui.GetTime();
                    _error = "PINs don't match - start over.";
                    _pin = string.Empty;
                    _pinToConfirm = string.Empty;
                    _lastDigit = '\0';
                    _stage = Stages.EnterNewPin;
                    return;
                }

                foreach (var childUi in childUis)
                {
                    childUi.LockWith(_pin);
                }

                // Commands queued before locking (an insertion, an earlier value change)
                // could otherwise undo a locked op away - the lock must be the last word
                UndoRedoStack.Clear();
                break;

            case Modes.Unlock:
                var lockedTargets = childUis.FindAll(c => c.IsLocked);
                if (lockedTargets.Count == 0)
                {
                    // Everything got unlocked while the dialog was open - nothing to ask a PIN for
                    CloseAndReset();
                    return;
                }

                // Each target is verified against its own stored hash, so ops locked
                // together as a group come free together, even in a mixed selection
                var anyUnlocked = false;
                foreach (var childUi in lockedTargets)
                {
                    if (!childUi.VerifyPin(_pin))
                        continue;

                    childUi.Unlock();
                    anyUnlocked = true;
                }

                if (!anyUnlocked)
                {
                    RegisterWrongPin();
                    return;
                }

                break;
        }

        FlagCompositionAsModified(childUis);
        CloseAndReset();
    }

    private void RegisterWrongPin()
    {
        _pin = string.Empty;
        _lastDigit = '\0';
        _lastWrongPinAt = ImGui.GetTime();
        _failedAttempts++;

        if (_failedAttempts >= AttemptsBeforeCooldown)
        {
            var seconds = MathF.Min(MaxCooldownSeconds,
                                    CooldownStartSeconds * MathF.Pow(2, _failedAttempts - AttemptsBeforeCooldown));
            _cooldownUntil = ImGui.GetTime() + seconds;
            _error = string.Empty;
        }
        else
        {
            var remaining = AttemptsBeforeCooldown - _failedAttempts;
            _error = $"Wrong PIN - {remaining} attempt{(remaining == 1 ? "" : "s")} left before a short timeout.";
        }
    }

    private bool IsCoolingDown()
        => ImGui.GetTime() < _cooldownUntil;

    private int CooldownRemainingSeconds
        => (int)MathF.Ceiling((float)(_cooldownUntil - ImGui.GetTime()));

    private static void FlagCompositionAsModified(List<SymbolUi.Child> childUis)
    {
        var parent = childUis[0].SymbolChild?.Parent;
        parent?.GetSymbolUi().FlagAsModified();
    }

    private void CloseAndReset()
    {
        _pin = string.Empty;
        _pinToConfirm = string.Empty;
        _error = string.Empty;
        _failedAttempts = 0;
        _cooldownUntil = 0;
        ImGui.CloseCurrentPopup();
    }

    private List<SymbolUi.Child> ResolveTargetChildUis()
    {
        var childUis = new List<SymbolUi.Child>();
        if (!SymbolUiRegistry.TryGetSymbolUi(_compositionSymbolId, out var compositionSymbolUi))
            return childUis;

        foreach (var childId in _targetChildIds)
        {
            if (compositionSymbolUi.ChildUis.TryGetValue(childId, out var childUi) && childUi.SymbolChild != null)
                childUis.Add(childUi);
        }

        return childUis;
    }

    private static bool TryCollectTargets(List<SymbolUi.Child> childUis, out Guid compositionSymbolId, out List<Guid> childIds)
    {
        compositionSymbolId = Guid.Empty;
        childIds = [];
        if (childUis.Count == 0)
            return false;

        foreach (var childUi in childUis)
        {
            if (childUi.SymbolChild?.Parent == null)
                continue;

            compositionSymbolId = childUi.SymbolChild.Parent.Id;
            childIds.Add(childUi.Id);
        }

        return childIds.Count > 0;
    }

    private enum Modes
    {
        SetPin,
        Unlock,
    }

    private enum Stages
    {
        EnterNewPin,
        ConfirmPin,
    }

    private const float WrongPinFlashDuration = 0.45f;
    private const float DigitEchoDuration = 0.6f;
    private const int AttemptsBeforeCooldown = 3;
    private const float CooldownStartSeconds = 3f;
    private const float MaxCooldownSeconds = 15f;

    private Modes _mode;
    private Stages _stage;
    private string _pin = string.Empty;
    private string _pinToConfirm = string.Empty;
    private string _error = string.Empty;
    private Guid _compositionSymbolId;
    private List<Guid> _targetChildIds = [];
    private bool _isOpen;
    private int _failedAttempts;
    private double _cooldownUntil;
    private char _lastDigit;
    private double _lastDigitAt = double.NegativeInfinity;
    private double _lastWrongPinAt = double.NegativeInfinity;
}
