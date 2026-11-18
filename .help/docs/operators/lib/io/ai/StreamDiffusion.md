# StreamDiffusion

*in [Lib.io.ai](README.md)*

Realtime video-to-video diffusion with the StreamDiffusion V2 causal Wan2.1 model on ONNX Runtime. Every frame of the connected video or camera input is restyled with the prompt; the output size comes from the export.

Produce the ONNX export once with the scripts in Operators/StreamDiffusion/tools/streamdiffusionv2; the checkpoint download in the model browser (Settings > AI Models) provides model.pt, the T5 text encoder and the tokenizer.

Tips:
- Connect a video or camera to `InputImage` - the model is video-only and cannot generate from text alone.
- Point `EnginePath` at the V2 export folder, or leave it empty to use the first V2 export in the model store.
- `Steps` is the KV-cache batch size (1-4); every extra step doubles the cache.
- `Strength` is how strongly each frame is re-noised: lower keeps more of the previous frame.
- `FastDecode` decodes one latent instead of a three-latent window: faster, less temporal smoothing.
- `Feedback` blends the previous output frame to smooth flicker.
- Use a CUDA or TensorRT GPU; DirectML and CPU work but are far slower.

AKA: ai, generation, diffusion, onnx, streamdiffusion, streamdiffusion v2, video diffusion, wan, video to video, restyle

## Input Parameters

| Name (Relevancy & Type) | Description |
|---|---|
| **EnginePath** (String) | Folder with the StreamDiffusion V2 export (dit_step.onnx, text_encoder.onnx, vae_decoder.onnx, tokenizer.json, ...). Empty = first V2 export in the model store. |
| **CudaDevice** (Int32) | DirectML device index for multi-GPU systems. Shows available adapters in dropdown. |
| **TriggerGenerate** (Boolean) | Momentarily true to trigger a single generation when AutoGenerate is off. |
| **FastDecode** (Boolean) | V2 only: decode one latent instead of a three-latent window. Saves about 20 ms per frame on TensorRT with slightly less temporal smoothing. |
| **ResetStream** (Boolean) | Momentarily true to drop the rolling KV cache and re-prime the stream from the next input frame - useful on scene cuts or when the style drifts. |
| **Prompt** (String) | Text prompt describing the desired image. Be descriptive for better results. |
| **AutoGenerate** (Boolean) | Continuously regenerate when inputs change. Disable for manual trigger mode. |
| **ExecutionProviderParam** (Int32) | Hardware used for inference. The dropdown lists only the providers this operating system supports: Windows has DirectML, CUDA and TensorRT, Linux has CUDA and TensorRT, macOS has CoreML. A provider that cannot initialize falls back to the next one, ending at CPU. |
| **Strength** (Single) | How strongly each Stream frame is re-noised: lower keeps more of the previous frame, higher follows the input faster. |
| **Steps** (Int32) | The V2 step count is the KV-cache batch size: 1 is realtime, 2-4 add temporal context at twice the cache per step. Changing it restarts the stream. |
| **Debug** (Boolean) | Logs detailed timing and state information. |
| **InputImage** (Texture2D) | Source image for img2img and Stream mode. Ignored in txt2img mode. Accepts RGBA images and float render targets; linear float values are encoded to sRGB before generation. |
| **ResizeMode** (Int32) | How the input image fits the output: Stretch = fill, CenterCrop = crop then fill, Pad = fit with borders. |
| **Seed** (Int32) | Random seed for generation. -1 = random, same value = reproducible output. |

## Outputs

| Name | Type |
|---|---|
| **Output** | T3.Core.DataTypes.Texture2D |
| **OutputSize** | T3.Core.DataTypes.Vector.Int2 |
| **GenerationTime** | System.Single |
| **NoiseScale** | System.Single - effective noise scale after the motion-aware controller |
| **EncodeMs** | System.Single |
| **DenoiseMs** | System.Single |
| **DecodeMs** | System.Single |


