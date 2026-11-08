using System.Collections.Generic;
using T3.Core.DataTypes;
using T3.Core.Output;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Regression tests for the hardening pass: counters describe one Apply, public transforms clamp
/// a caller count to its array, and a malformed quad passes through unchanged.
/// </summary>
public class LaserHardeningTests
{
    [Fact]
    public void SafetyApply_ReusedStats_AreResetEachCall()
    {
        var config = LaserSafetyTransforms.Config.Default;
        config.ZoneHalfWidth = 1000;
        config.ZoneHalfHeight = 1000;

        var stats = new LaserSafetyTransforms.Stats();
        var outside = new List<LaserPoint> { new(20000, 0, 65535, 65535, 65535, 65535) };
        LaserSafetyTransforms.Apply(outside, in config, ref stats);
        Assert.True(stats.ClampedPoints > 0);

        var clean = new List<LaserPoint>
                        {
                            new(0, 0, 1000, 1000, 1000, 65535),
                            new(500, 500, 1000, 1000, 1000, 65535),
                        };
        LaserSafetyTransforms.Apply(clean, in config, ref stats);

        Assert.False(stats.AnythingEngaged);
        Assert.Equal(0, stats.ClampedPoints);
    }

    [Fact]
    public void Optimizer_CountBeyondArray_IsClampedInsteadOfThrowing()
    {
        var input = new[]
                        {
                            new LaserPoint(0, 0, 65535, 65535, 65535, 65535),
                            new LaserPoint(1000, 0, 65535, 65535, 65535, 65535),
                        };

        var result = LaserOptimizerTransforms.Apply(input, input.Length + 100, LaserOptimizerTransforms.Config.Default,
                                                    new LaserOptimizerTransforms.Scratch());

        Assert.NotEmpty(result);
        Assert.All(result, p => Assert.InRange(p.X, -32767, 32767));
    }

    [Fact]
    public void FrameMapping_CountBeyondArray_IsClampedInsteadOfThrowing()
    {
        var input = new[] { new LaserPoint(1000, 1000, 65535, 65535, 65535, 65535) };
        var mapped = LaserPipeline.MapFrameIntoZone(input, input.Length + 50, new LaserProcessingSettings.SafetySettings(),
                                                    LaserFrameMappingMode.Stretch, null);

        Assert.NotNull(mapped);
        Assert.Single(mapped!);
    }

    [Fact]
    public void Warp_WithBowTieQuad_ReturnsInputUnchanged()
    {
        // Corners in a crossed (self-intersecting) order: a homography would fold the frame.
        var safety = new LaserProcessingSettings.SafetySettings
                         {
                             WarpCorner1X = -0.5f, WarpCorner1Y = -0.5f,
                             WarpCorner2X = 0.5f, WarpCorner2Y = 0.5f,
                             WarpCorner3X = 0.5f, WarpCorner3Y = -0.5f,
                             WarpCorner4X = -0.5f, WarpCorner4Y = 0.5f,
                         };

        var input = new[] { new LaserPoint(100, 200, 65535, 0, 0, 65535) };
        var mapped = LaserPipeline.WarpFrameIntoQuad(input, input.Length, safety);

        Assert.Same(input, mapped);
    }
}
