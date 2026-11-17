"""Repair ONNX models broken by float16 conversion.

onnxconverter-common's float16 conversion can emit two kinds of corruption
(seen on exported_sd15-fp16/unet/model.onnx with ORT >= 1.20):
  1. two nodes with the same node name - rejected as
     "two nodes with same node name";
  2. leftover self-loop Cast nodes (in[0] == out[0]), which define the same
     output tensor twice - rejected as "Duplicate definition of name".
Both are safe to fix mechanically: node names are cosmetic (graph edges
reference tensor names), and a self-loop Cast has no effect a consumer could
observe - it produces a tensor that is already produced by the real node.

Usage:
    python fix-node-names.py <model.onnx> [more.onnx ...]

Rewrites each file in place (via a temp file next to it).
Requires: pip install onnx
"""

import sys
from pathlib import Path

import onnx


def fix(path: Path) -> None:
    print(f"Loading {path} ...")
    model = onnx.load(str(path))
    stats = {"renamed": 0, "selfloops": 0, "marked": set()}
    # First pass handles renames while collecting nothing; the self-loop drop
    # needs a second pass because we mutate while iterating below.
    graph = model.graph
    seen = set()
    marked = []
    for node in graph.node:
        if node.name:
            if node.name in seen:
                new = f"{node.name}_fix{stats['renamed']}"
                stats["renamed"] += 1
                print(f"  node '{node.name}' -> '{new}'")
                node.name = new
            else:
                seen.add(node.name)
        if (node.op_type == "Cast" and len(node.input) == 1 and len(node.output) == 1
                and node.input[0] and node.input[0] == node.output[0]):
            marked.append(node)
            print(f"  dropping self-loop Cast '{node.name or '<unnamed>'}' "
                  f"({node.input[0]} -> {node.output[0]})")
    if marked:
        kept = [n for n in graph.node if not any(n is m for m in marked)]
        del graph.node[:]
        graph.node.extend(kept)
    if stats["renamed"] == 0 and not marked:
        print("  no issues found - file left untouched")
        return
    tmp = path.with_suffix(path.suffix + ".fixing")
    print(f"  saving ({stats['renamed']} renamed, {len(marked)} dropped) -> {tmp.name}")
    onnx.save(model, str(tmp))
    tmp.replace(path)
    print(f"  done: {path}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for arg in sys.argv[1:]:
        fix(Path(arg))

