namespace t3.streamdiffusion.Onnx;

/// <summary>What the operator needs from an image generation backend (Stable Diffusion ONNX or StreamDiffusion V2).</summary>
internal interface IGenerationPipeline : IDisposable
{
    bool IsInitialized { get; }
    bool SupportsImg2Img { get; }
    string ProviderLabel { get; }

    /// <summary>Why the last <c>Initialize</c> failed, for the operator status; null when it succeeded.</summary>
    string? InitializationError => null;

    bool VerboseLogging { get; set; }
    int RecommendedSteps { get; }
    float RecommendedGuidance { get; }

    /// <summary>True when the exported models bake in the output resolution; the operator then ignores its Width/Height inputs.</summary>
    bool TryGetFixedOutputSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        return false;
    }

    /// <summary>Called before each stream step with the user-selected denoising step count.</summary>
    void ConfigureStream(int steps)
    {
    }

    byte[]? Txt2Img(string prompt, string? negativePrompt, int width, int height, int steps, float guidance, int seed, out string? error);

    byte[]? Img2Img(string prompt, string? negativePrompt, byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
                    int steps, float guidance, float strength, int seed, int resizeMode, float preserveDetails, bool isBgra, out string? error);

    byte[]? StreamStep(string prompt, byte[] rgbaInput, int inputWidth, int inputHeight, int width, int height,
                       float strength, int resizeMode, int seed, bool isBgra, out string? error);
}
