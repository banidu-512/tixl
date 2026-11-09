"""Convert Stable Diffusion ONNX models to float16.

Usage:
    python convert-fp16.py <model_dir> [--components unet vae_encoder vae_decoder]

Writes converted models to <model_dir>-fp16\\ alongside the source directory,
preserving both the flat (unet.onnx) and diffusers (unet/model.onnx [+ weights.pb])
layouts. The text encoder is intentionally kept in fp32 for accuracy.

Requires: pip install onnx onnxruntime

Conversion uses the ONNX Runtime transformer (OnnxModel.convert_float_to_float16,
keep_io_types=True - the C# side keeps sending fp32 tensors and ORT inserts the
casts internally). Do NOT use onnxconverter-common's float16 here: its converter
emits duplicate node names and self-loop Cast nodes that ORT >= 1.20 rejects as
invalid models (this is what corrupted the first exported_sd15-fp16 attempt).
"""

import argparse
import shutil
import sys
from pathlib import Path

try:
    import onnx
    from onnxruntime.transformers.onnx_model import OnnxModel
except ImportError:
    sys.exit("Missing dependencies. Install with: pip install onnx onnxruntime")

DEFAULT_COMPONENTS = ["unet", "vae_encoder", "vae_decoder"]


def resolve_model_file(model_dir: Path, component: str) -> Path | None:
    flat = model_dir / f"{component}.onnx"
    if flat.exists():
        return flat
    diffusers = model_dir / component / "model.onnx"
    if diffusers.exists():
        return diffusers
    return None


def repair_converter_artifacts(model) -> None:
    """Heal the artifacts the fp16 converters leave behind (all seen on the
    SD UNet/VAE exports with ORT >= 1.20):

    - identical Cast twins (same op, inputs and outputs inserted twice) -
      ORT rejects the double tensor definition as "Duplicate definition of name";
    - two nodes with the same node name - rejected as
      "two nodes with same node name";
    - self-loop Casts (in[0] == out[0]) - same double-definition rejection.

    All three are mechanical no-ops semantically: casts are pure type
    conversion, node names are cosmetic (edges reference tensor names), and a
    self-loop produces a tensor another node already produces.
    """
    graph = model.model.graph

    # 1) Drop later copies of identical Cast twins.
    seen_casts = set()
    drop = []
    for node in graph.node:
        if node.op_type != "Cast" or not node.output or not node.output[0]:
            continue
        key = (node.op_type, tuple(node.input), tuple(node.output),
               tuple((a.name, a.i) for a in node.attribute))
        if key in seen_casts:
            drop.append(node)
            print(f"  dropping duplicate Cast '{node.name or '<unnamed>'}' -> {node.output[0]}")
        else:
            seen_casts.add(key)
    if drop:
        kept = [n for n in graph.node if not any(n is d for d in drop)]
        del graph.node[:]
        graph.node.extend(kept)

    # 2) Drop self-loop Casts (in[0] == out[0]).
    loops = [n for n in graph.node
             if n.op_type == "Cast" and len(n.input) == 1 and len(n.output) == 1
             and n.input[0] and n.input[0] == n.output[0]]
    if loops:
        for node in loops:
            print(f"  dropping self-loop Cast '{node.name or '<unnamed>'}' ({node.input[0]})")
        kept = [n for n in graph.node if not any(n is d for d in loops)]
        del graph.node[:]
        graph.node.extend(kept)

    # 3) Rename duplicate node names.
    seen_names = set()
    rename_counts = {}
    for node in graph.node:
        if not node.name:
            continue
        if node.name in seen_names:
            idx = rename_counts.get(node.name, 0)
            rename_counts[node.name] = idx + 1
            new_name = f"{node.name}_fix{idx}"
            print(f"  renaming duplicate node name '{node.name}' -> '{new_name}'")
            node.name = new_name
        seen_names.add(node.name)


def convert_model(src: Path, dst: Path) -> None:
    print(f"Converting {src} -> {dst}")
    model = OnnxModel(onnx.load(str(src)))
    model.convert_float_to_float16(keep_io_types=True)
    repair_converter_artifacts(model)
    dst.parent.mkdir(parents=True, exist_ok=True)
    model.save_model_to_file(str(dst))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model_dir", type=Path, help="Source model directory")
    parser.add_argument("--components", nargs="*", default=DEFAULT_COMPONENTS,
                        help=f"Components to convert (default: {DEFAULT_COMPONENTS})")
    parser.add_argument("--copy", nargs="*", default=["text_encoder"],
                        help="Components to copy unchanged (default: text_encoder, kept fp32 for accuracy)")
    args = parser.parse_args()

    src_dir = args.model_dir.resolve()
    if not src_dir.is_dir():
        sys.exit(f"Model directory not found: {src_dir}")

    dst_dir = src_dir.parent / f"{src_dir.name}-fp16"

    for component in args.components:
        src = resolve_model_file(src_dir, component)
        if src is None:
            print(f"Skipping {component}: not found in {src_dir}")
            continue
        dst = dst_dir / src.relative_to(src_dir)
        convert_model(src, dst)

    # Copy components that are intentionally kept fp32 (e.g. text encoder)
    for component in set(args.copy):
        src = resolve_model_file(src_dir, component)
        if src is None:
            print(f"Skipping copy of {component}: not found in {src_dir}")
            continue
        dst = dst_dir / src.relative_to(src_dir)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        external = src.parent / "weights.pb"
        if src.name == "model.onnx" and external.exists():
            shutil.copy2(external, dst.parent / "weights.pb")
        print(f"Copied {src} -> {dst}")

    # Copy tokenizer files so the -fp16 directory is directly selectable
    for tokenizer_candidate in ["tokenizer.json", "vocab.json", "merges.txt"]:
        src_file = src_dir / tokenizer_candidate
        if src_file.exists():
            dst_dir.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src_file, dst_dir / tokenizer_candidate)
    tokenizer_dir = src_dir / "tokenizer"
    if tokenizer_dir.is_dir():
        shutil.copytree(tokenizer_dir, dst_dir / "tokenizer", dirs_exist_ok=True)

    print(f"Done. fp16 models written to: {dst_dir}")


if __name__ == "__main__":
    main()
