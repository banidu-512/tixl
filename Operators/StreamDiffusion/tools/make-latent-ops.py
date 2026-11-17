"""Generate the tiny latent-math ONNX graphs for the GPU-resident StreamDiffusion flow.

The scheduler math in StableDiffusionPipeline (Euler/DDIM/ancestral steps,
add-noise, stream blending, CFG combine, VAE scale) reduces to two shapes:

    lat_scale.onnx    out = a * k
    lat_combine.onnx  out = a*k1 + b*k2 + c*k3

with per-step scalars (k1..k3, shape [1]) computed on the CPU from the same
alpha-cumprod tables - so numerics match the CPU flow exactly. The pipeline
runs these with IO binding and device-bound outputs so latents stay in GPU
memory across encode -> denoise -> decode.

Usage:
    python make-latent-ops.py <model_dir> [more_dirs...]

Writes lat_scale.onnx / lat_combine.onnx into each directory.
Requires: pip install torch onnx
"""

import sys
from pathlib import Path

import torch


class Scale(torch.nn.Module):
    def forward(self, a: torch.Tensor, k: torch.Tensor) -> torch.Tensor:
        return a * k


class Combine(torch.nn.Module):
    def forward(self, a: torch.Tensor, b: torch.Tensor, c: torch.Tensor,
                k1: torch.Tensor, k2: torch.Tensor, k3: torch.Tensor) -> torch.Tensor:
        return a * k1 + b * k2 + c * k3


def export(target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(
            Scale(), (torch.randn(1, 4, 8, 8), torch.ones(1)),
            str(target / "lat_scale.onnx"),
            input_names=["a", "k"], output_names=["out"],
            dynamic_axes={"a": {2: "H", 3: "W"}, "out": {2: "H", 3: "W"}},
            opset_version=17, do_constant_folding=True)
        torch.onnx.export(
            Combine(), (torch.randn(1, 4, 8, 8), torch.randn(1, 4, 8, 8), torch.randn(1, 4, 8, 8),
                        torch.ones(1), torch.ones(1), torch.zeros(1)),
            str(target / "lat_combine.onnx"),
            input_names=["a", "b", "c", "k1", "k2", "k3"], output_names=["out"],
            dynamic_axes={"a": {2: "H", 3: "W"}, "b": {2: "H", 3: "W"}, "c": {2: "H", 3: "W"},
                          "out": {2: "H", 3: "W"}},
            opset_version=17, do_constant_folding=True)
    print(f"  wrote lat_scale.onnx, lat_combine.onnx -> {target}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for arg in sys.argv[1:]:
        export(Path(arg))
