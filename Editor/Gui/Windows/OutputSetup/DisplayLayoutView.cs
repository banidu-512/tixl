#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using ImGuiNET;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.SystemUi;
using T3.SystemUi;
using Vector2 = System.Numerics.Vector2;
using T3.Editor.Show;

namespace T3.Editor.Gui.Windows.OutputSetup;

/// <summary>
/// The machine's displays drawn to scale in their desktop arrangement, with one of them lit. It answers the
/// only question a plug row raises — "which screen is Display 2, and where is it?" — where the question is
/// asked, so the arrangement needs no window of its own.
/// </summary>
internal static class DisplayLayoutView
{
    /// <summary>
    /// The hover body for a display: the machine's screens in their arrangement with
    /// <paramref name="highlightedIndex"/> lit. Drawing only — a tooltip takes no clicks.
    /// </summary>
    /// <summary>
    /// The same hover body for a display of a connected render node, drawn from the arrangement that node
    /// reported. Built on hover only, so the small lists it allocates are not a per-frame cost.
    /// </summary>
    public static void DrawRemoteTooltip(Show.BoygroupManager.ClientInfo client, int highlightedIndex)
    {
        if (client.Screens.Count == 0)
            return;

        ImGui.TextDisabled(client.Name);

        // A node on an older build reports no positions: its displays are lined up side by side instead.
        var hasPositions = false;
        foreach (var screen in client.Screens)
            hasPositions |= screen.X != 0 || screen.Y != 0;

        var scale = 1f;
        var overallW = 0;
        var overallH = 0;
        var minX = 0;
        var minY = 0;
        var first = true;
        foreach (var screen in client.Screens)
        {
            if (first)
            {
                minX = screen.X;
                minY = screen.Y;
                first = false;
            }

            minX = Math.Min(minX, screen.X);
            minY = Math.Min(minY, screen.Y);
            overallW = Math.Max(overallW, screen.X + screen.Width);
            overallH = Math.Max(overallH, screen.Y + screen.Height);
        }

        var overallWidth = hasPositions ? overallW - minX : overallW;
        var overallHeight = hasPositions ? overallH - minY : overallH;
        var padding = 20f;
        var area = new Vector2(overallWidth, overallHeight);
        ImGui.Dummy(area with { X = area.X + padding * 2, Y = area.Y + padding * 2 });
        if (overallWidth > 0)
            scale = area.X > 0 ? (area.X + 0f) / overallWidth : 1f;

        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos() - new Vector2(area.X + padding * 2, area.Y + padding * 2) + new Vector2(padding);

        var fallbackX = 0;
        foreach (var screen in client.Screens)
        {
            var bx = hasPositions ? screen.X - minX : fallbackX;
            var by = hasPositions ? screen.Y - minY : 0;
            fallbackX += screen.Width;

            var min = canvasPos + new Vector2(bx * scale, by * scale);
            var max = min + new Vector2(screen.Width * scale, screen.Height * scale);
            var isHighlighted = screen.Index == highlightedIndex;

            drawList.AddRectFilled(min, max, isHighlighted ? UiColors.StatusAnimated.Fade(0.3f) : UiColors.BackgroundButton);
            drawList.AddRect(min, max, isHighlighted ? UiColors.StatusAnimated : UiColors.BackgroundFull.Fade(0.7f));

            var label = $"Display {screen.Index + 1}";
            var resolution = $"{screen.Width} x {screen.Height}";
            var labelSize = ImGui.CalcTextSize(label);
            var resolutionSize = ImGui.CalcTextSize(resolution);
            var centre = (min + max) * 0.5f;
            var lineY = centre.Y - (labelSize.Y + resolutionSize.Y) * 0.5f;

            drawList.AddText(new Vector2(centre.X - labelSize.X * 0.5f, lineY),
                             isHighlighted ? UiColors.Text : UiColors.TextMuted, label);
            lineY += labelSize.Y;
            drawList.AddText(new Vector2(centre.X - resolutionSize.X * 0.5f, lineY),
                             UiColors.TextMuted, resolution);
        }
    }

    public static void DrawTooltip(int highlightedIndex)
    {
        RefreshCache();
        if (_screens.Count == 0)
            return;

        var scale = ScaleToFit();
        var drawList = ImGui.GetWindowDrawList();

        // A quarter of a display's own width as the margin, so the diagram breathes at whatever size it lands.
        var padding = _screens[0].Bounds.Width * scale * 0.25f;
        var canvasPos = ImGui.GetCursorScreenPos() + new Vector2(padding);
        var area = new Vector2(_overallBounds.Width, _overallBounds.Height) * scale;
        ImGui.Dummy(area + new Vector2(padding * 2));

        for (var i = 0; i < _screens.Count; i++)
        {
            var bounds = _screens[i].Bounds;
            var min = canvasPos + new Vector2((bounds.X - _overallBounds.X) * scale, (bounds.Y - _overallBounds.Y) * scale);
            var max = min + new Vector2(bounds.Width * scale, bounds.Height * scale);
            var isHighlighted = i == highlightedIndex;

            drawList.AddRectFilled(min, max, isHighlighted ? UiColors.StatusAnimated.Fade(0.3f) : UiColors.BackgroundButton);
            drawList.AddRect(min, max, isHighlighted ? UiColors.StatusAnimated : UiColors.BackgroundFull.Fade(0.7f));

            // Name, the primary marker and the resolution stack as their own lines: a wide display box is
            // still only as wide as its screen is, and one long line spills straight out of it.
            var label = Label(i);
            var resolution = Resolution(bounds.Width, bounds.Height);
            var isPrimary = _screens[i].Primary;

            var labelSize = ImGui.CalcTextSize(label);
            ImGui.PushFont(Fonts.FontSmall);
            var primarySize = isPrimary ? ImGui.CalcTextSize(PrimaryMarker) : Vector2.Zero;
            var resolutionSize = ImGui.CalcTextSize(resolution);
            ImGui.PopFont();

            var centre = (min + max) * 0.5f;
            var lineY = centre.Y - (labelSize.Y + primarySize.Y + resolutionSize.Y) * 0.5f;
            var small = Fonts.FontSmall;

            drawList.AddText(new Vector2(centre.X - labelSize.X * 0.5f, lineY),
                             isHighlighted ? UiColors.Text : UiColors.TextMuted, label);
            lineY += labelSize.Y;

            if (isPrimary)
            {
                drawList.AddText(small, small.FontSize, new Vector2(centre.X - primarySize.X * 0.5f, lineY),
                                 UiColors.TextMuted, PrimaryMarker);
                lineY += primarySize.Y;
            }

            drawList.AddText(small, small.FontSize, new Vector2(centre.X - resolutionSize.X * 0.5f, lineY),
                             UiColors.TextMuted, resolution);
        }
    }

    /// <summary>Opens the OS display settings, where the arrangement itself is edited.</summary>
    public static void OpenSystemDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:display", UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warning($"Could not open the display settings: {e.Message}");
        }
    }

    /// <summary>
    /// Re-measures the arrangement when the screen list is a new one — it is replaced, not changed, when the
    /// displays do.
    /// </summary>
    private static void RefreshCache()
    {
        var screens = EditorUi.Instance.AllScreens;
        if (ReferenceEquals(screens, _screens))
            return;

        _screens = screens;
        _labels.Clear();
        _resolutions.Clear();

        if (_screens.Count == 0)
        {
            _overallBounds = Rectangle.Empty;
            return;
        }

        var bounds = _screens[0].Bounds;
        for (var i = 1; i < _screens.Count; i++)
        {
            bounds = Rectangle.Union(bounds, _screens[i].Bounds);
        }

        _overallBounds = bounds;
    }

    /// <summary>Small enough to take in at a glance — the diagram says where a screen is, not what is on it.</summary>
    private static float ScaleToFit()
    {
        if (_overallBounds.Width <= 0 || _overallBounds.Height <= 0)
            return DiagramScale;

        // A wall of displays still has to fit a tooltip, so the arrangement's longer side sets the limit.
        return MathF.Min(DiagramScale, MaxDiagramSize * T3Ui.UiScaleFactor / Math.Max(_overallBounds.Width, _overallBounds.Height));
    }

    private static string Label(int index)
    {
        if (!_labels.TryGetValue(index, out var label))
        {
            label = $"Display {index + 1}";
            _labels[index] = label;
        }

        return label;
    }

    private static string Resolution(int width, int height)
    {
        var key = (width, height);
        if (!_resolutions.TryGetValue(key, out var resolution))
        {
            resolution = $"{width}×{height}";
            _resolutions[key] = resolution;
        }

        return resolution;
    }

    private const string PrimaryMarker = "(Primary)";
    private const float DiagramScale = 0.06f;
    private const float MaxDiagramSize = 260f;

    private static IReadOnlyList<IScreen> _screens = [];
    private static Rectangle _overallBounds;
    private static readonly Dictionary<int, string> _labels = [];
    private static readonly Dictionary<(int, int), string> _resolutions = [];
}
