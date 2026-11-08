"""Export the TAESD tiny autoencoder (SD 1.5) to ONNX for realtime decode/encode.

The full SD 1.5 VAE costs ~215 ms per frame (encode + decode @512²) in ORT CUDA
on a laptop 3080 Ti - most of the StreamDiffusion frame budget. TAESD does the
same job with 1.2M parameters (~6 ms) at slightly lower fidelity, which is what
the reference StreamDiffusion implementation ships for realtime use.

The exported graphs are drop-in replacements for vae_decoder/vae_encoder with
the pipeline's VaeScalingFactor (0.18215) compensation folded in, so the C#
pipeline needs no changes:

- taesd_decoder.onnx: in `latent_sample` (latents / 0.18215)  -> out `sample` [-1,1]
- taesd_encoder.onnx: in `sample` [-1,1]                      -> out `latent_sample` (latents / 0.18215)

The pipeline prefers taesd_*.onnx when present in the model directory and falls
back to the full VAE otherwise.

Usage:
    python export-taesd-onnx.py <target_dir>   (required - do NOT derive from
    __file__: this package is typically a symlink into the repo, so __file__
    resolves outside the repo's model directories)

Requires: pip install torch diffusers onnx
"""

import sys
from pathlib import Path

import torch

SD_SCALING = 0.18215  # StableDiffusionPipeline.VaeScalingFactor


def main() -> int:
    try:
        from diffusers.models.autoencoders.autoencoder_tiny import AutoencoderTiny
    except ImportError:
        print("Missing diffusers. Install with: pip install torch diffusers onnx")
        return 1

    if len(sys.argv) < 2:
        print("Usage: python export-taesd-onnx.py <target_model_dir>")
        return 1
    target_dir = Path(sys.argv[1]).resolve()

    vae = AutoencoderTiny.from_pretrained("madebyollin/taesd", torch_dtype=torch.float32).eval()

    class DecoderWrapper(torch.nn.Module):
        """Accepts pipeline latents (raw/0.18215), decodes with TAESD scaling."""
        def __init__(self, decoder):
            super().__init__()
            self.decoder = decoder

        def forward(self, latent_sample: torch.Tensor) -> torch.Tensor:
            return self.decoder(latent_sample * SD_SCALING)

    class EncoderWrapper(torch.nn.Module):
        """Produces pipeline latents (raw/0.18215) so the caller's *0.18215 round-trips."""
        def __init__(self, encoder):
            super().__init__()
            self.encoder = encoder

        def forward(self, sample: torch.Tensor) -> torch.Tensor:
            return self.encoder(sample) / SD_SCALING

    target_dir.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        dec = DecoderWrapper(vae.decoder)
        torch.onnx.export(
            dec, torch.randn(1, 4, 64, 64), str(target_dir / "taesd_decoder.onnx"),
            input_names=["latent_sample"], output_names=["sample"],
            dynamic_axes={"latent_sample": {2: "H", 3: "W"}, "sample": {2: "H8", 3: "W8"}},
            opset_version=17, do_constant_folding=True)
        print(f" wrote {target_dir / 'taesd_decoder.onnx'}")

        enc = EncoderWrapper(vae.encoder)
        torch.onnx.export(
            enc, torch.randn(1, 3, 512, 512), str(target_dir / "taesd_encoder.onnx"),
            input_names=["sample"], output_names=["latent_sample"],
            dynamic_axes={"sample": {2: "H", 3: "W"}, "latent_sample": {2: "H8", 3: "W8"}},
            opset_version=17, do_constant_folding=True)
        print(f" wrote {target_dir / 'taesd_encoder.onnx'}")

    # Round-trip sanity: encode+decode of a random image must stay in range and
    # roughly track the original (TAESD is soft, not exact).
    img = torch.rand(1, 3, 512, 512) * 2 - 1
    with torch.no_grad():
        z = enc(img)
        recon = dec(z)
    print(f"roundtrip: latent range [{z.min():.3f}, {z.max():.3f}], "
          f"recon range [{recon.min():.3f}, {recon.max():.3f}], "
          f"mean abs err {(recon - img).abs().mean():.4f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
