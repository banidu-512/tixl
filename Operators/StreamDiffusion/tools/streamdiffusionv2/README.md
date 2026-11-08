# StreamDiffusion V2 export tools

Converts the [StreamDiffusionV2](https://github.com/chenfengxu714/StreamDiffusionV2) weights (a causal Wan2.1 1.3B video
DiT) into the ONNX folder the `StreamDiffusion` operator loads when `ModelType` is **StreamDiffusion V2**.

The PyTorch modules here are independent re-implementations of the model layout; the upstream repository is only a
reference for the architecture and is never imported or executed.

## Inputs

| File | Source |
|---|---|
| `model.pt` (causal generator) | `jerryfeng/StreamDiffusionV2`, `wan_causal_dmd_v2v/model.pt` |
| `models_t5_umt5-xxl-enc-bf16.pth`, `google/umt5-xxl/tokenizer.json` | `Wan-AI/Wan2.1-T2V-1.3B` |
| `taew2_1.pth` | `https://github.com/madebyollin/taehv` |

## Steps

Run from this folder, then copy everything into one model folder together with `tokenizer.json`.

```bash
python export-dit.py --ckpt model.pt --out out --height 480 --width 832 --verify
python export-text-encoder.py --t5 models_t5_umt5-xxl-enc-bf16.pth --out out --verify
python export-vae.py --taehv taew2_1.pth --out out --verify
```

Resulting folder: `dit_step.onnx(+.data)`, `dit_manifest.json`, `cross_kv.onnx(+.data)`, `kv_zeros.onnx`,
`text_encoder.onnx(+.data)`, `vae_encoder.onnx`, `vae_decoder.onnx`, `tokenizer.json`.

The resolution is baked into `dit_step.onnx`; export once per size (multiples of 16). Memory use grows with the token
count: the GPU-resident KV cache is `layers x denoising steps x 6 frames x tokens` fp16 pairs, plus the same again while a
step runs.

Measured on a 16 GB RTX 3080 Ti Laptop GPU, fp16, TAEHV, CUDA, after warm-up (the operator's own stage timers):

| Export | Steps | Steady state | Notes |
|---|---|---|---|
| 256x256 (`tokensPerFrame` 256) | 1 | ~0.32 s/frame (3 fps) | CUDA: encode ~35 ms, DiT ~170 ms, decode ~110 ms |
| 256x256 | 1 | ~0.12 s/frame | TensorRT: encode ~20 ms, DiT ~55 ms, decode ~45 ms |
| 256x256 | 1 | ~0.095 s/frame | TensorRT + `FastDecode`: decode ~19 ms, less temporal smoothing |
| 256x256 | 2 | ~0.31-0.41 s/frame | CUDA; about the same fps as 1 step and twice the temporal context, 2x KV cache |
| 256x256 | 4 | ~2.8 s/frame | CUDA; the 4x KV cache plus the in-flight copy leaves little VRAM headroom on 16 GB |
| 512x512 (`tokensPerFrame` 1024) | 1 | ~3.3-5 s/frame | CUDA; decode dominates, not realtime on a 16 GB laptop |
| 512x512 | 2 | fills the 16 GB card | throughput collapses; not practical |

The step count is the batch size, so every extra step doubles the KV cache. Attention cost grows with the square of
`cacheFrames * tokensPerFrame`, so 512x512 is roughly 16x more expensive per step than 256x256 at the same batch size.
Larger GPUs can run more steps at 512; below 16 GB, stay at 256 for multi-step streaming.

The RTX 3080 Ti Laptop throttles at 87°C under sustained streaming (clocks oscillate between 1.1 and 1.8 GHz), so long
takes show occasional 2-3x slower frames. It is a cooling limit, not a leak: GPU memory stayed flat at ~7.5 GB over a
100-frame soak and returned to idle after disposal. The operator skips identical input frames, so a frozen source stops
using the GPU entirely; prompt changes encode in the background (the stream keeps the previous prompt) and the encoded
prompt is cached on disk, so a known prompt starts in about 1.4 s instead of loading the 11 GB text encoder (~40-60 s).

## Execution providers

| Provider | Status | Notes |
|---|---|---|
| CUDA | verified | Fastest warm path; used for all measurements above. Needs the CUDA 13 runtime and cuDNN 9 (ONNX Runtime 1.30, driver 580+); CUDA 13 no longer supports Pascal GPUs, which fall back to CPU |
| TensorRT | verified | Builds engines on first use (about 12 min for all five graphs, 2.8 GB in `trt_cache`); later runs reuse them. The DiT gets an explicit 1-4 batch profile, so Steps can change without a rebuild. TensorRT runs alone here: adding CUDA as a per-node fallback made the DiT roughly 6x slower in testing (7 min vs 1 min 12 s for the same frames) |
| DirectML | not available | ONNX Runtime 1.28 stopped publishing a DirectML build, and its DML provider fails the DiT and VAE 5D reshapes (`node_view_*`) with an internal error anyway. Selecting it logs that the runtime ships no DirectML provider and continues on CUDA or CPU |
| CPU | works, very slow | Only practical for a one-off test; the 1.3B DiT step takes minutes |

TiXL finds the TensorRT 10 runtime itself - the standard `NVIDIA GPU Computing Toolkit\TensorRT-*\bin` install,
`TENSORRT_ROOT`, or a `TensorRTRuntime` folder next to the editor - and preloads `nvinfer_10.dll` and the ONNX parser
before ONNX Runtime asks for the provider, so `PATH` does not need to change. If no runtime is found, the operator falls
back to CUDA with a readable reason. The provider cache lives in `trt_cache` next to the model files and can be deleted
to force a rebuild.

## Design notes

- The reference mutates per-layer KV caches in place with data-dependent control flow. Here the DiT step is stateless:
  cache tensors, a per-slot visibility mask and the destination slot go in, the updated caches come out. The C# pipeline
  owns the slot policy (3 sink frames, 3 rotating frames).
- The T5 encoder keeps bf16 weights and computes in fp32: fp16 overflows to NaN in this model.
- TAEHV replaces the Wan 3D VAE for both encode and decode; it runs over a short window of frames per step instead of
  carrying state.

## Known differences from the reference

- Adaptive sink refresh (scene-cut detection) and the RoPE rewind for streams longer than about 1000 latent frames are not
  implemented; the pipeline re-primes instead.
- The first two latent frames are denoised one after another rather than as one two-frame block.
