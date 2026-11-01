# StreamDiffusion Operator

## Overview
AI image generation using Stable Diffusion models with ONNX Runtime and DirectML acceleration. Supports text-to-image and image-to-image generation with SD 1.x / SD-Turbo model exports. Works on any Direct3D 12 GPU (NVIDIA, AMD, Intel) — no CUDA or TensorRT required.

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
- **int CudaDevice** - DirectML device (GPU adapter) index
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

## Prerequisites
- Any GPU with Direct3D 12 support (DirectML execution provider)
- Falls back to CPU automatically when DirectML is unavailable
- ONNX Runtime is delivered via NuGet (`Microsoft.ML.OnnxRuntime.DirectML`) — no native build step

## Recommended Settings
| Model | Steps | Guidance | Resolution |
|-------|-------|----------|------------|
| SD Turbo | 1 | 1.0 (no CFG) | 512x512 |
| SD 1.5 | 20-50 | 7.5-12.0 | 512x512 |

## Troubleshooting
- **"Model path is empty"**: Set `EnginePath` to the model folder
- **"Model directory not found" / "Missing in ..."**: The folder must contain the required .onnx files listed above
- **"img2img requires vae_encoder.onnx"**: Export the VAE encoder too, or use Text to Image mode
- **Slow generation**: Prefer SD-Turbo with 1 step and Guidance 1.0; keep resolution at 512x512; check the status line — "(CPU)" means DirectML failed to initialize
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
