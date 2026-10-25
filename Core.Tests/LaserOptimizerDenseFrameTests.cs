using System;
using System.Diagnostics;
using Xunit;

namespace Core.Tests;

/// <summary>
/// The greedy tour answers "nearest unvisited segment" through a uniform grid once a frame has
/// more than a few hundred lit segments, because the exhaustive scan is quadratic and a mesh
/// wireframe frame has thousands of them (seconds per frame, which the stream watchdog then
/// punishes by blanking the beam). These pin the properties that must survive that switch.
/// </summary>
public class LaserOptimizerDenseFrameTests
{
    /// <summary>Dense, clustered strokes: what a [LaserCamera] mesh capture produces.</summary>
    private static T3.Core.DataTypes.LaserPoint[] BuildDenseFrame(int segmentCount, int pointsPerSegment)
    {
        var points = new System.Collections.Generic.List<T3.Core.DataTypes.LaserPoint>(segmentCount * (pointsPerSegment + 1));
        for (var s = 0; s < segmentCount; s++)
        {
            var x0 = (s % 60) * 500 - 15000;
            var y0 = (s / 60) * 500 - 15000;
            for (var i = 0; i < pointsPerSegment; i++)
                points.Add(new T3.Core.DataTypes.LaserPoint(x0 + i * 7, y0 + i * 3, 65535, 0, 0, 65535));

            points.Add(T3.Core.DataTypes.LaserPoint.CreateBlanked(x0, y0));
        }

        return points.ToArray();
    }

    [Fact]
    public void DenseFrameToursInMilliseconds()
    {
        var frame = BuildDenseFrame(4000, 5);
        var config = T3.Core.DataTypes.LaserOptimizerTransforms.Config.Default;
        var scratch = new T3.Core.DataTypes.LaserOptimizerTransforms.Scratch();

        // Warm up both the grid build and the JIT, then measure a steady-state frame.
        T3.Core.DataTypes.LaserOptimizerTransforms.Apply(frame, frame.Length, config, scratch);

        var sw = Stopwatch.StartNew();
        var optimized = T3.Core.DataTypes.LaserOptimizerTransforms.Apply(frame, frame.Length, config, scratch);
        sw.Stop();

        Assert.NotEmpty(optimized);
        // The exhaustive scan needed ~350 ms for this shape and grew quadratically from there;
        // the budget is deliberately loose so slow CI machines do not make this flaky.
        Assert.True(sw.Elapsed.TotalMilliseconds < 150,
                    $"optimizing a {frame.Length}-point / 4000-segment frame took {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public void DenseTourIsDeterministic()
    {
        var frame = BuildDenseFrame(800, 4);
        var config = T3.Core.DataTypes.LaserOptimizerTransforms.Config.Default;

        var first = T3.Core.DataTypes.LaserOptimizerTransforms.Apply(frame, frame.Length, config,
                                                                     new T3.Core.DataTypes.LaserOptimizerTransforms.Scratch());
        var second = T3.Core.DataTypes.LaserOptimizerTransforms.Apply(frame, frame.Length, config,
                                                                      new T3.Core.DataTypes.LaserOptimizerTransforms.Scratch());

        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
            Assert.Equal(first[i], second[i]);
    }
}
