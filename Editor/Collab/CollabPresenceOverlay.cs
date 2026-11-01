#nullable enable
using ImGuiNET;
using T3.Editor.Gui;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.MagGraph.Ui;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Collab;

/// <summary>
/// Draws remote participants on the graph canvas: colored cursors with name tags and a compact
/// interaction chip (mouse buttons and wheel, mirroring the local interaction overlay), plus
/// accent-colored outlines around nodes a remote user has selected. Also samples the local cursor,
/// selection and mouse interaction each frame and hands them to the manager.
///
/// Called from the graph window's draw after the canvas channels are merged, so
/// overlays clip to the graph window and render above nodes and connections.
/// </summary>
internal static class CollabPresenceOverlay
{
    public static void Draw(ProjectView projectView, IGraphView graphView, ImDrawListPtr drawList)
    {
        var manager = CollabManager.Instance;
        if (!manager.IsSessionActive)
        {
            _peerVisuals.Clear();
            CollabSpectator.Stop();
            return;
        }

        if (projectView?.InstView == null || graphView?.Canvas == null)
            return;

        var canvas = graphView.Canvas;
        var compositionId = projectView.InstView.Symbol.Id;

        SampleLocalPresence(manager, projectView, canvas, compositionId);
        DrawRemotePresence(manager, projectView, graphView, canvas, compositionId, drawList);
        CollabSpectator.Update(manager, canvas, compositionId, drawList);
        DrawLockBadges(manager, projectView, graphView, canvas, compositionId, drawList);
    }

    // ---------------------------------------------------------------------------------------
    // Local sampling
    // ---------------------------------------------------------------------------------------

    private static void SampleLocalPresence(CollabManager manager, ProjectView projectView, ScalableCanvas canvas, Guid compositionId)
    {
        // The graph canvas lives in a child window (ScalableCanvas uses the same flags), so a plain
        // IsWindowHovered() is false whenever the pointer is over the canvas — which left the remote
        // cursor permanently hidden.
        var hovering = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                             | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                             | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var cursorOnCanvas = hovering
                                 ? canvas.InverseTransformPositionFloat(ImGui.GetMousePos())
                                 : Vector2.Zero;

        // Decays across frames so a peer's wheel indicator fades out on its own; presence is sent at
        // 20 Hz, and a per-interval delta would be invisible between two packets.
        // Only the wheel over this canvas counts: the IO wheel is global and would also report scrolling elsewhere.
        _wheelSpin = _wheelSpin * 0.82f + (hovering ? ImGui.GetIO().MouseWheel * 6f : 0f);
        if (MathF.Abs(_wheelSpin) < 0.05f)
            _wheelSpin = 0;

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

        manager.SetLocalPresence(compositionId,
                                 cursorOnCanvas,
                                 hovering,
                                 selectionIds,
                                 ImGui.IsMouseDown(ImGuiMouseButton.Left),
                                 ImGui.IsMouseDown(ImGuiMouseButton.Right),
                                 ImGui.IsMouseDown(ImGuiMouseButton.Middle),
                                 _wheelSpin);
    }

    private static float _wheelSpin;

    // ---------------------------------------------------------------------------------------
    // Remote drawing
    // ---------------------------------------------------------------------------------------

    private static void DrawRemotePresence(CollabManager manager, ProjectView projectView, IGraphView graphView, ScalableCanvas canvas, Guid compositionId, ImDrawListPtr drawList)
    {
        var deltaTime = ImGui.GetIO().DeltaTime;

        foreach (var (name, display) in manager.GetPeersInComposition(compositionId))
        {
            var color = display.AccentColor;
            var peerName = string.IsNullOrEmpty(name) ? "peer" : name;

            DrawSelectionOutlines(drawList, projectView, graphView, canvas, display.SelectedChildIds, color, peerName);

            if (!_peerVisuals.TryGetValue(display.UserId, out var visual))
            {
                visual = new PeerVisual();
                _peerVisuals[display.UserId] = visual;
            }

            visual.Update(display, deltaTime);
            if (visual.Alpha > 0.01f)
                DrawCursor(drawList, canvas, visual, display, color, peerName);
        }
    }

    /// <summary>
    /// Smoothed view of one remote cursor. Presence arrives at 20 Hz, which looks steppy when drawn
    /// as is; easing towards the latest position and fading in and out when the pointer enters or
    /// leaves the canvas keeps the cursor calm without adding any latency worth noticing.
    /// </summary>
    private sealed class PeerVisual
    {
        public Vector2 PosOnCanvas;
        public float Alpha;
        public readonly MouseInteractionGraphic Mouse = new();

        /// <summary>1 while the peer is pressing or scrolling, fading to 0 once they stop.</summary>
        public float InteractionFade => 1f - Math.Clamp((float)(ImGui.GetTime() - _lastInteractionTime) / InteractionFadeoutSec, 0f, 1f);

        private bool _hasPosition;
        private double _lastInteractionTime = double.NegativeInfinity;

        public void Update(CollabManager.CollabPeerDisplay display, float deltaTime)
        {
            if (display.IsHoveringCanvas)
            {
                // A cursor that was away re-appears where the peer is now instead of gliding in from the old spot.
                if (!_hasPosition || Alpha < 0.05f)
                    PosOnCanvas = display.CursorOnCanvas;
                else
                    PosOnCanvas = Vector2.Lerp(PosOnCanvas, display.CursorOnCanvas, 1f - MathF.Exp(-deltaTime * CursorFollowSpeed));

                _hasPosition = true;
            }

            var targetAlpha = display.IsHoveringCanvas ? 1f : 0f;
            Alpha += (targetAlpha - Alpha) * (1f - MathF.Exp(-deltaTime * FadeSpeed));

            // WheelSpin is the sender's decaying scroll speed; its time integral is about two ticks
            // per unit, which moves the wheel by the amount the peer actually scrolled.
            Mouse.AddWheelTicks(display.WheelSpin * deltaTime * WheelTicksPerSpinSecond);
            if (display.LeftButtonDown || display.RightButtonDown || display.MiddleButtonDown || display.WheelSpin != 0)
                _lastInteractionTime = ImGui.GetTime();
        }

        private const float CursorFollowSpeed = 22f;
        private const float FadeSpeed = 14f;
        private const float InteractionFadeoutSec = 2f;
        private const float WheelTicksPerSpinSecond = 2f;
    }

    private static readonly Dictionary<Guid, PeerVisual> _peerVisuals = new();

    private static uint Tint(Vector4 color, float alpha) => CollabColors.ToUint(new Vector4(color.X, color.Y, color.Z, color.W * alpha));

    private static uint Black(float alpha) => CollabColors.ToUint(new Vector4(0, 0, 0, alpha));

    private static void DrawCursor(ImDrawListPtr drawList, ScalableCanvas canvas, PeerVisual visual, CollabManager.CollabPeerDisplay display, Vector4 color, string name)
    {
        var screenPos = canvas.TransformPositionFloat(visual.PosOnCanvas);
        if (screenPos.X < -100 || screenPos.Y < -100)
            return;

        var scale = T3Ui.UiScaleFactor;
        var alpha = visual.Alpha;

        // Pointer arrow: a head and a tail built from convex pieces, with a soft drop shadow and a thin
        // dark rim so it reads on both dark node bodies and the lighter graph background.
        var shadowOffset = new Vector2(1.2f, 1.8f) * scale;
        DrawArrow(drawList, screenPos + shadowOffset, scale, Black(0.35f * alpha), Black(0.35f * alpha));
        DrawArrow(drawList, screenPos, scale, Tint(color, alpha), Black(0.75f * alpha));

        // Name tag: accent-filled pill with dark text, so it is legible whatever sits behind it.
        var textSize = ImGui.CalcTextSize(name);
        var padding = new Vector2(7, 2.5f) * scale;
        var tagMin = screenPos + new Vector2(13, 17) * scale;
        var tagMax = tagMin + textSize + padding * 2;
        var rounding = (tagMax.Y - tagMin.Y) * 0.5f;
        drawList.AddRectFilled(tagMin + new Vector2(0, 1.5f * scale), tagMax + new Vector2(0, 1.5f * scale), Black(0.25f * alpha), rounding);
        drawList.AddRectFilled(tagMin, tagMax, Tint(color, alpha), rounding);
        drawList.AddText(tagMin + padding, Tint(new Vector4(0.06f, 0.06f, 0.08f, 1f), alpha), name);

        var interactionFade = visual.InteractionFade * alpha;
        if (interactionFade > 0.01f)
            DrawInteractionMouse(drawList, new Vector2(tagMin.X, tagMax.Y + 4 * scale), visual, display, color, interactionFade);
    }

    private static void DrawArrow(ImDrawListPtr drawList, Vector2 tip, float scale, uint fill, uint rim)
    {
        var leftBottom = tip + new Vector2(0, 16.5f) * scale;
        var notch = tip + new Vector2(4.3f, 12.8f) * scale;
        var right = tip + new Vector2(11.8f, 11.8f) * scale;
        var tailLeft = tip + new Vector2(7.3f, 19.6f) * scale;
        var tailRight = tip + new Vector2(10f, 18.4f) * scale;
        var notchRight = tip + new Vector2(7.4f, 12.1f) * scale;

        drawList.AddTriangleFilled(tip, leftBottom, notch, fill);
        drawList.AddTriangleFilled(tip, notch, right, fill);
        drawList.AddQuadFilled(notch, tailLeft, tailRight, notchRight, fill);

        var thickness = 1.1f * scale;
        drawList.AddLine(tip, leftBottom, rim, thickness);
        drawList.AddLine(leftBottom, notch, rim, thickness);
        drawList.AddLine(notch, tailLeft, rim, thickness);
        drawList.AddLine(tailLeft, tailRight, rim, thickness);
        drawList.AddLine(tailRight, notchRight, rim, thickness);
        drawList.AddLine(notchRight, right, rim, thickness);
        drawList.AddLine(right, tip, rim, thickness);
    }

    /// <summary>
    /// The peer's mouse as the local interaction overlay draws it: which buttons they hold and how they
    /// scroll, in their accent color on a dark plate right under the name tag. Only shown while they
    /// interact, so an idle cursor stays a plain arrow with a name.
    /// </summary>
    private static void DrawInteractionMouse(ImDrawListPtr drawList, Vector2 topLeft, PeerVisual visual, CollabManager.CollabPeerDisplay display, Vector4 color, float fade)
    {
        var uiScale = T3Ui.UiScaleFactor;
        var mouseScale = MouseScale * uiScale;

        var plateSize = new Vector2(MouseExtent * 2, MouseExtent * 2 - 6) * mouseScale;
        drawList.AddRectFilled(topLeft, topLeft + plateSize, Black(0.6f * fade), 6 * uiScale);

        var center = topLeft + new Vector2(MouseExtent, MouseExtent - 3) * mouseScale;
        visual.Mouse.Draw(drawList, center, display.LeftButtonDown, display.RightButtonDown, display.MiddleButtonDown, color, fade, mouseScale);
    }

    /** Half the width of the mouse drawing at scale 1, buttons and stroke included. */
    private const float MouseExtent = 34f;

    /** Size of a peer's mouse relative to the local overlay's. */
    private const float MouseScale = 0.55f;

    /// <summary>
    /// Frames the nodes a remote user has selected: a soft tinted fill, a glow and a crisp outline that
    /// follows the node's real bounds, plus one name tag on the first node so it is clear whose selection it is.
    /// </summary>
    private static void DrawSelectionOutlines(ImDrawListPtr drawList, ProjectView projectView, IGraphView graphView, ScalableCanvas canvas, Guid[] selectedIds, Vector4 color, string name)
    {
        if (selectedIds.Length == 0 || projectView.InstView == null)
            return;

        var symbolUi = projectView.InstView.Symbol.GetSymbolUi();
        if (symbolUi == null)
            return;

        var scale = T3Ui.UiScaleFactor;
        var padding = new Vector2(3, 3) * scale;
        var rounding = 7f * scale;
        var tagDrawn = false;

        foreach (var childId in selectedIds)
        {
            if (!TryGetNodeBounds(graphView, symbolUi, childId, out var posOnCanvas, out var size))
                continue;

            var min = canvas.TransformPositionFloat(posOnCanvas) - padding;
            var max = canvas.TransformPositionFloat(posOnCanvas + size) + padding;

            drawList.AddRectFilled(min, max, Tint(color, 0.08f), rounding);
            drawList.AddRect(min, max, Tint(color, 0.2f), rounding, 0, 4.5f * scale);
            drawList.AddRect(min, max, Tint(color, 0.95f), rounding, 0, 1.6f * scale);

            if (tagDrawn || max.X - min.X < 36 * scale)
                continue;

            tagDrawn = true;
            var textSize = ImGui.CalcTextSize(name);
            var tagPadding = new Vector2(6, 1.5f) * scale;
            var tagSize = textSize + tagPadding * 2;
            var tagMin = new Vector2(min.X + rounding, min.Y - tagSize.Y + 1.5f * scale);
            drawList.AddRectFilled(tagMin, tagMin + tagSize, Tint(color, 0.95f), tagSize.Y * 0.5f);
            drawList.AddText(tagMin + tagPadding, Tint(new Vector4(0.06f, 0.06f, 0.08f, 1f), 1f), name);
        }
    }

    /// <summary>
    /// The magnetic graph lays nodes out taller than their symbol UI size (input rows are part of the
    /// node), so frames must use its own bounds; other views fall back to the symbol UI.
    /// </summary>
    private static bool TryGetNodeBounds(IGraphView graphView, SymbolUi symbolUi, Guid childId, out Vector2 posOnCanvas, out Vector2 size)
    {
        if (graphView is MagGraphView magGraphView)
            return magGraphView.TryGetItemBounds(childId, out posOnCanvas, out size);

        if (symbolUi.ChildUis.TryGetValue(childId, out var childUi))
        {
            posOnCanvas = childUi.PosOnCanvas;
            size = childUi.Size;
            return true;
        }

        posOnCanvas = default;
        size = default;
        return false;
    }

    /// <summary>
    /// Marks nodes whose parameters another participant is currently editing (soft lock). The chip
    /// shows the holder's color and name so everyone sees who is working on what at a glance.
    /// </summary>
    private static void DrawLockBadges(CollabManager manager, ProjectView projectView, IGraphView graphView, ScalableCanvas canvas, Guid compositionId, ImDrawListPtr drawList)
    {
        if (projectView.InstView == null)
            return;

        var symbolUi = projectView.InstView.Symbol.GetSymbolUi();
        if (symbolUi == null)
            return;

        var scale = T3Ui.UiScaleFactor;
        var padding = new Vector2(3, 3) * scale;

        foreach (var lockInfo in manager.GetInputLocksForComposition(compositionId))
        {
            if (!TryGetNodeBounds(graphView, symbolUi, lockInfo.ChildId, out var posOnCanvas, out var size))
                continue;

            var min = canvas.TransformPositionFloat(posOnCanvas) - padding;
            var max = canvas.TransformPositionFloat(posOnCanvas + size) + padding;
            var color = CollabColors.ToUint(lockInfo.Color);

            drawList.AddRect(min, max, color, 7f * scale, 0, 1.4f * scale);

            var label = $"{lockInfo.HolderName} editing";
            var labelSize = ImGui.CalcTextSize(label);
            var tagPadding = new Vector2(6, 1.5f) * scale;
            var tagSize = labelSize + tagPadding * 2;
            var tagMin = new Vector2(max.X - tagSize.X - 8 * scale, min.Y - tagSize.Y - 3 * scale);
            drawList.AddRectFilled(tagMin, tagMin + tagSize, 0xE60E0E12, tagSize.Y * 0.5f);
            drawList.AddRect(tagMin, tagMin + tagSize, color, tagSize.Y * 0.5f, 0, 1f);
            drawList.AddText(tagMin + tagPadding, color, label);
        }
    }
}
