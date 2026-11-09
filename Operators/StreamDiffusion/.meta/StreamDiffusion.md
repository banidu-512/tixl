# StreamDiffusion Operator

## Overview
AI image and video generation with ONNX Runtime. Supports text-to-image, image-to-image and continuous video restyling, with Stable Diffusion 1.x / SD-Turbo / SDXL exports and the StreamDiffusion V2 (causal Wan2.1 DiT) export. GPU acceleration uses the CUDA or TensorRT execution provider (ONNX Runtime 1.30, CUDA 13 + cuDNN 9, NVIDIA driver 580+); macOS uses CoreML. Without a usable GPU provider the operator falls back to CPU and says so in its status line.

## Inputs
- **string EnginePath** - Path to the model folder with the ONNX models (see below)
- **int Mode** - Generation mode (Text to Image / Image to Image)
- **string Prompt** - Text description of desired image
- **string NegativePrompt** - Used as the unconditional side when Guidance > 1.0
- **int Seed** - Random seed (-1 = random)
- **int Width** - Output width in pixels (aligned to multiples of 8)
- **int Height** - Output height in pixels (aligned to multiples of 8)
- **float Guidance** - Classifier-free guidance (1.0 = no CFG, fastest; 7-12 typical for SD 1.5)
- **float Strength** - img2img transformation strength (0.0 = keep input, 1.0 = ignore input)
- **int Steps** - Number of denoising steps (1-50)
- **Texture2D? InputImage** - Source image for img2img mode
- **int ModelType** - Informational label; the exported models define the actual model
- **int CudaDevice** - CUDA/TensorRT device index (the dropdown lists the adapters found by the driver)
- **bool TriggerGenerate** - Momentarily true to trigger single generation
- **bool AutoGenerate** - Continuously regenerate on input changes

## Outputs
- **Texture2D Output** - Generated image texture
- **Int2 OutputSize** - Dimensions of generated image
- **float GenerationTime** - Time in seconds for last generation

## Model Files Required
Point `EnginePath` to a folder containing:

| File | Required | Purpose |
|---|---|---|
| `unet.onnx` | yes | Denoising UNet |
| `text_encoder.onnx` | yes | CLIP text encoder |
| `vae_decoder.onnx` | yes | Latent-to-image decoder |
| `vae_encoder.onnx` | img2img only | Image-to-latent encoder |
| `tokenizer.json` (or `vocab.json` + `merges.txt`) | yes | CLIP tokenizer data |

The operator validates the folder and reports exactly which files are missing.

### Exporting models
Export SD 1.5 or SD-Turbo (fp16 recommended) to ONNX with
[diffusers' `StableDiffusionPipeline.to_onnx` / `optimum`]:
```bash
optimum-cli export onnx --model stabilityai/sd-turbo --task stable-diffusion ./sd-turbo-onnx
```
Or from Python:
```python
from diffusers import StableDiffusionPipeline
pipe = StableDiffusionPipeline.from_single_file("sd-turbo.safetensors")
pipe.to_onnx("./sd-turbo-onnx", fp16=True)
```
The single `model.onnx` file produced by some exporters is NOT enough — the operator needs the per-component files listed above. Note: SDXL and FLUX variants use different text encoders and are not supported by this pipeline.

## StreamDiffusion V2 (ModelType = StreamDiffusion V2)

Runs the causal Wan2.1 1.3B video DiT from [StreamDiffusionV2](https://github.com/chenfengxu714/StreamDiffusionV2) as a
pipelined stream-batch: each input frame advances every denoising step at once, and keys/values of past frames are kept in
a GPU-resident cache. Use it with Mode = Stream (Image to Image is routed to the same path; Text to Image is unsupported).

Model folder (produced by `tools/streamdiffusionv2/`, see the README there):

| File | Purpose |
|---|---|
| `dit_step.onnx` (+ `.data`), `dit_manifest.json` | Stateless DiT step; resolution is baked in |
| `cross_kv.onnx`, `kv_zeros.onnx` | Prompt cross-attention K/V and device-side cache allocation |
| `text_encoder.onnx` (+ `.data`), `tokenizer.json` | UMT5-XXL, loaded on the CPU only while the prompt changes |
| `vae_encoder.onnx`, `vae_decoder.onnx` | TAEHV (Wan-compatible tiny VAE) |

Input mapping: `Width`/`Height` are ignored (taken from the export), `Steps` selects 1-4 denoising steps (restarts the
stream), `StreamStrength` is the starting noise level (the pipeline lowers it automatically on fast motion), and a changed
`Seed` restarts the stream. An input frame that is identical to the previous one reuses the last result instead of running
the DiT again, so a frozen source stops consuming the GPU.

Prompts are cached: the encoded UMT5 embeddings are written to `%LOCALAPPDATA%\TiXL\sdv2_prompt_cache` (8 MB per prompt,
32 prompts) and reused across restarts, so a known prompt starts streaming without loading the 11 GB text encoder. A
prompt change is encoded on a background task while the previous prompt keeps producing frames; the status line shows
`encoding new prompt` until the swap lands. The status also reports the last step as `enc / dit / dec` milliseconds.
`FastDecode` decodes one latent instead of a three-latent window: on TensorRT the decode drops from about 11-26 ms to about 10 ms
(and on CUDA from about 70-105 ms to about 28 ms),
with slightly less temporal smoothing. Changing it restarts the stream.

Execution providers: **CUDA** and **TensorRT** are supported. TensorRT builds an engine on first use (several minutes,
cached in `trt_cache` next to the model, with a 1-4 batch profile so changing `Steps` reuses it); TiXL preloads the
TensorRT 10 runtime from the standard NVIDIA install or `TENSORRT_ROOT`, so `PATH` does not need to change, and falls
back to CUDA with a readable reason when it is missing. The TensorRT builder needs transient workspace on top of the
resident sessions; if it cannot get it, the build fails and the operator reports that once and re-initializes the model
on CUDA, so the stream recovers instead of erroring on every frame. The workspace cap defaults to 4096 MB and can be
changed with `TIXL_SD_TRT_WORKSPACE_MB`. **DirectML is not available**: ONNX Runtime stopped publishing DirectML
build (and its DML provider fails the DiT/VAE 5D reshapes anyway), so selecting it logs that the runtime ships no
DirectML provider and continues on CUDA or CPU. **CPU** runs but is far too slow for streaming. GPU memory scales with
resolution and step count.

Fastest configuration: TensorRT, `FastDecode` on, `Steps` 1, and the fp16 256x256 export. That is roughly 10x the
throughput of the default CUDA path at the same export; use `Steps` 2 when the temporal context matters more than rate.

`InputImage` accepts RGBA images and float render targets. Float textures hold linear color, so their values are encoded to
sRGB on readback before they reach the model, which keeps a render chain (for example through `AdjustColors`) looking the
same as an image loader.

`Steps` is also the batch size of the pipelined stream, so every extra step doubles the GPU-resident KV cache. On a 16 GB
GPU the 256x256 export runs all 4 steps, while the 512x512 export fits 1 step; more steps at 512 need a larger GPU. When
the cache does not fit, inference slows to a crawl rather than reporting an error, so pick the resolution and step count
for the card.

Measured on a 16 GB RTX 3080 Ti Laptop GPU with the fp16 256x256 export (ONNX Runtime 1.30, CUDA 13.3, TensorRT
10.16): after warm-up a frame takes about 0.31-0.35 s on CUDA with Steps=1 (encode 35 ms, DiT 205 ms, decode
70-105 ms), or about 0.25-0.27 s with `FastDecode` (decode ~28 ms). With TensorRT the same frame is about
0.17-0.19 s, and about 0.12-0.14 s with `FastDecode` - the fastest configuration. Steps=2 runs at about the same
rate and doubles the temporal context; Steps=4 slows to about 2.8 s because the 4x KV cache leaves little VRAM
headroom on 16 GB.
The first frame is about 1.4 s when the prompt is already in the disk cache, and 40-60 s when the 11 GB text encoder
has to load and encode. Open the composition once before a show so the prompt cache and CUDA context are warm; later
prompt changes encode in the background while the stream keeps running.

A process binds exactly one ONNX Runtime native build, shared with Core and every other ONNX operator package. Core
pins the GPU build (`Microsoft.ML.OnnxRuntime.Gpu` 1.30.0) and this operator matches that pin, so CUDA, TensorRT and
CPU are all available in the same process; a package that shipped a conflicting native would make the managed
assembly load the wrong one, which shows up as "Unable to find an entry point ... in DLL 'onnxruntime'" in the log and
a CPU status line.

## Prerequisites
- NVIDIA GPU supported by CUDA 13 (Pascal and older were dropped) with driver 580 or newer
- CUDA 13 runtime and cuDNN 9 installed, or their DLLs in a `CudaRuntime` folder next to the editor/operator
- TensorRT 10 is optional; it is picked up from the NVIDIA install or `TENSORRT_ROOT`
- macOS: CoreML via the CPU build
- Without a GPU provider the operator falls back to CPU automatically and says so
- ONNX Runtime is delivered via NuGet (`Microsoft.ML.OnnxRuntime.Gpu` 1.30.0) — no native build step

## Recommended Settings
| Model | Steps | Guidance | Resolution |
|-------|-------|----------|------------|
| SD Turbo | 1 | 1.0 (no CFG) | 512x512 |
| SD 1.5 | 20-50 | 7.5-12.0 | 512x512 |

## Troubleshooting
- **"Model path is empty"**: Set `EnginePath` to the model folder
- **"Model directory not found" / "Missing in ..."**: The folder must contain the required .onnx files listed above
- **"img2img requires vae_encoder.onnx"**: Export the VAE encoder too, or use Text to Image mode
- **Slow generation**: Prefer SD-Turbo with 1 step and Guidance 1.0; keep resolution at 512x512; check the status line — "(CPU)" means no GPU provider initialized (read the log line that names the reason)
- **Black output with non-turbo models at 1 step**: SD 1.5 needs 20+ steps and Guidance 7.5+

## Performance Notes
- SD Turbo at 1 step / Guidance 1.0 runs a single UNet pass (fastest path)
- Guidance > 1.0 doubles UNet work (two passes per step for CFG)
- Width/Height are aligned to multiples of 8 for the latent space
- img2img at Strength < 1.0 skips the first denoise steps proportional to (1 - Strength)

## Use Cases
- Text-to-image generation
- Image-to-image style transfer
- Sketch/ photo guided generation
- Creative coding and VJ performances

## Examples

The Examples package ships three example compositions (symbol browser → Examples → Lib/io/ai):

- `StreamDiffusionExample` — text to image with SD Turbo, one prompt and an AutoGenerate toggle.
- `StreamDiffusionExampleImg2ImgRestyle` — LoadImage → StreamDiffusion in Image to Image mode, with Strength and MatchInputSize wired.
- `StreamDiffusionExampleV2Restyle` — LoadImage → AdjustColors → StreamDiffusion V2 in Stream mode; the float render target exercises the worker-side conversion. Set ModelPath to a V2 export and use CUDA or TensorRT.

All three expose `ModelPath` as an input because the ONNX exports are not part of the repository.

## Symbol and UI Files (`StreamDiffusion.t3` / `StreamDiffusion.t3ui`)

These two JSON files are the operator's **symbol** and **UI** definitions. They are read by the T3 editor to build the node graph interface and must stay in sync with the C# `Input`/`Output` slot declarations. Both share the same `FormatVersion` (currently `3`) and the same `Id` (the operator's GUID).

### `StreamDiffusion.t3` — Symbol file

Defines the operator's inputs and outputs, their types, and default values. The editor uses this to wire connections and serialize scenes.

| Field | Location | Meaning |
|---|---|---|
| `FormatVersion` | root | Schema version (`3`). Incremented when the JSON structure changes in a non-backwards-compatible way. |
| `Id` | root | Operator GUID (`9A7B3C8D-4E2F-5A6B-7C8D-9E0F1A2B3C4D`). Must match the `[Guid(...)]` attribute on the C# class. |
| `Inputs` | array | One entry per `InputSlot<T>` field in the C# class. |
| `Inputs[].Id` | per-input | The input's GUID. Must match the `[Input(Guid = "...")]` attribute. The editor uses this to identify connections across sessions. |
| `Inputs[].DefaultValue` | per-input | The default value the editor writes when the input is unconnected. Type depends on the slot: `string`, `int`, `float`, `bool`, or `null` for object/Texture slots. |
| `Outputs` | array | One entry per `OutputSlot<T>` field. Same `Id`/GUID rules as inputs. |
| `Outputs[].Id` | per-output | The output's GUID, matching `[Output(Guid = "...")]`. |
| `Children` | array | Sub-operator children (empty for leaf operators). |
| `Connections` | array | Hard-coded internal connections (empty for this operator). |

**Convention:** Inline comments of the form `/*InputName*/` or `/*OutputName*/` are appended to GUIDs for human readability. They are ignored by the parser but used by tooling and editors for hover labels.

### `StreamDiffusion.t3ui` — UI layout file

Controls how the operator's inputs and outputs appear in the editor node panel: positions, descriptions, widget types, and default values. Only inputs that need special UI treatment (file pickers, dropdowns, sliders with min/max) need entries here; everything else falls back to generic widgets.

| Field | Location | Meaning |
|---|---|---|
| `FormatVersion` | root | Same as `.t3` (`3`). |
| `Id` | root | Same GUID as `.t3`. |
| `Description` | root | The operator's long description shown in the node library and as a tooltip. Supports `\n` for newlines. |
| `InputUis` | array | UI overrides for individual inputs, keyed by `InputId`. |
| `InputUis[].InputId` | per-input | Must match the input's GUID from `.t3`. Inline `/*Name*/` comments are allowed. |
| `InputUis[].Position` | per-input | `{X, Y}` in node-local coordinates. Negative Y places the control above the node title; positive Y places it below. |
| `InputUis[].Description` | per-input | Tooltip / hover text for this input in the editor. |
| `InputUis[].Usage` | per-input | Widget override. Common values: `"CustomDropdown"` (shows the options returned by `GetOptionsForInput`), `"FilePath"` (adds a file-browser button; pair with `FileFilter`). Omitting it uses the default widget for the slot's type. |
| `InputUis[].FileFilter` | per-input | For `Usage: "FilePath"` — file extension filter shown in the file dialog (e.g. `"onnx"`, `"txt"`). |
| `InputUis[].DefaultInt` / `DefaultFloat` / `DefaultString` / `DefaultBool` | per-input | Override the default value shown in the editor widget. Must be compatible with the slot's C# type. |
| `InputUis[].Min` / `Max` / `ClampMin` | per-input | Slider range for numeric inputs. `ClampMin: true` prevents the user from dragging below `Min`. |
| `OutputUis` | array | UI overrides for outputs, same structure as `InputUis` but keyed by `OutputId`. |
| `OutputUis[].OutputId` | per-output | Matches the output GUID from `.t3`. |
| `SymbolChildUis` | array | UI definitions for child-symbol slots (empty for this operator). |

### Editing guidelines

- **Never edit `.t3`/`.t3ui` by hand** unless you are adding/removing an input or output, or changing a GUID. The editor auto-adds missing inputs on load (with default positions/descriptions), so for most code-only changes the symbol files do not need to be touched.
- **Adding a new input:** (1) add the `[Input(Guid = "...")]` slot in C#, (2) add a matching entry in `.t3` `Inputs[]` with the same `Id` and a `DefaultValue`, (3) optionally add a `.t3ui` entry for position/description/widget.
- **Changing a GUID:** update it in the C# attribute, the `.t3` entry, and the `.t3ui` entry. The `Id` at the root level never changes for an existing operator.
- **Dropdown options** (`Mode`, `ModelType`, `ResizeMode`, `CudaDevice`) are provided at runtime by the operator's `GetOptionsForInput` implementation; the `.t3ui` only needs `"Usage": "CustomDropdown"` to tell the editor to show a dropdown widget.
- **The `.t3ui` `Description` field** is the operator's node-library blurb; keep it concise (one paragraph + bullet tips) because it appears in search results and hover tooltips.
