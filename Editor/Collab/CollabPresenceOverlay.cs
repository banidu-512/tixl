#nullable enable
using ImGuiNET;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Collab;

/// <summary>
/// Draws remote participants on the graph canvas: colored cursors with name tags while hovering the
/// same composition, and accent-colored outlines around nodes a remote user has selected. Also
/// samples the local cursor and selection each frame and hands them to the manager.
///
/// Called from the graph window's draw after the canvas channels are merged, so
/// overlays clip to the graph window and render above nodes and connections.
/// </summary>
internal static class CollabPresenceOverlay
{
    public static void Draw(ProjectView projectView, IGraphView graphView, ImDrawListPtr drawList)
    {
        var manager = CollabManager.Instance;
        if (!manager.IsSessionActive || projectView?.InstView == null || graphView?.Canvas == null)
            return;

        var canvas = graphView.Canvas;
        var compositionId = projectView.InstView.Symbol.Id;

        SampleLocalPresence(manager, projectView, canvas, compositionId);
        DrawRemotePresence(manager, projectView, canvas, compositionId, drawList);
        DrawLockBadges(manager, projectView, canvas, compositionId, drawList);
    }

    // ---------------------------------------------------------------------------------------
    // Local sampling
    // ---------------------------------------------------------------------------------------

    private static void SampleLocalPresence(CollabManager manager, ProjectView projectView, ScalableCanvas canvas, Guid compositionId)
    {
        var hovering = ImGui.IsWindowHovered();
        var cursorOnCanvas = hovering
                                 ? canvas.InverseTransformPositionFloat(ImGui.GetMousePos())
                                 : Vector2.Zero;

        Guid[] selectionIds = [];
        if (projectView.NodeSelection is { } selection)
        {
            var childUis = selection.GetSelectedChildUis();
            if (childUis.Any())
            {
                // Sorted so the array is stable while dragging — otherwise every micro-selection
                // change would flag presence as modified.
                selectionIds = childUis.Select(c => c.Id).OrderBy(id => id).ToArray();
            }
        }

        manager.SetLocalPresence(compositionId, cursorOnCanvas, hovering, selectionIds);
    }

    // ---------------------------------------------------------------------------------------
    // Remote drawing
    // ---------------------------------------------------------------------------------------

    private static void DrawRemotePresence(CollabManager manager, ProjectView projectView, ScalableCanvas canvas, Guid compositionId, ImDrawListPtr drawList)
    {
        foreach (var (name, display) in manager.GetPeersInComposition(compositionId))
        {
            var color = display.AccentColor;
            var peerName = string.IsNullOrEmpty(name) ? "peer" : name;

            if (display.IsHoveringCanvas)
                DrawCursor(drawList, canvas, display.CursorOnCanvas, color, peerName);

            DrawSelectionOutlines(drawList, projectView, canvas, display.SelectedChildIds, color, peerName);
        }
    }

    private static void DrawCursor(ImDrawListPtr drawList, ScalableCanvas canvas, Vector2 cursorOnCanvas, Vector4 color, string name)
    {
        var screenPos = canvas.TransformPositionFloat(cursorOnCanvas);
        if (screenPos.X < -100 || screenPos.Y < -100)
            return;

        drawList.AddTriangleFilled(screenPos,
                                   screenPos + new Vector2(14, 0),
                                   screenPos + new Vector2(0, 16),
                                   CollabColors.ToUint(color));

        drawList.AddTriangle(screenPos,
                             screenPos + new Vector2(14, 0),
                             screenPos + new Vector2(0, 16),
                             CollabColors.ToUint(new Vector4(0, 0, 0, 0.8f)),
                             1f);

        var labelPos = screenPos + new Vector2(14, 12);
        var labelSize = ImGui.CalcTextSize(name);
        drawList.AddRectFilled(labelPos, labelPos + labelSize + new Vector2(8, 4), 0xCC0E0E12, 4);
        drawList.AddRect(labelPos, labelPos + labelSize + new Vector2(8, 4), CollabColors.ToUint(color), 4, 0, 1f);
        drawList.AddText(labelPos + new Vector2(4, 2), 0xFFFFFFFF, name);
    }

    private static void DrawSelectionOutlines(ImDrawListPtr drawList, ProjectView projectView, ScalableCanvas canvas, Guid[] selectedIds, Vector4 color, string name)
    {
        if (selectedIds.Length == 0 || projectView.InstView == null)
            return;

        var symbolUi = projectView.InstView.Symbol.GetSymbolUi();
        if (symbolUi == null)
            return;
        foreach (var childId in selectedIds)
        {
            if (!symbolUi.ChildUis.TryGetValue(childId, out var childUi))
                continue;

            var topLeft = canvas.TransformPositionFloat(childUi.PosOnCanvas);
            var bottomRight = canvas.TransformPositionFloat(childUi.PosOnCanvas + childUi.Size);
            drawList.AddRect(topLeft - HighlightPadding, bottomRight + HighlightPadding,
                             CollabColors.ToUint(color), 6f, 0, SelectionOutlineThickness);
        }
    }

    private static readonly Vector2 HighlightPadding = new(4, 4);
    private const float SelectionOutlineThickness = 2.5f;

    /// <summary>
    /// Marks nodes whose parameters another participant is currently editing (soft lock). The chip
    /// shows the holder's color and name so everyone sees who is working on what at a glance.
    /// </summary>
    private static void DrawLockBadges(CollabManager manager, ProjectView projectView, ScalableCanvas canvas, Guid compositionId, ImDrawListPtr drawList)
    {
        if (projectView.InstView == null)
            return;

        var symbolUi = projectView.InstView.Symbol.GetSymbolUi();
        if (symbolUi == null)
            return;

        foreach (var lockInfo in manager.GetInputLocksForComposition(compositionId))
        {
            if (!symbolUi.ChildUis.TryGetValue(lockInfo.ChildId, out var childUi))
                continue;

            var topLeft = canvas.TransformPositionFloat(childUi.PosOnCanvas);
            var topRight = canvas.TransformPositionFloat(childUi.PosOnCanvas + new Vector2(childUi.Size.X, 0));
            var color = CollabColors.ToUint(lockInfo.Color);

            drawList.AddRect(topLeft - HighlightPadding, canvas.TransformPositionFloat(childUi.PosOnCanvas + childUi.Size) + HighlightPadding,
                             color, 6f, 0, 1.2f);

            var label = $"{lockInfo.HolderName} editing";
            var labelSize = ImGui.CalcTextSize(label);
            var labelPos = new Vector2(topRight.X - labelSize.X - 10, topLeft.Y - labelSize.Y - 10);
            drawList.AddRectFilled(labelPos - new Vector2(4, 2), labelPos + labelSize + new Vector2(4, 2), 0xD20E0E12, 4);
            drawList.AddRect(labelPos - new Vector2(4, 2), labelPos + labelSize + new Vector2(4, 2), color, 4, 0, 1f);
            drawList.AddText(labelPos, color, label);
        }
    }
}
