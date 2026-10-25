#nullable enable
using System;
using System.Numerics;
using ImGuiNET;
using T3.Core.Logging;
using T3.Core.Output;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.Windows.OutputSetup;

/// <summary>
/// The scanner camera's calibration solve: stage↔field correspondences in, pose + scan lens out.
/// [LaserCamera] projects through the solved camera instead of the manual look-at, so the frame
/// lands where the venue actually is. The solve is an authoring action like any other setup edit -
/// undoable, and saved with the setup.
/// </summary>
internal static partial class SetupParameterView
{
    private static void DrawLaserCalibration(Setup setup, OutputDefinition output, OutputDefinition.ProjectorCamera cam)
    {
        var points = cam.CalibrationPoints;
        var solved = cam.Pose != null && cam.Lens != null;

        FormInputs.ApplyIndent();
        if (solved)
        {
            CustomComponents.StylizedText($"Calibration solve active (residual {cam.ResidualPx:0.##} px) - it overrides the manual aim.",
                                          Fonts.FontSmall, UiColors.StatusOkay);
        }
        else
        {
            CustomComponents.StylizedText("Manual aim in use - [LaserCamera] with this output as OutputRef projects through this camera.",
                                          Fonts.FontSmall, UiColors.TextMuted);
        }

        FormInputs.ApplyIndent();
        CustomComponents.StylizedText($"{points.Count} correspondence(s). Aim the beam at a known stage point, then enter where it lands in the field.",
                                      Fonts.FontSmall, UiColors.TextMuted);

        FormInputs.ApplyIndent();
        if (ImGui.Button("Add Point"))
        {
            SetupUndo.RunUndoable("Add laser calibration point", setup, () =>
                                                                            {
                                                                                points.Add(new CalibrationPoint
                                                                                               {
                                                                                                   StagePosition = cam.ManualTarget,
                                                                                                   OutputPixel = LaserScannerField.PixelFromField(0f, 0f),
                                                                                               });
                                                                            });
        }

        CustomComponents.TooltipForLastItem("Add a correspondence", "Starts at the manual target and the field centre - edit both rows below.");

        ImGui.SameLine();
        var canSolve = points.Count >= ProjectorSolver.MinPointCount;
        ImGui.BeginDisabled(!canSolve);
        var solveClicked = ImGui.Button("Solve");
        ImGui.EndDisabled();

        if (!canSolve)
        {
            ImGui.SameLine();
            CustomComponents.StylizedText($"Add {ProjectorSolver.MinPointCount - points.Count} more to solve",
                                          Fonts.FontSmall, UiColors.TextMuted);
        }

        CustomComponents.TooltipForLastItem("Solve the scanner camera",
                                            $"Fits pose and scan lens to the correspondences (needs {ProjectorSolver.MinPointCount} spanning two planes).");

        if (solveClicked && ProjectorSolver.TrySolve(points, LaserScannerField.PixelResolution, out var solve))
        {
            SetupUndo.RunUndoable("Solve laser calibration", setup, () =>
                                                                      {
                                                                          cam.Pose = solve.Pose;
                                                                          cam.Lens = solve.Lens;
                                                                          cam.ResidualPx = solve.MeanResidualPx;
                                                                      });
        }
        else if (solveClicked)
        {
            Log.Warning($"Laser calibration solve failed - {points.Count} points did not fit a camera "
                        + $"(needs {ProjectorSolver.MinPointCount}+ spanning two non-parallel planes).");
        }

        if (solved)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear Solve"))
            {
                SetupUndo.RunUndoable("Clear laser calibration", setup, () =>
                                                                         {
                                                                             cam.Pose = null;
                                                                             cam.Lens = null;
                                                                             cam.ResidualPx = 0f;
                                                                         });
            }

            CustomComponents.TooltipForLastItem("Clear the solve", "Falls back to the manual aim above. The correspondences stay.");
        }

        var removeIndex = -1;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            ImGui.PushID(i);

            Span<float> stage = [point.StagePosition.X, point.StagePosition.Y, point.StagePosition.Z];
            var stageState = DrawFloatsRow($"Stage {i + 1} (m)", stage,
                                           "Where the beam actually hits, in stage metres (X right, Y up, Z towards the viewer).",
                                           reserveRight: 22);
            BeginFieldUndo(setup, stageState);
            if ((stageState & InputEditStateFlags.Modified) != 0)
                point.StagePosition = new Vector3(stage[0], stage[1], stage[2]);
            CommitFieldUndo(setup, "Move laser calibration point", stageState);

            var field = LaserScannerField.FieldFromPixel(point.OutputPixel);
            Span<float> fieldValues = [field.X, field.Y];
            var fieldState = DrawFloatsRow("Field", fieldValues,
                                           "Where that stage point sits in the scan field: X right, Y up, +-1 is the field edge.",
                                           reserveRight: 22);
            BeginFieldUndo(setup, fieldState);
            if ((fieldState & InputEditStateFlags.Modified) != 0)
                point.OutputPixel = LaserScannerField.PixelFromField(fieldValues[0], fieldValues[1]);
            CommitFieldUndo(setup, "Move laser calibration point", fieldState);

            ImGui.SameLine(0, 4 * T3Ui.UiScaleFactor);
            if (ImGui.SmallButton("x"))
                removeIndex = i;

            CustomComponents.TooltipForLastItem($"Remove correspondence {i + 1}", "Only this point - the rest keep their positions.");

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            SetupUndo.RunUndoable("Remove laser calibration point", setup, () => points.RemoveAt(removeIndex));
        }
    }
}
