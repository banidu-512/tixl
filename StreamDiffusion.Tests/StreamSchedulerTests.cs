using t3.streamdiffusion.Onnx;
using Xunit;

namespace StreamDiffusion.Tests;

public class StreamSchedulerTests
{
    [Fact]
    public void SetTimesteps_ProducesDescendingTimesteps()
    {
        var scheduler = new StreamScheduler();
        scheduler.SetTimesteps(50);

        Assert.Equal(50, scheduler.Timesteps.Length);

        for (var i = 1; i < scheduler.Timesteps.Length; i++)
        {
            Assert.True(scheduler.Timesteps[i - 1] > scheduler.Timesteps[i],
                $"Timesteps must strictly descend: [{i - 1}]={scheduler.Timesteps[i - 1]} >= [{i}]={scheduler.Timesteps[i]}");
        }

        Assert.Equal(999, scheduler.Timesteps[0]);
        Assert.All(scheduler.Timesteps, t => Assert.InRange(t, 0, 999));
    }

    [Fact]
    public void SetTimesteps_SingleStep_CoversFullRange()
    {
        var scheduler = new StreamScheduler();
        scheduler.SetTimesteps(1);

        Assert.Single(scheduler.Timesteps);
        Assert.Equal(999, scheduler.Timesteps[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SetTimesteps_InvalidCount_Throws(int steps)
    {
        var scheduler = new StreamScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SetTimesteps(steps));
    }

    [Fact]
    public void AlphaCumprod_IsMonotonicallyDecreasing()
    {
        var scheduler = new StreamScheduler();

        Assert.True(scheduler.AlphaCumprodAt(0) > scheduler.AlphaCumprodAt(500));
        Assert.True(scheduler.AlphaCumprodAt(500) > scheduler.AlphaCumprodAt(999));
        Assert.True(scheduler.AlphaCumprodAt(0) <= 1.0f);
        Assert.True(scheduler.AlphaCumprodAt(999) > 0.0f);

        for (var t = 1; t < 1000; t += 97)
        {
            Assert.True(scheduler.AlphaCumprodAt(t - 1) >= scheduler.AlphaCumprodAt(t));
        }
    }

    [Fact]
    public void Step_IsDeterministic()
    {
        var scheduler = new StreamScheduler();
        scheduler.SetTimesteps(10);

        var latentsA = new float[] { 0.5f, -1.2f, 2.0f, 0.1f };
        var latentsB = new float[] { 0.5f, -1.2f, 2.0f, 0.1f };
        var eps = new float[] { 0.3f, 0.3f, -0.7f, 1.1f };

        scheduler.Step(latentsA, eps, 0);
        scheduler.Step(latentsB, eps, 0);

        Assert.Equal(latentsA, latentsB);
    }

    [Fact]
    public void Step_WithZeroNoise_PredictsCleanSampleDirection()
    {
        var scheduler = new StreamScheduler();
        scheduler.SetTimesteps(2); // timesteps: [999, 499]

        var latents = new float[] { 1.0f, -1.0f };
        var zeroEps = new float[] { 0f, 0f };

        // Final step (index 1): alphaPrev = 1, so x' = x0 = x / sqrt(ac_t)
        scheduler.Step(latents, zeroEps, 1);

        var sqrtAlpha = MathF.Sqrt(scheduler.AlphaCumprodAt(499));
        Assert.Equal(1.0f / sqrtAlpha, latents[0], 4);
        Assert.Equal(-1.0f / sqrtAlpha, latents[1], 4);
    }

    [Fact]
    public void Step_WithoutSetTimesteps_Throws()
    {
        var scheduler = new StreamScheduler();
        Assert.Throws<InvalidOperationException>(() => scheduler.Step(new float[1], new float[1], 0));
    }

    [Fact]
    public void Step_MismatchedLengths_Throws()
    {
        var scheduler = new StreamScheduler();
        scheduler.SetTimesteps(2);
        Assert.Throws<ArgumentException>(() => scheduler.Step(new float[4], new float[3], 0));
    }

    [Theory]
    [InlineData(1.0f, 20, 0)]
    [InlineData(0.5f, 20, 10)]
    [InlineData(0.0f, 20, 19)] // clamped to the last step
    public void GetImg2ImgStartStepIndex_MapsStrengthToStartStep(float strength, int steps, int expected)
    {
        var scheduler = new StreamScheduler();
        Assert.Equal(expected, scheduler.GetImg2ImgStartStepIndex(strength, steps));
    }

    [Fact]
    public void Constructor_ScaledLinearSchedule_MatchesSdBetaBounds()
    {
        var scheduler = new StreamScheduler(1000, 0.0001f, 0.02f);

        // First alpha_cumprod must be slightly below 1 (first beta is ~1e-4)
        Assert.InRange(scheduler.AlphaCumprodAt(0), 0.9998f, 1.0f);
        // By the end the signal is almost fully decayed
        Assert.True(scheduler.AlphaCumprodAt(999) < 0.01f);
    }

    [Fact]
    public void GetCyclicTimestep_StaysInStreamWindow()
    {
        var scheduler = new StreamScheduler();

        for (var frame = 0; frame < 2000; frame += 37)
        {
            Assert.InRange(scheduler.GetCyclicTimestep(frame),
                StreamScheduler.StreamWindowLow, StreamScheduler.StreamWindowHigh);
        }
    }

    [Fact]
    public void GetCyclicTimestep_CyclesAndIsDeterministic()
    {
        var scheduler = new StreamScheduler();
        var count = StreamScheduler.StreamWindowHigh - StreamScheduler.StreamWindowLow + 1;

        // Deterministic: same frame index yields same timestep
        Assert.Equal(scheduler.GetCyclicTimestep(5), scheduler.GetCyclicTimestep(5));

        // Cycles: frame and frame + window size alias to the same timestep
        Assert.Equal(scheduler.GetCyclicTimestep(3), scheduler.GetCyclicTimestep(3 + count));

        // Ascending within a window period, then wraps
        Assert.Equal(StreamScheduler.StreamWindowLow, scheduler.GetCyclicTimestep(0));
        Assert.Equal(StreamScheduler.StreamWindowLow + 1, scheduler.GetCyclicTimestep(1));
        Assert.Equal(StreamScheduler.StreamWindowHigh, scheduler.GetCyclicTimestep(count - 1));
        Assert.Equal(StreamScheduler.StreamWindowLow, scheduler.GetCyclicTimestep(count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GetCyclicTimestep_NegativeFrame_Throws(int frameIndex)
    {
        var scheduler = new StreamScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.GetCyclicTimestep(frameIndex));
    }

    [Fact]
    public void StepAt_DenoisesTowardLowerTimestep()
    {
        var scheduler = new StreamScheduler();

        var latents = new float[] { 1.0f, -1.0f };
        var eps = new float[] { 0.5f, 0.5f };

        // Zero-noise prediction: model claims the latent is pure signal, so the step
        // only rescales by √(a_prev/a_t) — x' = √a'·(x/√a)
        var sqrtAlpha800 = MathF.Sqrt(scheduler.AlphaCumprodAt(800));
        var sqrtAlpha700 = MathF.Sqrt(scheduler.AlphaCumprodAt(700));
        var rescale = sqrtAlpha700 / sqrtAlpha800;

        var stepped = new float[] { 1.0f, -1.0f };
        scheduler.StepAt(stepped, new float[] { 0f, 0f }, 800, 700);
        Assert.Equal(rescale, stepped[0], 4);
        Assert.Equal(-rescale, stepped[1], 4);

        // Non-zero eps: x' = √a'·(x/√a + (σ'−σ)·ε)
        var sigma800 = MathF.Sqrt((1f - scheduler.AlphaCumprodAt(800)) / scheduler.AlphaCumprodAt(800));
        var sigma700 = MathF.Sqrt((1f - scheduler.AlphaCumprodAt(700)) / scheduler.AlphaCumprodAt(700));
        var epsScale = sqrtAlpha700 * (sigma700 - sigma800);

        var moved = new float[] { 1.0f, -1.0f };
        scheduler.StepAt(moved, new float[] { 0.5f, 0.5f }, 800, 700);
        Assert.Equal(rescale + epsScale * 0.5f, moved[0], 4);
        Assert.Equal(-rescale + epsScale * 0.5f, moved[1], 4);
        Assert.True(epsScale < 0f, "stepping to a lower timestep should scale down the noise direction");

        // Deterministic
        var a = new float[] { 0.3f, 0.7f };
        var b = new float[] { 0.3f, 0.7f };
        scheduler.StepAt(a, eps, 800, 700);
        scheduler.StepAt(b, eps, 800, 700);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SetTimesteps_EulerLadderStartsAtMaxNoise()
    {
        var scheduler = new StreamScheduler(schedulerType: SchedulerType.EulerAncestral);

        // linspace(0, 999, N+1) reversed, trailing 0 dropped
        scheduler.SetTimesteps(4);
        Assert.Equal(new[] { 999, 749, 500, 250 }, scheduler.Timesteps);

        // Single step must still start at max noise (SD-Turbo 1-step default)
        scheduler.SetTimesteps(1);
        Assert.Equal(new[] { 999 }, scheduler.Timesteps);
    }

    [Fact]
    public void StepEuler_WithPerfectEpsPrediction_RecoversCleanLatents()
    {
        var scheduler = new StreamScheduler(schedulerType: SchedulerType.Euler);
        scheduler.SetTimesteps(2); // [999, 500]

        var x0 = new float[] { 1.0f, -0.5f };
        var eps = new float[] { 0.7f, -0.2f };

        var sqrtAlpha999 = MathF.Sqrt(scheduler.AlphaCumprodAt(999));
        var sqrtOneMinus999 = MathF.Sqrt(1f - scheduler.AlphaCumprodAt(999));
        var latents = new float[2];
        for (var i = 0; i < 2; i++)
        {
            latents[i] = sqrtAlpha999 * x0[i] + sqrtOneMinus999 * eps[i];
        }

        scheduler.Step(latents, eps, 0);
        scheduler.Step(latents, eps, 1);

        // The final step lands at t=0 (σ≈0.011), so allow a small residual
        Assert.InRange(latents[0], x0[0] - 0.01f, x0[0] + 0.01f);
        Assert.InRange(latents[1], x0[1] - 0.01f, x0[1] + 0.01f);
    }

    [Fact]
    public void StepEulerAncestral_SeededNoise_IsReproducible()
    {
        var schedulerA = new StreamScheduler(schedulerType: SchedulerType.EulerAncestral);
        var schedulerB = new StreamScheduler(schedulerType: SchedulerType.EulerAncestral);
        schedulerA.SetEta(1f);
        schedulerB.SetEta(1f);
        schedulerA.SetTimesteps(4);
        schedulerB.SetTimesteps(4);
        schedulerA.SetSeed(42);
        schedulerB.SetSeed(42);

        var latentsA = new float[] { 0.5f, -1.0f, 2.0f };
        var latentsB = new float[] { 0.5f, -1.0f, 2.0f };
        var eps = new float[] { 0.1f, 0.2f, -0.3f };

        schedulerA.Step(latentsA, eps, 0);
        schedulerB.Step(latentsB, eps, 0);

        Assert.Equal(latentsA, latentsB);

        // eta = 0 must reduce to the deterministic Euler step
        var deterministic = new StreamScheduler(schedulerType: SchedulerType.EulerAncestral);
        deterministic.SetTimesteps(4);
        var eulerOnly = new StreamScheduler(schedulerType: SchedulerType.Euler);
        eulerOnly.SetTimesteps(4);

        var ancestralLatents = new float[] { 0.5f, -1.0f, 2.0f };
        var eulerLatents = new float[] { 0.5f, -1.0f, 2.0f };
        deterministic.Step(ancestralLatents, eps, 0);
        eulerOnly.Step(eulerLatents, eps, 0);
        Assert.Equal(eulerLatents, ancestralLatents);
    }

    [Fact]
    public void StepAt_InvalidArguments_Throw()
    {
        var scheduler = new StreamScheduler();

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.StepAt(new float[1], new float[1], -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.StepAt(new float[1], new float[1], 0, 1000));
        Assert.Throws<ArgumentException>(() => scheduler.StepAt(new float[2], new float[1], 500, 400));
    }
}
