#nullable enable
using System;
using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.Output;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using Vector2 = System.Numerics.Vector2;
using Color = T3.Core.DataTypes.Vector.Color;

namespace T3.Editor.Gui.Windows.OutputSetup;

/// <summary>
/// Draws the live laser frame of a laser output — not the camera's raw points but the frame the
/// output actually streams: the setup pipeline (optimizer + safety from the output's processing
/// settings, the alignment rectangle while uncalibrated) as the <see cref="LaserDriver"/> ran it.
/// The Board card and the output's parameter card share it, so both preview exactly what leaves
/// for the laser. Runs on the UI thread; frames come from the driver's per-output cache, so the
/// camera evaluates at most once per frame no matter how many cards show it.
/// </summary>
internal static class LaserOutputPreview
{
    /// <summary>
    /// The frame this output streams right now (see <see cref="LaserDriver.GetFrame"/>).
    /// </summary>
    public static LaserPoint[] GetFrame(OutputDefinition output) => LaserDriver.GetFrame(output);

    /// <summary>
    /// Draws a laser frame onto the ILDA scan field inscribed in the rect. The field stays square —
    /// it is the scanner's field, not the card's aspect: a circle previews as a circle. Lit runs draw
    /// as colored polylines, the jumps between them as faint straight lines (invisible on the wall,
    /// but they show where the beam travels), and dwell dots as dots. A dark laser master dims the
    /// frame to a hint: nothing is leaving for the laser.
    /// </summary>
    public static void DrawFrame(ImDrawListPtr dl, Vector2 min, Vector2 max, LaserPoint[] frame, float fade = 1f)
    {
        var uiScale = T3Ui.UiScaleFactor;
        var inset = 3f * uiScale;
        var side = MathF.Min(max.X - min.X, max.Y - min.Y) - 2f * inset;
        if (side < 8f || fade <= 0.001f)
            return;

        var center = (min + max) * 0.5f;
        var half = side * 0.5f;
        var fieldMin = new Vector2(center.X - half, center.Y - half);
        var fieldMax = new Vector2(center.X + half, center.Y + half);
        var alpha = fade * (LaserMasterEnable.Enabled ? 1f : 0.2f);

        dl.PushClipRect(min, max, true);
        dl.AddRect(fieldMin, fieldMax, UiColors.TextMuted.Fade(0.22f * fade), 2f * uiScale);

        if (frame.Length > 0)
        {
            var posScale = half / 32767f;
            var lineThickness = MathF.Max(1.2f * uiScale, side / 220f);
            var dotRadius = MathF.Max(1.5f * uiScale, lineThickness * 0.8f);
            Vector2 Map(in LaserPoint p) => new(center.X + p.X * posScale, center.Y - p.Y * posScale);

            Vector2? lastLit = null; // the previous lit run's end, where the next jump starts
            for (var i = 0; i < frame.Length; i++)
            {
                if (!IsLit(frame[i]))
                    continue;

                var pos = Map(frame[i]);
                var color = PointColor(frame[i], alpha);
                if (i > 0 && IsLit(frame[i - 1]))
                {
                    var prev = Map(frame[i - 1]);
                    if (Vector2.DistanceSquared(prev, pos) > 0.01f)
                    {
                        dl.AddLine(prev, pos, color, lineThickness);
                    }
                    else
                    {
                        // Dwell: consecutive points parked on one spot draw as a dot — a zero-length
                        // line would draw nothing, and a dwell run is how dots are sent.
                        dl.AddCircleFilled(pos, dotRadius, color);
                    }
                }
                else
                {
                    if (lastLit is { } from)
                        dl.AddLine(from, pos, UiColors.TextMuted.Fade(0.10f * fade), uiScale);

                    // A run of one point is a dwell dot — no segment to draw, so the point itself.
                    if (i + 1 >= frame.Length || !IsLit(frame[i + 1]))
                        dl.AddCircleFilled(pos, dotRadius, color);
                }

                lastLit = pos;
            }
        }

        dl.PopClipRect();
    }

    private static bool IsLit(in LaserPoint p) => (p.R | p.G | p.B) != 0;

    private static Color PointColor(in LaserPoint p, float alpha)
    {
        // A lit point with no chroma still draws — as dim white, so black laser content stays visible.
        var max = Math.Max(p.R, Math.Max(p.G, p.B));
        if (max == 0)
            return new Color(0.35f, 0.35f, 0.35f, 1f).Fade(alpha);

        return new Color(p.R / 65535f, p.G / 65535f, p.B / 65535f, 1f).Fade(alpha);
    }
}
