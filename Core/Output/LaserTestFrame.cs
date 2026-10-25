#nullable enable
using System;
using System.Collections.Generic;
using T3.Core.DataTypes;

namespace T3.Core.Output;

/// <summary>
/// The test screen a laser output streams while <b>Test screen</b> is on in its Safety settings: the
/// alignment rectangle (the same one the uncalibrated safety shows) with the output's name scanned as
/// strokes inside it, so the projector on the wall can be told apart from its siblings while aiming.
/// Independent of the Calibrated gate by design - it is the aiming aid used <i>before</i> calibration.
/// </summary>
public static class LaserTestFrame
{
    /// <summary>
    /// Builds the test frame: the alignment rectangle in green with the name scanned inside it in the
    /// same green. Rectangle geometry uses the setup rect's normalized units exactly like the
    /// uncalibrated safety frame (1.0 = full scanner field).
    /// </summary>
    public static LaserPoint[] Build(string name, int cx, int cy, int halfWidth, int halfHeight)
    {
        var rect = LaserPipeline.BuildSetupRectangle(cx, cy, halfWidth, halfHeight);

        var points = new List<LaserPoint>(rect.Length + 2048);
        points.AddRange(rect);

        if (!string.IsNullOrWhiteSpace(name))
            AppendText(points, name.Trim().ToUpperInvariant(), cx, cy, halfHeight);

        return points.ToArray();
    }

    private static void AppendText(List<LaserPoint> points, string text, int cx, int cy, int rectHalfHeight)
    {
        // Letter height: a fifth of the rectangle height, clamped so it stays scannable at both extremes.
        var letterHeight = Math.Clamp(rectHalfHeight * 0.4f, 600f, 9000f);
        const float letterSpacing = 0.35f;
        const float glyphWidth = 0.72f;
        var advance = glyphWidth * letterHeight + letterSpacing * letterHeight;
        var totalWidth = advance * text.Length - letterSpacing * letterHeight;

        var startX = cx - totalWidth * 0.5f;
        var baselineY = cy - letterHeight * 0.5f;
        var step = MathF.Max(140f, letterHeight / 10f);

        for (var i = 0; i < text.Length; i++)
        {
            if (!Glyphs.TryGetValue(text[i], out var strokes))
            {
                // Unknown character: scan a filler box so gaps in the name stay readable as gaps.
                strokes = UnknownGlyph;
            }

            var originX = startX + i * advance;
            AppendGlyph(points, strokes, originX, baselineY, letterHeight, step);
        }
    }

    private static void AppendGlyph(List<LaserPoint> points, float[][] strokes, float originX, float baselineY,
                                    float letterHeight, float step)
    {
        foreach (var stroke in strokes)
        {
            if (stroke.Length < 4)
                continue;

            // Blanked approach to the stroke start, then lit samples along the whole polyline -
            // corners stay lit (the optimizer adds dwell), only the travel in is dark.
            var px = originX + stroke[0] * letterHeight;
            var py = baselineY + stroke[1] * letterHeight;
            points.Add(LaserPoint.CreateBlanked((int)px, (int)py));
            points.Add(new LaserPoint((int)px, (int)py, 0, 65535, 0));

            for (var i = 2; i + 1 < stroke.Length; i += 2)
            {
                var x = originX + stroke[i] * letterHeight;
                var y = baselineY + stroke[i + 1] * letterHeight;
                var dx = x - px;
                var dy = y - py;
                var length = MathF.Sqrt(dx * dx + dy * dy);
                var samples = Math.Max(1, (int)MathF.Ceiling(length / step));
                for (var s = 1; s <= samples; s++)
                {
                    var t = s / (float)samples;
                    points.Add(new LaserPoint((int)(px + dx * t), (int)(py + dy * t), 0, 65535, 0));
                }

                px = x;
                py = y;
            }
        }
    }

    /// <summary>Stroke font in a 0..1-high cell (y up), x 0..0.72; each stroke is x0,y0,x1,y1 pairs.
    /// Straight-segment stick shapes - coarse at small sizes, unambiguous at scan rates.</summary>
    private static readonly Dictionary<char, float[][]> Glyphs = new()
    {
        // A
        ['A'] = [[0, 0, 0.36f, 1, 0.72f, 0], [0.12f, 0.34f, 0.6f, 0.34f]],
        // B
        ['B'] = [[0, 0, 0, 1, 0.5f, 1, 0.66f, 0.82f, 0.5f, 0.55f, 0, 0.55f], [0, 0, 0.52f, 0, 0.68f, 0.18f, 0.5f, 0.55f]],
        // C
        ['C'] = [[0.66f, 0.85f, 0.45f, 1, 0.15f, 0.95f, 0, 0.6f, 0, 0.4f, 0.15f, 0.05f, 0.45f, 0, 0.66f, 0.15f]],
        // D
        ['D'] = [[0, 0, 0, 1, 0.45f, 1, 0.72f, 0.7f, 0.72f, 0.3f, 0.45f, 0, 0, 0]],
        // E
        ['E'] = [[0.7f, 1, 0, 1, 0, 0, 0.7f, 0], [0, 0.55f, 0.55f, 0.55f]],
        // F
        ['F'] = [[0.7f, 1, 0, 1, 0, 0], [0, 0.55f, 0.55f, 0.55f]],
        // G
        ['G'] = [[0.66f, 0.85f, 0.45f, 1, 0.15f, 0.95f, 0, 0.6f, 0, 0.4f, 0.15f, 0.05f, 0.45f, 0, 0.7f, 0], [0.7f, 0, 0.7f, 0.4f, 0.4f, 0.4f]],
        // H
        ['H'] = [[0, 0, 0, 1], [0.72f, 0, 0.72f, 1], [0, 0.52f, 0.72f, 0.52f]],
        // I
        ['I'] = [[0.36f, 0, 0.36f, 1], [0.1f, 1, 0.62f, 1], [0.1f, 0, 0.62f, 0]],
        // J
        ['J'] = [[0.6f, 1, 0.6f, 0.2f, 0.42f, 0, 0.18f, 0, 0, 0.2f]],
        // K
        ['K'] = [[0, 0, 0, 1], [0.7f, 1, 0, 0.48f, 0.7f, 0]],
        // L
        ['L'] = [[0, 1, 0, 0, 0.7f, 0]],
        // M
        ['M'] = [[0, 0, 0, 1, 0.36f, 0.55f, 0.72f, 1, 0.72f, 0]],
        // N
        ['N'] = [[0, 0, 0, 1, 0.72f, 0, 0.72f, 1]],
        // O
        ['O'] = [[0.2f, 1, 0.52f, 1, 0.72f, 0.75f, 0.72f, 0.25f, 0.52f, 0, 0.2f, 0, 0, 0.25f, 0, 0.75f, 0.2f, 1]],
        // P
        ['P'] = [[0, 0, 0, 1, 0.55f, 1, 0.72f, 0.8f, 0.55f, 0.55f, 0, 0.55f]],
        // Q
        ['Q'] = [[0.2f, 1, 0.52f, 1, 0.72f, 0.75f, 0.72f, 0.25f, 0.52f, 0, 0.2f, 0, 0, 0.25f, 0, 0.75f, 0.2f, 1], [0.42f, 0.28f, 0.75f, 0]],
        // R
        ['R'] = [[0, 0, 0, 1, 0.55f, 1, 0.72f, 0.8f, 0.55f, 0.55f, 0, 0.55f], [0.3f, 0.55f, 0.72f, 0]],
        // S
        ['S'] = [[0.7f, 0.85f, 0.5f, 1, 0.2f, 1, 0.02f, 0.85f, 0.2f, 0.62f, 0.5f, 0.55f, 0.68f, 0.42f, 0.7f, 0.18f, 0.5f, 0, 0.2f, 0, 0.02f, 0.15f]],
        // T
        ['T'] = [[0.36f, 0, 0.36f, 1], [0.05f, 1, 0.67f, 1]],
        // U
        ['U'] = [[0, 1, 0, 0.25f, 0.2f, 0, 0.52f, 0, 0.72f, 0.25f, 0.72f, 1]],
        // V
        ['V'] = [[0, 1, 0.36f, 0, 0.72f, 1]],
        // W
        ['W'] = [[0, 1, 0.18f, 0, 0.36f, 0.55f, 0.54f, 0, 0.72f, 1]],
        // X
        ['X'] = [[0, 0, 0.72f, 1], [0, 1, 0.72f, 0]],
        // Y
        ['Y'] = [[0, 1, 0.36f, 0.45f, 0.72f, 1], [0.36f, 0.45f, 0.36f, 0]],
        // Z
        ['Z'] = [[0, 1, 0.72f, 1, 0, 0, 0.72f, 0]],
        // Digits
        ['0'] = [[0.2f, 1, 0.52f, 1, 0.72f, 0.75f, 0.72f, 0.25f, 0.52f, 0, 0.2f, 0, 0, 0.25f, 0, 0.75f, 0.2f, 1], [0.05f, 0.05f, 0.67f, 0.95f]],
        ['1'] = [[0.15f, 0.75f, 0.4f, 1, 0.4f, 0, 0.4f, 0], [0.1f, 0, 0.7f, 0]],
        ['2'] = [[0.05f, 0.85f, 0.25f, 1, 0.55f, 1, 0.7f, 0.82f, 0.7f, 0.6f, 0.05f, 0, 0.7f, 0]],
        ['3'] = [[0.05f, 1, 0.7f, 1, 0.4f, 0.55f, 0.62f, 0.55f], [0.62f, 0.55f, 0.72f, 0.35f, 0.6f, 0.05f, 0.35f, 0, 0.05f, 0.1f]],
        ['4'] = [[0.55f, 0, 0.55f, 1, 0, 0.3f, 0.72f, 0.3f]],
        ['5'] = [[0.7f, 1, 0.05f, 1, 0.05f, 0.6f, 0.45f, 0.6f, 0.68f, 0.4f, 0.68f, 0.18f, 0.45f, 0, 0.15f, 0, 0.05f, 0.1f]],
        ['6'] = [[0.65f, 0.9f, 0.4f, 1, 0.12f, 0.8f, 0.05f, 0.4f, 0.15f, 0.05f, 0.45f, 0, 0.65f, 0.15f, 0.65f, 0.4f, 0.45f, 0.55f, 0.15f, 0.55f, 0.05f, 0.4f]],
        ['7'] = [[0, 1, 0.7f, 1, 0.28f, 0]],
        ['8'] = [[0.2f, 0.55f, 0.05f, 0.75f, 0.2f, 1, 0.52f, 1, 0.68f, 0.78f, 0.5f, 0.55f, 0.2f, 0.55f], [0.5f, 0.55f, 0.7f, 0.35f, 0.55f, 0.12f, 0.2f, 0.12f, 0.02f, 0.32f, 0.2f, 0.55f]],
        ['9'] = [[0.07f, 0.1f, 0.32f, 0, 0.58f, 0.2f, 0.66f, 0.6f, 0.55f, 0.95f, 0.28f, 1, 0.08f, 0.85f, 0.08f, 0.6f, 0.28f, 0.45f, 0.55f, 0.45f, 0.65f, 0.6f]],
        // Punctuation
        ['-'] = [[0.1f, 0.5f, 0.62f, 0.5f]],
        ['.'] = [[0.32f, 0, 0.4f, 0]],
        ['_'] = [[0, 0, 0.72f, 0]],
        [' '] = [],
    };

    private static readonly float[][] UnknownGlyph = [[0, 0, 0, 1, 0.72f, 1, 0.72f, 0, 0, 0]];
}
