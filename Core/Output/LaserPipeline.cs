#nullable enable
using System;
using System.Collections.Generic;
using T3.Core.DataTypes;

namespace T3.Core.Output;

/// <summary>
/// The setup-driven laser pipeline: optimizer + safety transforms configured on a laser output and
/// applied by the output operators before streaming. While the output's safety is uncalibrated, upstream content is
/// ignored and the manual alignment rectangle streams instead (the same behavior as the operator).
/// </summary>
public static class LaserPipeline
{
    /// <summary>Per-consumer scratch (optimizer buffers + safety work list); not thread-safe.</summary>
    public sealed class Session
    {
        private readonly LaserOptimizerTransforms.Scratch _optimizerScratch = new();
        private readonly List<LaserPoint> _safetyWorkList = new(4096);

        /// <summary>
        /// Runs the pipeline for a laser output's processing settings. Returns the points to stream and a
        /// status line. Empty input with calibration on yields an empty result ("No input points").
        /// </summary>
        public LaserPoint[] Apply(LaserProcessingSettings settings, LaserPoint[]? input, int count, out string status)
        {
            if (settings == null)
            {
                status = "No laser processing settings";
                return Array.Empty<LaserPoint>();
            }

            if (!settings.Safety.Calibrated)
            {
                RectFromSafety(settings.Safety, out var cx, out var cy, out var hw, out var hh);
                status = settings.Safety.DescribeSetup();
                return BuildSetupRectangle(cx, cy, hw, hh);
            }

            if (input == null || count <= 0)
            {
                status = "No input points";
                return Array.Empty<LaserPoint>();
            }

            var optimized = LaserOptimizerTransforms.Apply(input, count, settings.Optimizer.ToConfig(), _optimizerScratch);

            _safetyWorkList.Clear();
            for (var i = 0; i < optimized.Length; i++)
                _safetyWorkList.Add(optimized[i]);

            var stats = new LaserSafetyTransforms.Stats();
            var safetyConfig = settings.Safety.ToConfig();
            var result = LaserSafetyTransforms.Apply(_safetyWorkList, in safetyConfig, ref stats);
            status = stats.Describe(result.Count);
            return result.ToArray();
        }
    }

    /// <summary>The setup rect's normalized units mapped to scanner coordinates: center -1..1, width/height
    /// 0..1 where 1.0 spans the full field (-32767..32767). Shared by the uncalibrated safety frame and the
    /// test screen.</summary>
    public static void RectFromSafety(LaserProcessingSettings.SafetySettings safety, out int cx, out int cy, out int halfWidth, out int halfHeight)
    {
        const float field = 32767f;
        cx = (int)(Math.Clamp(safety.SetupCenterX, -1f, 1f) * field);
        cy = (int)(Math.Clamp(safety.SetupCenterY, -1f, 1f) * field);
        halfWidth = (int)(Math.Clamp(safety.SetupWidth, 0.005f, 1f) * field);
        halfHeight = (int)(Math.Clamp(safety.SetupHeight, 0.005f, 1f) * field);
    }

    /// <summary>
    /// Manually parameterized alignment rectangle (green). The user starts with a tiny square at
    /// center and extends width/height step by step — no auto animation. Lifted 1:1 from the
    /// The setup pipeline emits the same frames the safety transform stage produces.
    /// </summary>
    public static LaserPoint[] BuildSetupRectangle(int cx, int cy, int halfW, int halfH)
    {
        const int steps = 40;          // samples per side
        const int cornerDwell = 3;     // extra lit points per corner
        var pts = new LaserPoint[4 * (2 + cornerDwell + steps) + 8];

        Span<(int X, int Y)> corners = stackalloc (int X, int Y)[4]
        {
            (cx - halfW, cy - halfH), (cx + halfW, cy - halfH),
            (cx + halfW, cy + halfH), (cx - halfW, cy + halfH)
        };

        var n = 0;
        for (var side = 0; side < 4; side++)
        {
            var a = corners[side];
            var b = corners[(side + 1) % 4];
            pts[n++] = LaserPoint.CreateBlanked(a.X, a.Y);
            pts[n++] = new LaserPoint(a.X, a.Y, 0, 65535, 0);
            pts[n++] = new LaserPoint(a.X, a.Y, 0, 65535, 0);
            for (var s2 = 1; s2 < steps; s2++)
            {
                var f = (float)s2 / steps;
                var x = a.X + (int)((b.X - a.X) * f);
                var y = a.Y + (int)((b.Y - a.Y) * f);
                pts[n++] = new LaserPoint(x, y, 0, 65535, 0);
            }
        }

        pts[n++] = LaserPoint.CreateBlanked(corners[0].X, corners[0].Y);
        pts[n++] = LaserPoint.CreateBlanked(0, 0);
        Array.Resize(ref pts, n);
        return pts;
    }
}
