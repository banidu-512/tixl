namespace t3.streamdiffusion.Onnx;

public enum SchedulerType { DDIM, Euler, EulerAncestral }

/// <summary>
/// DDIM scheduler (eta = 0, deterministic) with the Stable Diffusion
/// scaled-linear beta schedule. Drives the UNet denoising loop.
/// </summary>
public sealed class StreamScheduler
{
    private readonly float[] _alphasCumprod;
    private readonly SchedulerType _schedulerType;
    private float _eta = 0.0f;  // For EulerAncestral
    private Random _noiseRandom = new();

    /// <summary>Descending inference timesteps, set by <see cref="SetTimesteps"/>.</summary>
    public int[] Timesteps { get; private set; } = Array.Empty<int>();
    public SchedulerType SchedulerType => _schedulerType;

    public int TrainTimestepCount => _alphasCumprod.Length;

    public StreamScheduler(int trainTimesteps = 1000, float betaStart = 0.0001f, float betaEnd = 0.02f, SchedulerType schedulerType = SchedulerType.DDIM)
    {
        if (trainTimesteps <= 1)
            throw new ArgumentOutOfRangeException(nameof(trainTimesteps));

        _schedulerType = schedulerType;

        // "scaled_linear": linear in sqrt space, then squared
        var betas = new float[trainTimesteps];
        var sqrtStart = MathF.Sqrt(betaStart);
        var sqrtEnd = MathF.Sqrt(betaEnd);
        for (var i = 0; i < trainTimesteps; i++)
        {
            var beta = MathF.Pow(sqrtStart + (sqrtEnd - sqrtStart) * i / (trainTimesteps - 1), 2);
            betas[i] = beta;
        }

        _alphasCumprod = new float[trainTimesteps];
        var cumulative = 1.0f;
        for (var i = 0; i < trainTimesteps; i++)
        {
            cumulative *= 1f - betas[i];
            _alphasCumprod[i] = cumulative;
        }
    }

    /// <summary>
    /// Set eta for EulerAncestral scheduler (0 = deterministic, 1 = stochastic).
    /// </summary>
    public void SetEta(float eta)
    {
        _eta = Math.Clamp(eta, 0f, 1f);
    }

    /// <summary>
    /// Evenly spaced descending timesteps. For Turbo/LCM models use linear spacing (Euler),
    /// for SD 1.5 use DDIM spacing.
    /// </summary>
    public void SetTimesteps(int numInferenceSteps)
    {
        if (numInferenceSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(numInferenceSteps));

        if (_schedulerType == SchedulerType.Euler || _schedulerType == SchedulerType.EulerAncestral)
        {
            // linspace(0, T-1, N+1) reversed, dropping the trailing 0 — the ladder must
            // start at max noise so pure-noise latents match what the UNet was trained on,
            // and must work for N=1 (single 999→0 step, the SD-Turbo default)
            Timesteps = new int[numInferenceSteps];
            for (var i = 0; i < numInferenceSteps; i++)
            {
                var t = MathF.Round((numInferenceSteps - i) * (TrainTimestepCount - 1) / (float)numInferenceSteps);
                Timesteps[i] = Math.Clamp((int)t, 0, TrainTimestepCount - 1);
            }
        }
        else
        {
            // DDIM spacing for SD 1.5
            var stepRatio = TrainTimestepCount / numInferenceSteps;
            Timesteps = new int[numInferenceSteps];
            for (var i = 0; i < numInferenceSteps; i++)
            {
                var t = (i + 1) * stepRatio - 1;
                Timesteps[i] = Math.Clamp(t, 0, TrainTimestepCount - 1);
            }

            Array.Reverse(Timesteps);
        }
    }

    /// <summary>
    /// One denoising step using the configured scheduler type.
    /// </summary>
    public void Step(float[] latents, float[] noisePrediction, int stepIndex)
    {
        if (Timesteps.Length == 0)
            throw new InvalidOperationException("Call SetTimesteps before Step");
        if (stepIndex < 0 || stepIndex >= Timesteps.Length)
            throw new ArgumentOutOfRangeException(nameof(stepIndex));
        if (latents.Length != noisePrediction.Length)
            throw new ArgumentException("Latents and noise prediction must have the same length");

        switch (_schedulerType)
        {
            case SchedulerType.Euler:
                StepEuler(latents, noisePrediction, stepIndex);
                break;
            case SchedulerType.EulerAncestral:
                StepEulerAncestral(latents, noisePrediction, stepIndex);
                break;
            default:
                StepDDIM(latents, noisePrediction, stepIndex);
                break;
        }
    }

    private void StepDDIM(float[] latents, float[] noisePrediction, int stepIndex)
    {
        var timestep = Timesteps[stepIndex];
        var prevTimestep = stepIndex < Timesteps.Length - 1 ? Timesteps[stepIndex + 1] : -1;

        GetDDIMScales(timestep, prevTimestep, out var xScale, out var epsScale);
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = xScale * latents[i] + epsScale * noisePrediction[i];
        }
    }

    private void StepEuler(float[] latents, float[] noisePrediction, int stepIndex)
    {
        // Euler step in k-diffusion space, converted back to VP latents:
        // x' = √a' · (x/√a + (σ' − σ) · ε)  — without the √a rescale the latents
        // progressively under-scale and images come out washed out
        var timestep = Timesteps[stepIndex];
        var prevTimestep = stepIndex < Timesteps.Length - 1 ? Timesteps[stepIndex + 1] : 0;

        GetEulerScales(timestep, prevTimestep, out var xScale, out var epsScale);
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = xScale * latents[i] + epsScale * noisePrediction[i];
        }
    }

    private void StepEulerAncestral(float[] latents, float[] noisePrediction, int stepIndex)
    {
        var timestep = Timesteps[stepIndex];
        var prevTimestep = stepIndex < Timesteps.Length - 1 ? Timesteps[stepIndex + 1] : 0;

        // Deterministic Euler down to σ_down, then re-add σ_up of fresh Gaussian noise.
        // σ_down lands below the schedule target by exactly the noise re-added.
        var isLastStep = stepIndex >= Timesteps.Length - 1;
        GetAncestralScales(timestep, prevTimestep, isLastStep, out var xScale, out var epsScale, out var noiseScale);
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = xScale * latents[i] + epsScale * noisePrediction[i];
            if (noiseScale != 0f)
                latents[i] += noiseScale * NextGaussian();
        }
    }

    public float AlphaCumprodAt(int timestep)
    {
        if (timestep < 0 || timestep >= TrainTimestepCount)
            throw new ArgumentOutOfRangeException(nameof(timestep));

        return _alphasCumprod[timestep];
    }

    /// <summary>
    /// k-dimension scales for one Euler step between two timesteps (also the
    /// StepAt formula): x' = xScale·x + epsScale·eps. Shared by the CPU loop
    /// and the GPU-resident latent flow so both stay numerically identical.
    /// </summary>
    public void GetEulerScales(int timestep, int prevTimestep, out float xScale, out float epsScale)
    {
        var sqrtAlpha = MathF.Sqrt(_alphasCumprod[timestep]);
        var sqrtAlphaPrev = MathF.Sqrt(_alphasCumprod[prevTimestep]);
        var sigma = MathF.Sqrt((1f - _alphasCumprod[timestep]) / _alphasCumprod[timestep]);
        var sigmaPrev = MathF.Sqrt((1f - _alphasCumprod[prevTimestep]) / _alphasCumprod[prevTimestep]);
        xScale = sqrtAlphaPrev / sqrtAlpha;
        epsScale = sqrtAlphaPrev * (sigmaPrev - sigma);
    }

    /// <summary>
    /// Scales for one DDIM step (eta = 0): x' = xScale·x + epsScale·eps.
    /// </summary>
    public void GetDDIMScales(int timestep, int prevTimestep, out float xScale, out float epsScale)
    {
        var alphaCumprod = _alphasCumprod[timestep];
        var alphaPrev = prevTimestep >= 0 ? _alphasCumprod[prevTimestep] : 1.0f;

        var sqrtAlphaCumprod = MathF.Sqrt(alphaCumprod);
        var sqrtOneMinusAlphaCumprod = MathF.Sqrt(1f - alphaCumprod);
        var sqrtAlphaPrev = MathF.Sqrt(alphaPrev);
        var sqrtOneMinusAlphaPrev = MathF.Sqrt(1f - alphaPrev);

        xScale = sqrtAlphaPrev / sqrtAlphaCumprod;
        epsScale = sqrtOneMinusAlphaPrev - sqrtAlphaPrev * sqrtOneMinusAlphaCumprod / sqrtAlphaCumprod;
    }

    /// <summary>
    /// Scales for one ancestral Euler step: x' = xScale·x + epsScale·eps +
    /// noiseScale·randn. <paramref name="noiseScale"/> is 0 on the last step or
    /// when eta is 0. The noise itself is drawn via <see cref="CreateAncestralNoise"/>.
    /// </summary>
    public void GetAncestralScales(int timestep, int prevTimestep, bool isLastStep,
        out float xScale, out float epsScale, out float noiseScale)
    {
        var sqrtAlpha = MathF.Sqrt(_alphasCumprod[timestep]);
        var sigma = MathF.Sqrt((1f - _alphasCumprod[timestep]) / _alphasCumprod[timestep]);
        var sigmaPrev = MathF.Sqrt((1f - _alphasCumprod[prevTimestep]) / _alphasCumprod[prevTimestep]);

        var sigmaUp = _eta * MathF.Sqrt(MathF.Max(0f, sigma * sigma - sigmaPrev * sigmaPrev));
        var sigmaDown = MathF.Sqrt(MathF.Max(0f, sigmaPrev * sigmaPrev - sigmaUp * sigmaUp));
        var sqrtAlphaDown = 1f / MathF.Sqrt(1f + sigmaDown * sigmaDown);

        xScale = sqrtAlphaDown / sqrtAlpha;
        epsScale = sqrtAlphaDown * (sigmaDown - sigma);
        noiseScale = !isLastStep && sigmaUp > 0f ? sqrtAlphaDown * sigmaUp : 0f;
    }

    /// <summary>
    /// Draws <paramref name="length"/> standard Gaussians from the seeded
    /// ancestral stream (same generator the CPU ancestral step uses, so the
    /// GPU-resident flow reproduces it element-for-element).
    /// </summary>
    public float[] CreateAncestralNoise(int length)
    {
        var noise = new float[length];
        for (var i = 0; i < noise.Length; i += 2)
        {
            noise[i] = NextGaussian();
            if (i + 1 < noise.Length)
                noise[i + 1] = NextGaussian();
        }
        return noise;
    }

    public void AddNoiseAt(float[] latents, float[] noise, int timestep)
    {
        if (timestep < 0 || timestep >= TrainTimestepCount)
            throw new ArgumentOutOfRangeException(nameof(timestep));
        if (latents.Length != noise.Length)
            throw new ArgumentException("Latents and noise must have the same length");

        var sqrtAlpha = MathF.Sqrt(_alphasCumprod[timestep]);
        var sqrtOneMinus = MathF.Sqrt(1f - _alphasCumprod[timestep]);
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = sqrtAlpha * latents[i] + sqrtOneMinus * noise[i];
        }
    }

    /// <summary>
    /// The highest timestep to start img2img from for a given denoise strength
    /// (1.0 = full denoising from pure noise, 0.0 = no denoising steps).
    /// </summary>
    public int GetImg2ImgStartStepIndex(float strength, int numInferenceSteps)
    {
        var clamped = Math.Clamp(strength, 0f, 1f);
        var startStep = (int)MathF.Round(numInferenceSteps * (1f - clamped));
        return Math.Clamp(startStep, 0, Math.Max(0, numInferenceSteps - 1));
    }

    /// <summary>Timestep window used for streaming residual denoising.</summary>
    public const int StreamWindowLow = 500;
    public const int StreamWindowHigh = 999;

    /// <summary>
    /// Cyclic timestep for streaming mode: wraps around the low-to-mid timestep
    /// window so consecutive frames use ascending (then wrapping) residual noise
    /// strengths. Deterministic and pure.
    /// </summary>
    public int GetCyclicTimestep(int frameIndex)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        var count = StreamWindowHigh - StreamWindowLow + 1;
        return Math.Clamp(StreamWindowLow + (frameIndex % count), 0, TrainTimestepCount - 1);
    }

    /// <summary>
    /// Single deterministic Euler step between two arbitrary timesteps, used by
    /// the streaming mode (which does not run a full SetTimesteps schedule).
    /// </summary>
    public void StepAt(float[] latents, float[] noisePrediction, int timestep, int prevTimestep)
    {
        if (timestep < 0 || timestep >= TrainTimestepCount)
            throw new ArgumentOutOfRangeException(nameof(timestep));
        if (prevTimestep < 0 || prevTimestep >= TrainTimestepCount)
            throw new ArgumentOutOfRangeException(nameof(prevTimestep));
        if (latents.Length != noisePrediction.Length)
            throw new ArgumentException("Latents and noise prediction must have the same length");

        // Same VP↔k-space Euler step as StepEuler, between arbitrary timesteps
        GetEulerScales(timestep, prevTimestep, out var xScale, out var epsScale);
        for (var i = 0; i < latents.Length; i++)
        {
            latents[i] = xScale * latents[i] + epsScale * noisePrediction[i];
        }
    }

    /// <summary>
    /// Seeds the ancestral noise generator. Negative seeds draw from a shared
    /// non-deterministic source. Call once per generation for reproducible output.
    /// </summary>
    public void SetSeed(int seed)
    {
        _noiseRandom = seed >= 0 ? new Random(seed) : Random.Shared;
    }

    private float NextGaussian()
    {
        // Box–Muller; u1 inverted to avoid log(0)
        var u1 = 1.0 - _noiseRandom.NextDouble();
        var u2 = _noiseRandom.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
