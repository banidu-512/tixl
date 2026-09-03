using T3.Core.DataTypes;
using T3.Core.Utils;
using Lib.laser;

namespace Lib.laser.optimize;

[Guid("77932588-E3BB-42E5-8032-3F5759031653")]
internal sealed class LaserOptimizer : Instance<LaserOptimizer>
{
    [Output(Guid = "BB8A62DF-FFB7-4EAF-B36E-362941DE082D")]
    public readonly Slot<StructuredList> OptimizedPoints = new();

    [Output(Guid = "F8FBFD4D-618B-4061-801A-8D2415D3BF3A", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PointCount = new();

    public LaserOptimizer()
    {
        OptimizedPoints.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var inputPoints = LaserPoints.GetValue(context);
        if (inputPoints is not StructuredList<LaserPoint> laserPoints || laserPoints.TypedElements == null || laserPoints.NumElements == 0)
        {
            OptimizedPoints.Value = s_emptyList;
            PointCount.Value = 0;
            return;
        }

        var enableOptimization = EnableOptimization.GetValue(context);
        var maxJumpDistance = MaxJumpDistance.GetValue(context);
        var blankingDelayPoints = BlankingDelayPoints.GetValue(context).Clamp(0, 100);
        var cornerAngleThreshold = CornerAngleThreshold.GetValue(context);
        var cornerAnchorCount = CornerAnchorCount.GetValue(context).Clamp(1, 10);

        var points = laserPoints.TypedElements;
        var pointCount = laserPoints.NumElements;

        if (pointCount == 0)
        {
            OptimizedPoints.Value = s_emptyList;
            PointCount.Value = 0;
            return;
        }

        var resultSize = EstimateResultSize(pointCount, blankingDelayPoints, cornerAnchorCount);
        if (_optimizedPoints.Length < resultSize)
        {
            _optimizedPoints = new LaserPoint[resultSize];
        }

        int outputCount;
        if (enableOptimization)
        {
            OptimizePath(points, _optimizedPoints, maxJumpDistance, blankingDelayPoints, cornerAngleThreshold, cornerAnchorCount, out outputCount);
        }
        else
        {
            ApplyCornerDwellingAndBlanking(points, _optimizedPoints, maxJumpDistance, blankingDelayPoints, cornerAngleThreshold, cornerAnchorCount, out outputCount);
        }

        // Create result array of exact size
        var resultArray = new LaserPoint[outputCount];
        Array.Copy(_optimizedPoints, 0, resultArray, 0, outputCount);

        var result = new StructuredList<LaserPoint>(resultArray);
        OptimizedPoints.Value = result;
        PointCount.Value = result.NumElements;
        PointCount.DirtyFlag.Invalidate();
        OptimizedPoints.DirtyFlag.Trigger = DirtyFlagTrigger.Animated;
    }

    private static int EstimateResultSize(int inputCount, int blankingDelay, int cornerAnchors)
    {
        var estimatedBlanks = inputCount / 10 * blankingDelay;
        var estimatedCorners = inputCount / 20 * (cornerAnchors - 1);
        return inputCount + estimatedBlanks + estimatedCorners;
    }

    private static readonly StructuredList s_emptyList = new StructuredList<LaserPoint>(Array.Empty<LaserPoint>());

    private void OptimizePath(
        LaserPoint[] input,
        LaserPoint[] output,
        float maxJumpDistance,
        int blankingDelayPoints,
        float cornerAngleThreshold,
        int cornerAnchorCount,
        out int outputCount)
    {
        outputCount = 0;

        if (input.Length == 0)
            return;

        // Clear and reuse lists
        _segmentStartIndices.Clear();
        _segmentEndIndices.Clear();

        FindSegments(input, _segmentStartIndices, _segmentEndIndices);

        if (_segmentStartIndices.Count == 0)
        {
            if (input.Length > 0)
            {
                output[outputCount++] = input[0];
            }
            return;
        }

        // Resize visited array if needed
        if (_visited.Length < _segmentStartIndices.Count)
        {
            _visited = new bool[_segmentStartIndices.Count * 2];
        }
        Array.Clear(_visited, 0, _segmentStartIndices.Count);

        var currentX = 0;
        var currentY = 0;

        for (var i = 0; i < _segmentStartIndices.Count; i++)
        {
            var bestDist = float.MaxValue;
            var bestIndex = -1;
            var bestReverse = false;

            for (var j = 0; j < _segmentStartIndices.Count; j++)
            {
                if (_visited[j])
                    continue;

                var startIdx = _segmentStartIndices[j];
                var endIdx = _segmentEndIndices[j];

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

            _visited[bestIndex] = true;
            var segStart = _segmentStartIndices[bestIndex];
            var segEnd = _segmentEndIndices[bestIndex];

            InsertBlankingIfNeeded(output, ref outputCount, currentX, currentY, input[segStart].X, input[segStart].Y, maxJumpDistance, blankingDelayPoints);

            if (bestReverse)
            {
                for (var k = segEnd; k >= segStart; k--)
                {
                    output[outputCount++] = input[k];
                }
            }
            else
            {
                for (var k = segStart; k <= segEnd; k++)
                {
                    output[outputCount++] = input[k];
                }
            }

            currentX = input[segEnd].X;
            currentY = input[segEnd].Y;
        }

        ApplyCornerDwellingToOutput(output, ref outputCount, cornerAngleThreshold, cornerAnchorCount);
    }

    private void FindSegments(
        LaserPoint[] input,
        System.Collections.Generic.List<int> startIndices,
        System.Collections.Generic.List<int> endIndices)
    {
        startIndices.Clear();
        endIndices.Clear();

        if (input.Length == 0)
            return;

        var segmentStart = 0;
        var isBlanked = input[0].I == 0;

        for (var i = 1; i < input.Length; i++)
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

        if (!isBlanked && segmentStart < input.Length)
        {
            startIndices.Add(segmentStart);
            endIndices.Add(input.Length - 1);
        }
    }

    private void ApplyCornerDwellingAndBlanking(
        LaserPoint[] input,
        LaserPoint[] output,
        float maxJumpDistance,
        int blankingDelayPoints,
        float cornerAngleThreshold,
        int cornerAnchorCount,
        out int outputCount)
    {
        outputCount = 0;

        if (input.Length == 0)
            return;

        var currentX = 0;
        var currentY = 0;

        for (var i = 0; i < input.Length; i++)
        {
            InsertBlankingIfNeeded(output, ref outputCount, currentX, currentY, input[i].X, input[i].Y, maxJumpDistance, blankingDelayPoints);

            output[outputCount++] = input[i];
            currentX = input[i].X;
            currentY = input[i].Y;

            if (i < input.Length - 1 && input[i].I > 0 && input[i + 1].I > 0)
            {
                if (i < input.Length - 2)
                {
                    var angle = CalculateAngle(input[i], input[i + 1], input[i + 2]);
                    if (angle < cornerAngleThreshold)
                    {
                        for (var a = 1; a < cornerAnchorCount; a++)
                        {
                            if (outputCount < output.Length)
                            {
                                output[outputCount++] = input[i + 1];
                            }
                        }
                    }
                }
            }
        }
    }

    private void ApplyCornerDwellingToOutput(
        LaserPoint[] output,
        ref int outputCount,
        float cornerAngleThreshold,
        int cornerAnchorCount)
    {
        if (outputCount < 3)
            return;

        var originalCount = outputCount;
        var tempArray = new LaserPoint[output.Length];

        var writeIndex = 0;
        tempArray[writeIndex++] = output[0];
        tempArray[writeIndex++] = output[1];

        for (var i = 2; i < originalCount; i++)
        {
            tempArray[writeIndex++] = output[i];

            if (i < originalCount - 1 && output[i].I > 0 && output[i + 1].I > 0)
            {
                if (i < originalCount - 2 && writeIndex >= 2)
                {
                    var angle = CalculateAngleInOutput(tempArray, writeIndex - 2, writeIndex - 1, writeIndex);
                    if (angle < cornerAngleThreshold)
                    {
                        for (var a = 1; a < cornerAnchorCount && writeIndex < tempArray.Length; a++)
                        {
                            tempArray[writeIndex++] = tempArray[writeIndex - 1];
                        }
                    }
                }
            }
        }

        Array.Copy(tempArray, output, writeIndex);
        outputCount = writeIndex;
    }

    private static void InsertBlankingIfNeeded(
        LaserPoint[] output,
        ref int outputCount,
        int fromX,
        int fromY,
        int toX,
        int toY,
        float maxJumpDistance,
        int blankingDelayPoints)
    {
        if (outputCount == 0)
            return;

        // Guard against invalid parameters that would cause math errors
        if (maxJumpDistance <= 0 || blankingDelayPoints <= 0)
            return;

        var distance = MathF.Sqrt(CalculateDistanceSquared(fromX, fromY, toX, toY));

        if (distance > maxJumpDistance)
        {
            var blankCount = (int)(distance / maxJumpDistance * blankingDelayPoints);
            blankCount = Math.Clamp(blankCount, 1, blankingDelayPoints);

            for (var b = 0; b < blankCount && outputCount < output.Length; b++)
            {
                output[outputCount++] = LaserPoint.CreateBlanked((short)toX, (short)toY);
            }
        }
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

        var dx1 = points[i2].X - points[i1].X;
        var dy1 = points[i2].Y - points[i1].Y;
        var dx2 = points[i3].X - points[i2].X;
        var dy2 = points[i3].Y - points[i2].Y;

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

    private static float CalculateDistanceSquared(int x1, int y1, int x2, int y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return dx * dx + dy * dy;
    }

    private LaserPoint[] _optimizedPoints = new LaserPoint[0];
    private readonly System.Collections.Generic.List<int> _segmentStartIndices = new();
    private readonly System.Collections.Generic.List<int> _segmentEndIndices = new();
    private bool[] _visited = new bool[256];

    [Input(Guid = "B9BDBEB2-FD13-4814-A00B-50BDD5C356FA")]
    public readonly InputSlot<StructuredList> LaserPoints = new();

    [Input(Guid = "BC05E8B6-E1C4-4877-A940-571FFC190296")]
    public readonly InputSlot<float> MaxJumpDistance = new(5000f);

    [Input(Guid = "BE0B8966-3CC6-4923-B727-68CD6ED68D73")]
    public readonly InputSlot<int> BlankingDelayPoints = new(5);

    [Input(Guid = "FC4C0603-EB70-484E-B80E-E5834A9FDC8E")]
    public readonly InputSlot<float> CornerAngleThreshold = new(30f);

    [Input(Guid = "8FEB10BF-1ABA-406A-810C-B27C1BA7295E")]
    public readonly InputSlot<int> CornerAnchorCount = new(3);

    [Input(Guid = "C0339847-2BB7-40A0-97EC-2E30A5EEFE00")]
    public readonly InputSlot<bool> EnableOptimization = new(true);
}
