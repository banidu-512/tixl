# AI Models

*Settings → AI Models* is where the models of the AI operators live: a store on disk, a browser to download them, a check that tells you what is missing, and the settings of the ONNX engine runtimes.

## AI Model Setup

The quickest way to get the AI operators running is **Help › AI Model Setup…** (also the **Setup wizard…** button on this page). It works like a classic installer:

1. **Welcome** — explains what will be installed, lets you pick the store folder, shows the free space on its drive and whether CUDA/TensorRT were found.
2. **Choose Components** — a **Setup type** preselects models: *Recommended* (the fastest model of every family), *Minimal* (a small depth model and core tracking, no diffusion), *Everything*, or *Custom* as soon as you tick boxes yourself. Each family can be expanded and ticked as a whole. Hovering a model shows its description, speed and quality ratings, hardware needs and the operators that use it. Installed and bundled models are shown ticked and greyed; exports you produce yourself are listed but not downloadable. **Space required** is compared with the free space on the store's drive.
3. **Installing** — downloads the selection one model after the other with overall progress, speed and remaining time. **Run in background** closes the window and keeps downloading; reopening the setup returns to the progress.
4. **Finished** — lists what was installed. Failed downloads keep their partial files, so **Retry failed** resumes them.

On a fresh installation without any downloaded model the setup opens once on its own after startup.

## Model Store

Models download into one subfolder per operator package in the **store folder**, by default `%LOCALAPPDATA%\TiXL\Models`. This keeps the multi-gigabyte models out of the TiXL installation and the git repository — the operators find their models there automatically.

**Open folder** shows the store in the file browser, and the store's total size is shown next to it. Deleting a model's folder is equivalent to removing it on the page.

## Model Browser

The browser lists the models the AI operators can use, filtered by family tabs: **All** shows every entry grouped by family, and **Depth Anything** and **Stream Diffusion** list one family each. The MediaPipe tracking models ship bundled with TiXL and are not listed here. Every entry names what it is used for and which operator needs it, together with its size and whether it is installed.

- **Download** streams the model into the store with a progress bar. Each file is verified against the size the server reports and only then moved into place; a dropped connection is retried and resumed automatically, and a stalled one is detected after 45 seconds, so **Cancel** always works.
- **Remove** deletes an installed model from the store. Operators that need it afterwards report it as missing.
- **Refresh** re-checks which entries are installed, for example after you added a file by hand.

An entry can be marked as the family's default — the one that the one-click setup below installs.

## Setup Check

**Run check** probes the engine runtimes and which models are installed, and reports anything missing with a readable reason, for example that a [DepthAnything] operator shows an error until a depth model is downloaded.

**Download Defaults** installs the fastest working model of every family that is missing one — a small Depth Anything model (the Stream Diffusion export is produced from the downloadable checkpoint with the bundled scripts). Downloads go into the model store, not into the TiXL installation.

## Engine Runtimes

The ONNX operators run on CUDA, TensorRT or DirectML. Their runtimes are detected automatically: the PATH, `TENSORRT_ROOT` and the standard NVIDIA install folders. A folder set on this page wins over all of them:

- **CUDA runtime folder**
- **TensorRT runtime folder** — the folder containing `nvinfer_10.dll`
- **DirectML.dll folder**

The **Prefer DirectML for AI models** switch takes effect on the next start. **Re-detect runtimes** re-probes CUDA and TensorRT after you installed or moved a runtime, without restarting the editor.
