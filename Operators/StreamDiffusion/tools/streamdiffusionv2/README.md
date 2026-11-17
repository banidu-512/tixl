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

Measured on a 16 GB RTX 3080 Ti Laptop GPU, fp16, TAEHV:

| Export | Steps | Measured |
|---|---|---|
| 256x256 (`tokensPerFrame` 256) | 2 | 8 frames in 51 s including model load |
| 256x256 | 4 | 6 frames in 87 s including model load |
| 512x512 (`tokensPerFrame` 1024) | 1 | 4 frames in 72 s including model load |
| 512x512 | 2 | fills the 16 GB card and throughput collapses; not practical |

The step count is the batch size, so every extra step doubles the KV cache. Attention cost grows with the square of
`cacheFrames * tokensPerFrame`, so 512x512 is roughly 16x more expensive per step than 256x256 at the same batch size.
Larger GPUs can run more steps at 512; below 16 GB, stay at 256 for multi-step streaming.

## Execution providers

| Provider | Status | Notes |
|---|---|---|
| CUDA | verified | Fastest warm path; used for all measurements above |
| TensorRT | verified | Builds engines on first use (about 12 min for all five graphs, 2.8 GB in `trt_cache`); later runs reuse them. The DiT gets an explicit 1-4 batch profile, so Steps can change without a rebuild |
| DirectML | unsupported | ONNX Runtime's DML provider fails the DiT and VAE 5D reshapes (`node_view_*`) with an internal error. The operator rejects the provider at load time |
| CPU | works, very slow | Only practical for a one-off test; the 1.3B DiT step takes minutes |

TensorRT needs `nvinfer_10.dll` and friends on `PATH` (the NVIDIA TensorRT install is not added automatically). The
provider cache lives in `trt_cache` next to the model files and can be deleted to force a rebuild.

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
