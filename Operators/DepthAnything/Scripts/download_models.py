#!/usr/bin/env python3
"""
DepthAnything V2 Model Download Script
Downloads the required ONNX models for the DepthAnything operator
"""

import os
import sys
from pathlib import Path
from urllib.request import urlretrieve
from urllib.error import URLError

# Configuration
SCRIPT_DIR = Path(__file__).parent
ASSETS_DIR = SCRIPT_DIR.parent / "Assets"
BASE_URL = "https://huggingface.co/depth-anything/Depth-Anything-V2/resolve/main"

# Models to download. model_fp16.onnx is the real fp16 export - model.onnx is
# fp32 despite the half-size naming in older revisions of this script.
# expected_mb guards against the earlier mislabeled fp32 downloads.
MODELS = [
    {"name": "depth-anything-v2-small-fp16.onnx", "url": "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model_fp16.onnx", "expected_mb": 50, "required": True},
    {"name": "depth-anything-v2-base-fp16.onnx", "url": "https://huggingface.co/onnx-community/depth-anything-v2-base/resolve/main/onnx/model_fp16.onnx", "expected_mb": 194, "required": False},
    {"name": "depth-anything-v2-large-fp16.onnx", "url": "https://huggingface.co/onnx-community/depth-anything-v2-large/resolve/main/onnx/model_fp16.onnx", "expected_mb": 672, "required": False},
]

# Colors for terminal output
class Colors:
    CYAN = '\033[96m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    GRAY = '\033[90m'
    RESET = '\033[0m'

def print_color(color, text):
    """Print colored text if terminal supports it"""
    if sys.stdout.isatty():
        print(f"{color}{text}{Colors.RESET}")
    else:
        print(text)

def download_model(model):
    """Download a single model file"""
    name = model["name"]
    url = model["url"]
    output_path = ASSETS_DIR / name

    if output_path.exists():
        size_mb = output_path.stat().st_size / (1024 * 1024)
        # fp16 files are roughly half their fp32 counterparts - a size far off
        # the expectation means a mislabeled download; replace it
        if abs(size_mb - model["expected_mb"]) / model["expected_mb"] < 0.25:
            print_color(Colors.GRAY, f"[OK] {name} already exists (skipping)")
            return True
        print_color(Colors.YELLOW, f"[REDO] {name} exists with unexpected size {size_mb:.1f} MB "
                                    f"(expected ~{model['expected_mb']} MB) - re-downloading")

    print_color(Colors.YELLOW, f"Downloading: {name} (~{model['expected_mb']} MB)...")

    try:
        urlretrieve(url, output_path)

        # Get file size
        size_mb = output_path.stat().st_size / (1024 * 1024)
        print_color(Colors.GREEN, f"[OK] Downloaded: {name} ({size_mb:.2f} MB)")
        return True
    except (URLError, IOError) as e:
        print_color(Colors.RED, f"[FAIL] Failed to download: {name}")
        print_color(Colors.RED, f"  Error: {e}")

        if not model["required"]:
            print_color(Colors.YELLOW, "  Note: This model is optional. The operator will work without it.")

        # Remove partial download
        if output_path.exists():
            output_path.unlink()

        return False

def main():
    """Main download function"""
    print_color(Colors.CYAN, "=" * 50)
    print_color(Colors.CYAN, "DepthAnything V2 Model Download Script")
    print_color(Colors.CYAN, "=" * 50)
    print()

    # Create Assets directory
    ASSETS_DIR.mkdir(parents=True, exist_ok=True)
    print_color(Colors.GREEN, f"Created Assets directory: {ASSETS_DIR}")
    print_color(Colors.YELLOW, f"Target directory: {ASSETS_DIR}")
    print()

    # Download models
    print_color(Colors.CYAN, "Starting downloads...")
    print()

    success_count = 0
    failed_count = 0

    for model in MODELS:
        if download_model(model):
            success_count += 1
        else:
            failed_count += 1
        print()

    # Summary
    print_color(Colors.CYAN, "=" * 50)
    print_color(Colors.CYAN, "Download Summary")
    print_color(Colors.CYAN, "=" * 50)
    print_color(Colors.GREEN, f"Successful: {success_count}")
    color = Colors.GREEN if failed_count == 0 else Colors.RED
    print_color(color, f"Failed: {failed_count}")
    print()

    # Check required models
    required_models = [m for m in MODELS if m["required"]]
    all_required_present = True

    for model in required_models:
        model_path = ASSETS_DIR / model["name"]
        if not model_path.exists():
            print_color(Colors.RED, f"[FAIL] Missing required model: {model['name']}")
            all_required_present = False

    if all_required_present:
        print_color(Colors.GREEN, "[OK] All required models are present!")
        print()
        print_color(Colors.CYAN, "You can now build and use the DepthAnything operator.")
        print()
        return 0
    else:
        print()
        print_color(Colors.YELLOW, "[WARNING] Some required models are missing.")
        print_color(Colors.YELLOW, "Please check the errors above and try again.")
        print()
        print_color(Colors.CYAN, "Alternative: Download manually from:")
        print_color(Colors.CYAN, "https://huggingface.co/onnx-community")
        print()
        return 1

if __name__ == "__main__":
    sys.exit(main())
