using System;
using System.Collections.Generic;

namespace T3.Core.DataTypes;

/// <summary>
/// Point-data safety transforms for laser output, applied in a fixed order:
/// blanking normalization → scan-field clamp → jump-distance limiting → static-dwell blanking →
/// intensity ceiling → out-of-bounds audit. A later stage relies on the guarantees
/// of the earlier ones, so the order must not be changed.
///
/// These functions reduce the risk of unsafe content reaching a projector, but they
/// are not a compliance certificate: audience scanning also requires a hardware
/// scan-fail interlock that cuts the beam independently of this software
/// (IEC 60825-1 / FDA CDRH variance territory).
/// </summary>
public static class LaserSafetyTransforms
{
    /// <summary>Symmetric ILDA 16-bit coordinate bounds.</summary>
    public const int FieldMin = -32767;
    public const int FieldMax = 32767;

    /// <summary>Hard cap on list growth from jump subdivision, so a tiny MaxStepPerPoint
    /// against full-field jumps cannot balloon memory. Real points are never dropped,
    /// only subdivision points stop being inserted.</summary>
    private const int MaxTotalPoints = 131072;

    /// <summary>
    /// Safety configuration. Externalized so limits are never hardcoded at a call site
    /// and can be tuned per venue from operator inputs.
    /// </summary>
    public struct Config
    {
        /// <summary>Master beam switch. False blanks every point (positions are kept so the scanners keep moving the same path).</summary>
        public bool BeamEnable;

        /// <summary>Safe-zone rectangle in ILDA 16-bit units; everything outside is clipped in. Default is the full field.</summary>
        public float ZoneCenterX, ZoneCenterY;
        public float ZoneHalfWidth, ZoneHalfHeight;

        /// <summary>Consecutive points farther apart than this get subdivided so scanners can track the move linearly instead of overshooting.</summary>
        public float MaxStepPerPoint;

        /// <summary>How many consecutive near-motionless lit points are allowed before the rest of the run is blanked. Scan-fail protection.</summary>
        public int MaxStaticPoints;

        /// <summary>Step length (ILDA units) below which a point counts as "not moving".</summary>
        public int StaticDistance;

        /// <summary>0..1 ceiling applied to R, G and B of every lit point, independent of what upstream content requests.</summary>
        public float MaxBrightness;

        public static Config Default => new()
        {
            BeamEnable = true,
            ZoneCenterX = 0f,
            ZoneCenterY = 0f,
            ZoneHalfWidth = FieldMax,
            ZoneHalfHeight = FieldMax,
            MaxStepPerPoint = 4000f,
            MaxStaticPoints = 64,
            StaticDistance = 5,
            MaxBrightness = 1f,
        };
    }

    /// <summary>Counters from the last Apply, surfaced to the operator user so safety actions are visible.</summary>
    public struct Stats
    {
        public int ClampedPoints;
        public int JumpPointsInserted;
        public int StaticBlanked;
        public int IntensityScaled;
        public int OutOfBoundsBlanked;
        public bool BeamDisabled;

        public readonly bool AnythingEngaged => BeamDisabled || ClampedPoints > 0 || JumpPointsInserted > 0
                                                || StaticBlanked > 0 || IntensityScaled > 0 || OutOfBoundsBlanked > 0;

        public readonly string Describe(int pointCount)
        {
            if (BeamDisabled)
                return $"BEAM DISABLED - {pointCount} points blanked";

            if (!AnythingEngaged)
                return $"OK - {pointCount} points";

            var parts = new List<string>(5);
            if (ClampedPoints > 0) parts.Add($"safe zone moved {ClampedPoints}");
            if (JumpPointsInserted > 0) parts.Add($"jump limit +{JumpPointsInserted}");
            if (StaticBlanked > 0) parts.Add($"static dwell blanked {StaticBlanked}");
            if (IntensityScaled > 0) parts.Add($"brightness capped {IntensityScaled}");
            if (OutOfBoundsBlanked > 0) parts.Add($"out-of-bounds blanked {OutOfBoundsBlanked}");
            return $"SAFETY: {string.Join(" | ", parts)} - {pointCount} points out";
        }
    }

    /// <summary>
    /// Runs the full safety stage. Returns the processed point list (the source list
    /// when no subdivision points were needed). Counters are reset for this run, so a
    /// caller can safely reuse one <see cref="Stats"/> instance across frames.
    /// </summary>
    public static List<LaserPoint> Apply(List<LaserPoint> points, in Config config, ref Stats stats)
    {
        return Apply(points, null, in config, ref stats);
    }

    /// <summary>
    /// The same stage with a caller-owned scratch list for the jump-subdivision pass, so a
    /// steady-state pump never allocates: when subdivision runs, <paramref name="subdivisionScratch"/>
    /// is cleared and filled and returned instead of a fresh list. May be null (allocates then).
    /// </summary>
    public static List<LaserPoint> Apply(List<LaserPoint> points, List<LaserPoint> subdivisionScratch, in Config config, ref Stats stats)
    {
        // Stats describe THIS call ("counters from the last Apply"). Reset up front so a
        // long-lived stream that reuses its Stats does not accumulate a show-long history
        // and report every frame as unsafe.
        stats = default;

        if (points == null)
            return [];

        if (points.Count == 0)
            return points;

        NormalizeBlanking(points);

        if (!config.BeamEnable)
        {
            for (var i = 0; i < points.Count; i++)
                points[i] = LaserPoint.CreateBlanked(points[i].X, points[i].Y);
            stats.BeamDisabled = true;
            return points;
        }

        var safeConfig = Sanitize(in config);
        ClampToScanField(points, in safeConfig, ref stats);
        points = LimitJumpDistance(points, subdivisionScratch, in safeConfig, ref stats);
        LimitStaticDwell(points, in safeConfig, ref stats);
        ClampIntensity(points, in safeConfig, ref stats);
        DetectAndBlankOutOfBounds(points, ref stats);
        return points;
    }

    /// <summary>
    /// Folds non-finite limit values into a conservative configuration. NaN makes every clamp
    /// comparison false, so a NaN limit is not "unlimited" - it silently disables the stage (a NaN
    /// safe-zone width used to pass every coordinate through). Non-finite limits are broken values,
    /// not operator intent: they collapse to the most restrictive interpretation.
    /// </summary>
    private static Config Sanitize(in Config config)
    {
        var result = config;

        // A non-finite center has no meaningful location; the field origin is the only
        // conservative answer and keeps the zone intersected with the physical field.
        if (!float.IsFinite(result.ZoneCenterX))
            result.ZoneCenterX = 0f;
        if (!float.IsFinite(result.ZoneCenterY))
            result.ZoneCenterY = 0f;

        // An infinite or negative extent is already intersected with the physical field, so only
        // NaN needs a value - and the center line is the smallest zone it can mean.
        result.ZoneHalfWidth = NormalizeZoneExtent(result.ZoneHalfWidth);
        result.ZoneHalfHeight = NormalizeZoneExtent(result.ZoneHalfHeight);

        if (!float.IsFinite(result.MaxStepPerPoint))
            result.MaxStepPerPoint = Config.Default.MaxStepPerPoint;

        if (!float.IsFinite(result.MaxBrightness))
            result.MaxBrightness = 0f;

        // The dwell cap and the motion threshold are counts, not geometry: clamp them into
        // the range the stage can actually express instead of letting negative/overflow
        // values silently blank or pass every point.
        result.MaxStaticPoints = Math.Clamp(result.MaxStaticPoints, 0, 100_000);
        result.StaticDistance = Math.Clamp(result.StaticDistance, 0, FieldMax * 2);

        return result;
    }

    /// <summary>
    /// Makes <c>I &lt;= 0</c> mean dark on every transport. Every later stage treats such a point as
    /// blanked and skips it, but no encoder can rely on the I channel alone: PONK and CITP carry no
    /// intensity at all, and many ILDA projectors ignore I and emit on RGB. A point with I = 0 and
    /// colour would therefore bypass the brightness, dwell and bounds stages and still be drawn.
    /// </summary>
    public static void NormalizeBlanking(List<LaserPoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.I <= 0 && (p.I != 0 || p.R != 0 || p.G != 0 || p.B != 0))
                points[i] = LaserPoint.CreateBlanked(p.X, p.Y);
        }
    }

    private static float NormalizeZoneExtent(float halfExtent)
        => float.IsNaN(halfExtent) ? 0f : MathF.Min(MathF.Abs(halfExtent), FieldMax);

    /// <summary>
    /// Hard-clips every point into the safe-zone rectangle, intersected with the
    /// physical field. The last line of defense against the beam ever leaving the
    /// intended coverage area.
    /// </summary>
    public static void ClampToScanField(List<LaserPoint> points, in Config config, ref Stats stats)
    {
        var safeConfig = Sanitize(in config);
        var minX = Math.Max(safeConfig.ZoneCenterX - safeConfig.ZoneHalfWidth, FieldMin);
        var maxX = Math.Min(safeConfig.ZoneCenterX + safeConfig.ZoneHalfWidth, FieldMax);
        var minY = Math.Max(safeConfig.ZoneCenterY - safeConfig.ZoneHalfHeight, FieldMin);
        var maxY = Math.Min(safeConfig.ZoneCenterY + safeConfig.ZoneHalfHeight, FieldMax);
        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minY > maxY) (minY, maxY) = (maxY, minY);

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var x = Math.Clamp(p.X, minX, maxX);
            var y = Math.Clamp(p.Y, minY, maxY);
            if ((int)x != p.X || (int)y != p.Y)
            {
                points[i] = new LaserPoint((int)x, (int)y, p.R, p.G, p.B, p.I);
                stats.ClampedPoints++;
            }
        }
    }

    /// <summary>
    /// Subdivides jumps between consecutive points that are longer than MaxStepPerPoint,
    /// so the scanners can track the move linearly instead of overshooting or arcing
    /// through unintended positions. Inserted points are blanked when either end of the
    /// jump is blanked; lit jumps carry the destination point's color.
    /// </summary>
    public static List<LaserPoint> LimitJumpDistance(List<LaserPoint> points, in Config config, ref Stats stats)
    {
        return LimitJumpDistance(points, null, in config, ref stats);
    }

    private static List<LaserPoint> LimitJumpDistance(List<LaserPoint> points, List<LaserPoint> subdivisionScratch, in Config config, ref Stats stats)
    {
        var maxStep = MathF.Max(1f, Sanitize(in config).MaxStepPerPoint);

        // Fast path: measure first so frames without long jumps don't allocate.
        // Cast to float before squaring to avoid int32 overflow (e.g. 60000*60000 > int.MaxValue).
        var longestSq = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = (float)(points[i].X - points[i - 1].X);
            var dy = (float)(points[i].Y - points[i - 1].Y);
            longestSq = MathF.Max(longestSq, dx * dx + dy * dy);
        }

        if (MathF.Sqrt(longestSq) <= maxStep)
            return points;

        var result = subdivisionScratch ?? new List<LaserPoint>(points.Count * 2);
        result.Clear();
        result.Add(points[0]);

        for (var i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1];
            var cur = points[i];
            // Cast to float to avoid int32 overflow on long jumps (e.g. 60000*60000 > int.MaxValue)
            var dx = (float)(cur.X - prev.X);
            var dy = (float)(cur.Y - prev.Y);
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist > maxStep && result.Count < MaxTotalPoints)
            {
                var steps = (int)MathF.Ceiling(dist / maxStep);
                var jumpLit = prev.I > 0 && cur.I > 0;

                for (var s = 1; s < steps && result.Count < MaxTotalPoints; s++)
                {
                    var t = s / (float)steps;
                    var x = (int)MathF.Round(prev.X + dx * t);
                    var y = (int)MathF.Round(prev.Y + dy * t);
                    result.Add(jumpLit
                                   ? new LaserPoint(x, y, cur.R, cur.G, cur.B, cur.I)
                                   : LaserPoint.CreateBlanked(x, y));
                    stats.JumpPointsInserted++;
                }
            }

            result.Add(cur);
        }

        return result;
    }

    /// <summary>
    /// Caps how long the beam may sit near-motionless at one coordinate - a frozen lit beam is the
    /// classic scan-failure hazard. Points beyond the cap keep their position but are blanked, so
    /// the scanners keep moving with the beam off.
    ///
    /// The frame is treated as a loop, because that is how it is played: a DAC replays the last
    /// frame until a new one arrives. A run therefore continues across the seam (last point back
    /// to the first), and only movement ends it - blanking in place does not, since the beam comes
    /// back on at the same spot. A frame that never moves at all would be a static beam for as long
    /// as it is shown, so all of its lit points are blanked.
    /// </summary>
    public static void LimitStaticDwell(List<LaserPoint> points, in Config config, ref Stats stats)
    {
        var count = points.Count;
        if (count == 0)
            return;

        var safeConfig = Sanitize(in config);
        // Widened before squaring: a full-field step squared overflows int and would read as "not moving".
        var staticDistance = (long)safeConfig.StaticDistance;
        var thresholdSq = staticDistance * staticDistance;
        var maxStaticPoints = safeConfig.MaxStaticPoints;

        // Start right after a movement, so the run that wraps over the seam is counted in one pass.
        var start = -1;
        for (var i = 0; i < count; i++)
        {
            if (MovesFromPrevious(points, i, thresholdSq))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            for (var i = 0; i < count; i++)
            {
                var p = points[i];
                if (p.I <= 0)
                    continue;

                points[i] = LaserPoint.CreateBlanked(p.X, p.Y);
                stats.StaticBlanked++;
            }

            return;
        }

        var run = 0;
        for (var k = 0; k < count; k++)
        {
            var i = (start + k) % count;
            if (MovesFromPrevious(points, i, thresholdSq))
                run = 0;

            var p = points[i];
            if (p.I <= 0)
                continue;

            run++;
            if (run > maxStaticPoints)
            {
                points[i] = LaserPoint.CreateBlanked(p.X, p.Y);
                stats.StaticBlanked++;
            }
        }
    }

    /** True when point <paramref name="index"/> is farther than the static threshold from the point before it in loop order. */
    private static bool MovesFromPrevious(List<LaserPoint> points, int index, long thresholdSq)
    {
        var previous = points[index == 0 ? points.Count - 1 : index - 1];
        var current = points[index];
        long dx = current.X - previous.X;
        long dy = current.Y - previous.Y;
        return dx * dx + dy * dy > thresholdSq;
    }

    /// <summary>
    /// Hard ceiling on output color, independent of whatever upstream content requests. Scales the
    /// I channel too: a single-colour projector driven from I would otherwise ignore the ceiling.
    /// A lit point whose intensity rounds to zero becomes a canonical blank. Also folds stray
    /// out-of-range values into 0..65535.
    /// </summary>
    public static void ClampIntensity(List<LaserPoint> points, in Config config, ref Stats stats)
    {
        var scale = Math.Clamp(Sanitize(in config).MaxBrightness, 0f, 1f);

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.I <= 0)
                continue;

            var r = (int)Math.Min(Math.Max(p.R, 0) * scale, 65535f);
            var g = (int)Math.Min(Math.Max(p.G, 0) * scale, 65535f);
            var b = (int)Math.Min(Math.Max(p.B, 0) * scale, 65535f);
            var intensity = (int)Math.Min(p.I * scale, 65535f);
            if (r == p.R && g == p.G && b == p.B && intensity == p.I)
                continue;

            points[i] = intensity > 0
                            ? new LaserPoint(p.X, p.Y, r, g, b, intensity)
                            : LaserPoint.CreateBlanked(p.X, p.Y);
            stats.IntensityScaled++;
        }
    }

    /// <summary>
    /// Belt-and-suspenders final audit: a lit point outside the physical field is
    /// force-blanked rather than drawn. Should never trip after ClampToScanField - if it
    /// does, an earlier stage was bypassed and the counters make that visible.
    /// </summary>
    public static void DetectAndBlankOutOfBounds(List<LaserPoint> points, ref Stats stats)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.I <= 0)
                continue;

            if (p.X < FieldMin || p.X > FieldMax || p.Y < FieldMin || p.Y > FieldMax)
            {
                points[i] = LaserPoint.CreateBlanked(p.X, p.Y);
                stats.OutOfBoundsBlanked++;
            }
        }
    }

    /// <summary>
    /// Copies source into destination with all color zeroed (positions preserved, so
    /// scanners keep moving the same path with the beam off). Used by output operators
    /// for E-stop and watchdog blanking. Iterates only to the shorter of the two arrays
    /// so a mismatch cannot cause an IndexOutOfRangeException.
    /// </summary>
    public static void BlankCopyInto(LaserPoint[] source, LaserPoint[] destination)
    {
        var count = Math.Min(source.Length, destination.Length);
        for (var i = 0; i < count; i++)
            destination[i] = LaserPoint.CreateBlanked(source[i].X, source[i].Y);
    }
}
