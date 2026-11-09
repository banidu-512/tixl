#nullable enable
using System;
using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;

namespace T3.Editor.Gui.Dialog;

/// <summary>
/// Shown the first time the LASER master button is armed: a laser safety warning the user
/// must confirm before any output can light up. OK arms the master directly;
/// "Don't show this message again" persists the acknowledgement in the user settings;
/// Cancel leaves the master off.
/// </summary>
internal sealed class LaserArmWarningDialog : ModalDialog
{
    internal void Draw()
    {
        // No minimum height, so the dialog hugs its content without empty space below the buttons.
        DialogSize = new Vector2(560, 0);

        // The header is part of the content and Cancel/Escape close it, so a title bar would only repeat both.
        Flags = ImGuiWindowFlags.NoTitleBar;

        if (BeginDialog("###LaserArmWarning"))
        {
            FormInputs.AddSectionHeader("Laser safety warning");

            FormInputs.AddVerticalSpace(4);

            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted("Lasers are powerful and dangerous, hence they should be handled with care and safety for the audience in mind.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Please comply with your local regulations.");
            ImGui.Spacing();
            ImGui.TextUnformatted("The TiXL developers cannot be held responsible for misuse of laser equipment.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Don't shoot rays towards your audience, always use a safety switch on your hardware.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Avoid burning your laser hardware by limiting the laser scan rate and specify a safety area.");
            ImGui.PopTextWrapPos();

            FormInputs.AddVerticalSpace(6);

            ImGui.Checkbox("Don't show this message again", ref _dontShowAgain);

            FormInputs.AddVerticalSpace(2);

            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                ImGui.CloseCurrentPopup();
            }

            if (CustomComponents.DrawCtaButton("Cancel", Icon.None, CustomComponents.ButtonStates.Default))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (CustomComponents.DrawCtaButton("OK", isEnabled: true, enableTriggerWithReturn: true))
            {
                if (_dontShowAgain)
                {
                    UserSettings.Config.LaserSafetyWarningAcknowledged = true;
                    UserSettings.Save();
                    Log.Debug("Laser arm warning acknowledged - won't be shown again");
                }

                LaserMasterEnable.Enabled = true;
                Log.Debug("Laser master enable -> ENABLED (green) - armed through the safety warning");
                ImGui.CloseCurrentPopup();
            }

            EndDialogContent();
        }

        EndDialog();
    }

    private bool _dontShowAgain;
}