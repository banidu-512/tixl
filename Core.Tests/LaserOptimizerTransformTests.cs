using T3.Core.DataTypes;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Tests for the laser optimizer transform (Core/DataTypes/LaserOptimizerTransforms.cs) — the
/// algorithm lifted 1:1 from the [LaserOptimizer] operator and shared with the output-setup
/// pipeline. These pin the behavior the pipeline relies on: segment reordering with reversal,
/// distance-scaled blanked jumps, corner dwelling, and the pass-through mode.
/// </summary>
public class LaserOptimizerTransformTests
{
    private static LaserPoint Lit(int x, int y) => new(x, y, 65535, 65535, 65535);

    private static LaserPoint Blank(int x, int y) => LaserPoint.CreateBlanked(x, y);

    private static LaserOptimizerTransforms.Scratch _scratch = new();

    private static LaserOptimizerTransforms.Config Config(bool optimize = true, float maxJump = 5000f,
                                                          int blanking = 5, float cornerAngle = 30f, int anchors = 3)
        => new()
               {
                   EnableOptimization = optimize,
                   MaxJumpDistance = maxJump,
                   BlankingDelayPoints = blanking,
                   CornerAngleThreshold = cornerAngle,
                   CornerAnchorCount = anchors,
               };

    [Fact]
    public void Apply_EmptyInput_ReturnsEmpty()
    {
        var result = LaserOptimizerTransforms.Apply([], 0, Config(), _scratch);
        Assert.Empty(result);
    }

    [Fact]
    public void Apply_ReordersSegmentsIntoNearestNeighbourTour()
    {
        // Three small shapes ordered far-to-near from the origin: the tour should visit the
        // closest first. Each shape is a single lit point with blanks between.
        var input = new[]
                        {
                            Blank(0, 0),
                            Lit(9000, 0), Blank(9000, 0),      // far
                            Lit(3000, 0), Blank(3000, 0),       // near
                            Lit(6000, 0), Blank(6000, 0),       // middle
                        };

        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(), _scratch);

        // First lit point must be the nearest one (3000, 0), not the input's first (9000, 0).
        var firstLit = Array.Find(result, p => p.I > 0);
        Assert.NotNull(firstLit);
        Assert.Equal(3000, firstLit!.X);
    }

    [Fact]
    public void Apply_ReversesSegmentWhenCloserFromTheOtherEnd()
    {
        // A line segment whose END is nearer the origin than its start: the optimizer emits it
        // reversed (end first) to shorten the jump.
        var input = new[]
                        {
                            Lit(10000, 0), Lit(9000, 0), Lit(8000, 0), Blank(0, 0),
                        };

        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(), _scratch);
        var firstLit = Array.Find(result, p => p.I > 0);
        Assert.NotNull(firstLit);
        Assert.Equal(8000, firstLit!.X);
    }

    [Fact]
    public void Apply_InsertsBlankedJumpsBetweenShapes()
    {
        // Two single-point shapes (separated by a blank so they are two segments): the move
        // between them must be blanked, never lit.
        var input = new[]
                        {
                            Lit(0, 0),
                            Blank(0, 0),
                            Lit(20000, 0),
                        };

        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(maxJump: 5000, blanking: 4), _scratch);

        // Between the two lit points there must be blanked points at the target location.
        var litIndices = new List<int>();
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i].I > 0)
                litIndices.Add(i);
        }

        Assert.Equal(2, litIndices.Count);
        var between = result[(litIndices[0] + 1)..litIndices[1]];
        Assert.NotEmpty(between);
        Assert.All(between, p => Assert.Equal(0, p.I));
        Assert.All(between, p => Assert.Equal(20000, p.X));
    }

    [Fact]
    public void Apply_NoReordering_KeepsOriginalOrder()
    {
        var input = new[]
                        {
                            Blank(0, 0),
                            Lit(9000, 0), Blank(9000, 0),
                            Lit(3000, 0), Blank(3000, 0),
                        };

        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(optimize: false), _scratch);
        var firstLit = Array.Find(result, p => p.I > 0);
        Assert.NotNull(firstLit);
        Assert.Equal(9000, firstLit!.X);
    }

    [Fact]
    public void Apply_SharpCornerGetsDwellAnchors()
    {
        // A V shape (sharp corner at the middle point) with an angle threshold high enough to
        // trigger dwelling: the corner point repeats.
        var input = new[]
                        {
                            Lit(0, 0), Lit(1000, 1000), Lit(2000, 0),
                        };

        var anchors = 3;
        var result = LaserOptimizerTransforms.Apply(input, input.Length,
                                                    Config(optimize: false, cornerAngle: 170f, anchors: anchors), _scratch);
        var cornerCount = 0;
        foreach (var p in result)
        {
            if (p.I > 0 && p.X == 1000)
                cornerCount++;
        }

        // The corner point appears once plus (anchors - 1) repeats.
        Assert.Equal(anchors, cornerCount);
    }

    [Fact]
    public void Apply_BlankedOnlyInput_CollapsesToSinglePoint()
    {
        // No lit runs: FindSegments yields nothing and the tour emits the first point only.
        var input = new[] { Blank(100, 100), Blank(200, 200), Blank(300, 300) };
        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(), _scratch);
        Assert.Single(result);
        Assert.Equal(0, result[0].I);
    }

    [Fact]
    public void Apply_BoundedOutput_NeverExceedsCap()
    {
        // Many scattered lit points with generous blanking: the result must stay within the
        // hard cap rather than overrunning the scratch buffer.
        var input = new LaserPoint[2000];
        for (var i = 0; i < input.Length; i++)
            input[i] = Lit((i * 977) % 65536 - 32768, (i * 1231) % 65536 - 32768);

        var result = LaserOptimizerTransforms.Apply(input, input.Length, Config(maxJump: 500, blanking: 100, anchors: 10), _scratch);
        Assert.True(result.Length <= LaserOptimizerTransforms.MaxOutputPoints);
    }
}
