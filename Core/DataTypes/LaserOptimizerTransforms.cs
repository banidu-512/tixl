using System;
using System.Collections.Generic;

namespace T3.Core.DataTypes;

/// <summary>
/// The laser optimizer as a static, stateless-per-frame transform — the exact algorithm of the
/// [LaserOptimizer] operator, lifted so the output-setup pipeline and the operator share one
/// implementation. Reorders lit segments into a greedy nearest-neighbour tour (reversing segments
/// when that is closer), inserts distance-scaled blanked jumps between shapes and dwells on sharp
/// corners. Callers own the scratch buffers; nothing carries semantic state across frames.
/// </summary>
public static class LaserOptimizerTransforms
{
    /// <summary>Hard cap on generated points (1M points = 24 MB scratch) - bounds damage from
    /// pathological input, mirroring LaserPointsToBuffer.MaxPoints.</summary>
    public const int MaxOutputPoints = 1 << 20;

    public struct Config
    {
        /// <summary>false = keep the original point order, applying only blanking and corner dwelling.</summary>
        public bool EnableOptimization;

        /// <summary>ILDA units per blanked-jump segment; the blank count scales with jump length.</summary>
        public float MaxJumpDistance;

        /// <summary>Maximum blanked points inserted before a shape start (0..100).</summary>
        public int BlankingDelayPoints;

        /// <summary>Corners sharper than this angle (degrees) get dwell anchors.</summary>
        public float CornerAngleThreshold;

        /// <summary>Repeated points at a corner (1..10).</summary>
        public int CornerAnchorCount;

        public static Config Default => new()
                                           {
                                               EnableOptimization = true,
                                               MaxJumpDistance = 5000f,
                                               BlankingDelayPoints = 5,
                                               CornerAngleThreshold = 30f,
                                               CornerAnchorCount = 3,
                                           };
    }

    /// <summary>Per-caller scratch buffers, reused across frames to avoid allocation. Not thread-safe:
    /// one instance per consumer (the operator, each output op).</summary>
    public sealed class Scratch
    {
        public LaserPoint[] Output = new LaserPoint[0];
        public readonly List<int> SegmentStartIndices = [];
        public readonly List<int> SegmentEndIndices = [];
        public readonly List<int> TourOrder = [];
        public bool[] Visited = new bool[256];
    }

    /// <summary>
    /// Runs the optimizer over <paramref name="input"/> into a freshly sized result array.
    /// Returns an empty array for empty input. A bad frame degrades (bounded writes), never throws.
    /// </summary>
    public static LaserPoint[] Apply(LaserPoint[] input, int count, in Config config, Scratch scratch)
    {
        if (input == null || count <= 0)
            return Array.Empty<LaserPoint>();

        var blankingDelay = Math.Clamp(config.BlankingDelayPoints, 0, 100);
        var cornerAnchors = Math.Clamp(config.CornerAnchorCount, 1, 10);

        var resultSize = EstimateResultSize(count, blankingDelay, cornerAnchors);
        if (scratch.Output.Length < resultSize)
            scratch.Output = new LaserPoint[resultSize];

        if (config.EnableOptimization)
        {
            OptimizePath(input, count, scratch.Output, config.MaxJumpDistance, blankingDelay,
                         config.CornerAngleThreshold, cornerAnchors, scratch, out var outputCount);
            return CopyExact(scratch.Output, outputCount);
        }
        else
        {
            ApplyCornerDwellingAndBlanking(input, count, scratch.Output, config.MaxJumpDistance, blankingDelay,
                                           config.CornerAngleThreshold, cornerAnchors, out var outputCount);
            return CopyExact(scratch.Output, outputCount);
        }
    }

    private static LaserPoint[] CopyExact(LaserPoint[] buffer, int outputCount)
    {
        outputCount = Math.Min(outputCount, buffer.Length);
        var result = new LaserPoint[outputCount];
        Array.Copy(buffer, 0, result, 0, outputCount);
        return result;
    }

    /// <summary>
    /// Allocate the true worst case (every input point preceded by a full blank run and a full
    /// corner-dwell run) so jump-heavy frames cannot overrun the buffer; the bounded writes are
    /// the second line of defense.
    /// </summary>
    private static int EstimateResultSize(int inputCount, int blankingDelay, int cornerAnchors)
    {
        var worstCase = inputCount * (1 + blankingDelay + Math.Max(0, cornerAnchors - 1)) + 1;
        return Math.Min(worstCase, MaxOutputPoints);
    }

    /// <summary>Bounded write into the output buffer. A full buffer drops the point rather than
    /// overrunning the array - a bad frame must degrade, never crash.</summary>
    private static bool TryAddPoint(LaserPoint[] output, ref int outputCount, in LaserPoint point)
    {
        if (outputCount >= output.Length)
            return false;

        output[outputCount++] = point;
        return true;
    }

    private static void OptimizePath(
        LaserPoint[] input, int inputCount,
        LaserPoint[] output,
        float maxJumpDistance, int blankingDelayPoints, float cornerAngleThreshold, int cornerAnchorCount,
        Scratch scratch, out int outputCount)
    {
        outputCount = 0;
        if (inputCount == 0)
            return;

        var starts = scratch.SegmentStartIndices;
        var ends = scratch.SegmentEndIndices;
        starts.Clear();
        ends.Clear();

        FindSegments(input, inputCount, starts, ends);

        if (starts.Count == 0)
        {
            if (inputCount > 0)
                TryAddPoint(output, ref outputCount, input[0]);
            return;
        }

        var visited = scratch.Visited;
        if (visited.Length < starts.Count)
        {
            visited = new bool[starts.Count * 2];
            scratch.Visited = visited;
        }
        Array.Clear(visited, 0, starts.Count);

        var currentX = 0;
        var currentY = 0;

        // The greedy tour is collected first and emitted after a rotation pass: the DAC replays
        // the finished frame in a continuous loop, so the link from the LAST emitted shape back
        // to the FIRST is scanned just as often as every other link. Starting the tour at the
        // shape nearest null (the natural greedy seed) puts that loop seam wherever it happens
        // to fall - rotating the visit order so the seam is the shortest available link keeps
        // the loop closed and smooth, which is repeated 40 times a second on a typical frame.
        var tourOrder = scratch.TourOrder;
        tourOrder.Clear();

        for (var i = 0; i < starts.Count; i++)
        {
            var bestDist = float.MaxValue;
            var bestIndex = -1;
            var bestReverse = false;

            for (var j = 0; j < starts.Count; j++)
            {
                if (visited[j])
                    continue;

                var startIdx = starts[j];
                var endIdx = ends[j];

                var distToStart = CalculateDistanceSquared(currentX, currentY, input[startIdx].X, input[startIdx].Y);
                var distToEnd = CalculateDistanceSquared(currentX, currentY, input[endIdx].X, input[endIdx].Y);

                if (distToStart < bestDist)
                {
                    bestDist = distToStart;
                    bestIndex = j;
                    bestReverse = false;
                }

                if (distToEnd < bestDist)
                {
                    bestDist = distToEnd;
                    bestIndex = j;
                    bestReverse = true;
                }
            }

            if (bestIndex == -1)
                break;

            visited[bestIndex] = true;
            tourOrder.Add(bestReverse ? ~bestIndex : bestIndex);

            var segStart = starts[bestIndex];
            var segEnd = ends[bestIndex];
            currentX = input[bestReverse ? segStart : segEnd].X;
            currentY = input[bestReverse ? segStart : segEnd].Y;
        }

        var rotation = FindBestSeamRotation(input, starts, ends, tourOrder);

        var emitX = 0;
        var emitY = 0;
        for (var i = 0; i < tourOrder.Count; i++)
        {
            var entry = tourOrder[(i + rotation) % tourOrder.Count];
            var bestReverse = entry < 0;
            var bestIndex = bestReverse ? ~entry : entry;
            var segStart = starts[bestIndex];
            var segEnd = ends[bestIndex];

            InsertBlankingIfNeeded(output, ref outputCount, emitX, emitY, input[segStart].X, input[segStart].Y, maxJumpDistance, blankingDelayPoints);

            if (bestReverse)
            {
                for (var k = segEnd; k >= segStart; k--)
                {
                    if (!TryAddPoint(output, ref outputCount, input[k]))
                        break;
                }
            }
            else
            {
                for (var k = segStart; k <= segEnd; k++)
                {
                    if (!TryAddPoint(output, ref outputCount, input[k]))
                        break;
                }
            }

            emitX = input[bestReverse ? segStart : segEnd].X;
            emitY = input[bestReverse ? segStart : segEnd].Y;
        }

        ApplyCornerDwellingToOutput(output, ref outputCount, cornerAngleThreshold, cornerAnchorCount);
    }

    /// <summary>
    /// Picks the rotation of the greedy tour whose closing link (last shape's exit point back to
    /// the first shape's entry point) is shortest. Rotating a tour only changes which two
    /// neighbouring links become the seam, so this never adds travel inside the tour.
    /// Deterministic: ties resolve to the earliest rotation.
    /// </summary>
    private static int FindBestSeamRotation(LaserPoint[] input, List<int> starts, List<int> ends, List<int> tourOrder)
    {
        if (tourOrder.Count < 2)
            return 0;

        var bestRotation = 0;
        var bestSeamSq = float.MaxValue;

        for (var r = 0; r < tourOrder.Count; r++)
        {
            // Rotating to start at r: the seam link connects tour order r-1's exit to r's entry.
            var lastEntry = tourOrder[(r + tourOrder.Count - 1) % tourOrder.Count];
            var firstEntry = tourOrder[r];

            var lastIdx = lastEntry < 0 ? ~lastEntry : lastEntry;
            var firstIdx = firstEntry < 0 ? ~firstEntry : firstEntry;

            // Exit of a reversed segment is its start index; entry of a reversed segment is its end.
            var lastExitX = input[lastEntry < 0 ? starts[lastIdx] : ends[lastIdx]].X;
            var lastExitY = input[lastEntry < 0 ? starts[lastIdx] : ends[lastIdx]].Y;
            var firstEntryX = input[firstEntry < 0 ? ends[firstIdx] : starts[firstIdx]].X;
            var firstEntryY = input[firstEntry < 0 ? ends[firstIdx] : starts[firstIdx]].Y;

            var seamSq = CalculateDistanceSquared(lastExitX, lastExitY, firstEntryX, firstEntryY);
            if (seamSq < bestSeamSq)
            {
                bestSeamSq = seamSq;
                bestRotation = r;
            }
        }

        return bestRotation;
    }

    private static void FindSegments(LaserPoint[] input, int inputCount, List<int> startIndices, List<int> endIndices)
    {
        if (inputCount == 0)
            return;

        var segmentStart = 0;
        var isBlanked = input[0].I == 0;

        for (var i = 1; i < inputCount; i++)
        {
            var currentlyBlanked = input[i].I == 0;

            if (currentlyBlanked != isBlanked)
            {
                if (!isBlanked)
                {
                    startIndices.Add(segmentStart);
                    endIndices.Add(i - 1);
                }

                isBlanked = currentlyBlanked;
                segmentStart = i;
            }
        }

        if (!isBlanked && segmentStart < inputCount)
        {
            startIndices.Add(segmentStart);
            endIndices.Add(inputCount - 1);
        }
    }

    private static void ApplyCornerDwellingAndBlanking(
        LaserPoint[] input, int inputCount,
        LaserPoint[] output,
        float maxJumpDistance, int blankingDelayPoints, float cornerAngleThreshold, int cornerAnchorCount,
        out int outputCount)
    {
        outputCount = 0;
        if (inputCount == 0)
            return;

        var currentX = 0;
        var currentY = 0;

        for (var i = 0; i < inputCount; i++)
        {
            InsertBlankingIfNeeded(output, ref outputCount, currentX, currentY, input[i].X, input[i].Y, maxJumpDistance, blankingDelayPoints);

            if (!TryAddPoint(output, ref outputCount, input[i]))
                break;

            currentX = input[i].X;
            currentY = input[i].Y;

            if (i < inputCount - 1 && input[i].I > 0 && input[i + 1].I > 0)
            {
                if (i < inputCount - 2)
                {
                    var angle = CalculateAngle(input[i], input[i + 1], input[i + 2]);
                    if (angle < cornerAngleThreshold)
                    {
                        for (var a = 1; a < cornerAnchorCount; a++)
                        {
                            if (outputCount < output.Length)
                                output[outputCount++] = input[i + 1];
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Corner dwell on the finished tour. A sharp turn (angle at a lit point between its
    /// neighbours below the threshold) repeats the corner point so the beam lingers and the
    /// corner renders sharply. Reads only written samples: the original version probed the
    /// not-yet-written slot after the write head - an always-zero point that made every lit
    /// point register as a corner aimed at the origin and injected those zero samples into
    /// the frame (the scope's "beam goes to the middle between shapes").
    /// </summary>
    private static void ApplyCornerDwellingToOutput(
        LaserPoint[] output, ref int outputCount, float cornerAngleThreshold, int cornerAnchorCount)
    {
        if (outputCount < 3 || cornerAnchorCount <= 1)
            return;

        var originalCount = outputCount;
        var tempArray = new LaserPoint[output.Length];

        var writeIndex = 0;
        tempArray[writeIndex++] = output[0];

        for (var i = 1; i < originalCount; i++)
        {
            if (writeIndex >= tempArray.Length)
                break;

            tempArray[writeIndex++] = output[i];

            // Sharp corner at this point: measured between the two written neighbours and
            // the next input point, all real samples.
            if (i + 1 < originalCount && output[i].I > 0 && output[i + 1].I > 0 && writeIndex >= 2)
            {
                var angle = CalculateAngle(tempArray[writeIndex - 2], tempArray[writeIndex - 1], output[i + 1]);
                if (angle < cornerAngleThreshold)
                {
                    for (var a = 1; a < cornerAnchorCount && writeIndex < tempArray.Length; a++)
                    {
                        tempArray[writeIndex] = tempArray[writeIndex - 1];
                        writeIndex++;
                    }
                }
            }
        }

        Array.Copy(tempArray, output, writeIndex);
        outputCount = writeIndex;
    }

    /// <summary>
    /// Blanked approach to a shape start. The beam must always be off while moving between two
    /// shapes - otherwise nearby shapes get connected by a lit line, and dot-like single points
    /// would burn strokes to their neighbours. The blanked samples are distributed along the
    /// travel line so the scanners track a ramp instead of a single-sample step (a step is a
    /// maximal-acceleration demand that rings the galvos - the opposite of longevity), with the
    /// last samples held at the target so the scanners settle before the beam turns on.
    /// Sub-pixel moves are skipped: the beam is already at the target.
    /// </summary>
    private static void InsertBlankingIfNeeded(
        LaserPoint[] output, ref int outputCount,
        int fromX, int fromY, int toX, int toY,
        float maxJumpDistance, int blankingDelayPoints)
    {
        if (outputCount == 0)
            return;

        if (maxJumpDistance <= 0 || blankingDelayPoints <= 0)
            return;

        // Cast to float before squaring: int dx*dx overflows for jumps beyond 46341 units and the
        // negative result silently skipped blanking on exactly the full-field jumps that need it
        // most (same bug as in LaserSafetyTransforms).
        var dx = (float)(toX - fromX);
        var dy = (float)(toY - fromY);
        var distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance < 0.5f)
            return;

        var blankCount = (int)MathF.Ceiling(distance / maxJumpDistance * blankingDelayPoints);
        blankCount = Math.Clamp(blankCount, 1, blankingDelayPoints);

        // Most samples ramp along the path, the rest settle at the target. One sample alone
        // cannot both ramp and settle - keep it as a pure settle like the classic blanking shift.
        var settleCount = Math.Min(2, blankCount - 1);
        var rampCount = blankCount - settleCount;

        for (var b = 1; b <= rampCount && outputCount < output.Length; b++)
        {
            var t = b / (float)(rampCount + 1);
            output[outputCount++] = LaserPoint.CreateBlanked(
                (int)MathF.Round(fromX + dx * t),
                (int)MathF.Round(fromY + dy * t));
        }

        for (var s = 0; s < settleCount && outputCount < output.Length; s++)
            output[outputCount++] = LaserPoint.CreateBlanked(toX, toY);
    }

    private static float CalculateAngle(LaserPoint p1, LaserPoint p2, LaserPoint p3)
    {
        var dx1 = p2.X - p1.X;
        var dy1 = p2.Y - p1.Y;
        var dx2 = p3.X - p2.X;
        var dy2 = p3.Y - p2.Y;

        var len1 = MathF.Sqrt(dx1 * dx1 + dy1 * dy1);
        var len2 = MathF.Sqrt(dx2 * dx2 + dy2 * dy2);

        if (len1 < 0.001f || len2 < 0.001f)
            return 180f;

        var dot = dx1 * dx2 + dy1 * dy2;
        var cosAngle = dot / (len1 * len2);
        cosAngle = Math.Clamp(cosAngle, -1f, 1f);

        var angleRad = MathF.Acos(cosAngle);
        return angleRad * (180f / MathF.PI);
    }

    private static float CalculateAngleInOutput(LaserPoint[] points, int i1, int i2, int i3)
    {
        if (i1 < 0 || i2 < 0 || i3 < 0 || i1 >= points.Length || i2 >= points.Length || i3 >= points.Length)
            return 180f;

        return CalculateAngle(points[i1], points[i2], points[i3]);
    }

    private static float CalculateDistanceSquared(int x1, int y1, int x2, int y2)
    {
        // Cast to float before squaring to avoid int32 overflow on long jumps
        // (e.g. 60000*60000 > int.MaxValue), same fix as in LaserSafetyTransforms.
        var dx = (float)(x2 - x1);
        var dy = (float)(y2 - y1);
        return dx * dx + dy * dy;
    }
}
