# AI Models

*Settings → AI Models* is where the models of the AI operators live: a store on disk, a browser to download them, a check that tells you what is missing, and the settings of the ONNX engine runtimes.

## Model Store

Models download into one subfolder per operator package in the **store folder**, by default `%LOCALAPPDATA%\TiXL\Models`. This keeps the multi-gigabyte models out of the TiXL installation and the git repository — the operators find their models there automatically.

**Open folder** shows the store in the file browser, and the store's total size is shown next to it. Deleting a model's folder is equivalent to removing it on the page.

## Model Browser

The browser lists the models the AI operators can use, filtered by family tabs: **All** shows every entry grouped by family, and **Depth Anything**, **Stream Diffusion** and **MediaPipe** list one family each. Every entry names what it is used for and which operator needs it, together with its size and whether it is installed.

- **Download** streams the model into the store, shows a progress bar and verifies the size. A failed download can be cancelled and retried.
- **Remove** deletes an installed model from the store. Operators that need it afterwards report it as missing.
- **Refresh** re-checks which entries are installed, for example after you added a file by hand.

An entry can be marked as the family's default — the one that the one-click setup below installs.

## Setup Check

**Run check** probes the engine runtimes and which models are installed, and reports anything missing with a readable reason, for example that a [DepthAnything] operator shows an error until a depth model is downloaded.

**Download Defaults** installs the fastest working model of every family that is missing one — a small Depth Anything model, a usable Stream Diffusion export and the MediaPipe tracking models. Downloads go into the model store, not into the TiXL installation.

## Engine Runtimes

The ONNX operators run on CUDA, TensorRT or DirectML. Their runtimes are detected automatically: the PATH, `TENSORRT_ROOT` and the standard NVIDIA install folders. A folder set on this page wins over all of them:

- **CUDA runtime folder**
- **TensorRT runtime folder** — the folder containing `nvinfer_10.dll`
- **DirectML.dll folder**

The **Prefer DirectML for AI models** switch takes effect on the next start. **Re-detect runtimes** re-probes CUDA and TensorRT after you installed or moved a runtime, without restarting the editor.
