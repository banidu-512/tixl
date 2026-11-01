# TiXL Linux Dependencies

What TiXL needs on a Linux system, for packagers and for users of the portable tarball.
`ldd` over the staged tarball found the "always present" libraries below. The rest are loaded at
runtime (`dlopen`), which `ldd` can't see.

Package names: Arch, AUR and nixpkgs names were checked against the live repositories (2026-10).
Debian/Ubuntu and Fedora names follow those distros' conventions but haven't been tested yet.

Until distribution packages exist, `install-dependencies.sh` installs the three required components from
this table for Arch, Debian/Ubuntu and Fedora. Run it as your normal user — it calls `sudo` only for the
package manager, so slangc lands in your home rather than root's:

```bash
Installer/Linux/install-dependencies.sh --check   # report only
Installer/Linux/install-dependencies.sh           # ask, then install
```

## Required

At startup the Editor checks for the .NET SDK, the Vulkan loader and slangc, and shows the install command
for the user's distribution when one is missing. It can download slangc itself, since that one needs no
package manager.

| Component | Why | Arch | Debian / Ubuntu | Fedora | nixpkgs |
|---|---|---|---|---|---|
| **.NET 10 SDK** (not just the runtime) | Runs the Editor and compiles operator projects at runtime (`dotnet restore` / `dotnet build`, needs network for NuGet) | `dotnet-sdk` | `dotnet-sdk-10.0` (Ubuntu archive or `ppa:dotnet/backports`; Debian via packages.microsoft.com) | `dotnet-sdk-10.0` | `dotnetCorePackages.sdk_10_0` |
| **Vulkan loader** | Rendering | `vulkan-icd-loader` | `libvulkan1` | `vulkan-loader` | `vulkan-loader` |
| **Vulkan driver** | Rendering | `vulkan-radeon` / `vulkan-intel` / `nvidia-utils` | `mesa-vulkan-drivers` (NVIDIA: proprietary driver) | `mesa-vulkan-drivers` (NVIDIA: proprietary driver) | `hardware.graphics.enable = true;` |
| **slangc** | Shader compilation. Found via `TIXL_SLANGC`, `~/.local/opt/slang-2026.18/bin`, or `PATH`. Tested with 2026.18 | `shader-slang` (extra; `slang` is the unrelated S-Lang) | GitHub release of shader-slang | GitHub release of shader-slang | `shader-slang` |
| **ALSA library** | Audio output (BASS loads it; PipeWire/PulseAudio provide the ALSA plugin) | `alsa-lib` | `libasound2t64` (older: `libasound2`) | `alsa-lib` | `alsa-lib` |
| **ICU, OpenSSL** | .NET globalization and HTTPS | pulled in by the SDK package | pulled in by the SDK package | pulled in by the SDK package | pulled in by the SDK package |
| **X11 or Wayland client libs, libGL/EGL** | Windows (SDL3), startup dialog (GLFW) | present on any desktop | present on any desktop | present on any desktop | present on any desktop |

**Always present** on desktop Linux (found by `ldd`): glibc 2.38 or newer (the prebuilt `libcimgui.so` from
ImGui.NET needs it; everything else needs 2.34), libstdc++,
zlib, fontconfig, freetype, libpng, expat, bzip2, brotli.

A distribution's Slang package follows Slang's releases, not TiXL's pinned line, so the startup check would
report it as the wrong version. A package that deliberately ships its distribution's slangc sets
`TIXL_SLANGC_UNPINNED=1` (next to `TIXL_SLANGC`): the version then goes to the log instead of a dialog, and
the shader cache is keyed on the version in use so blobs from two compilers stay apart. Shaders can still
compile differently — that is what the pin is about — so prefer the pinned line where it is available.

## Optional

| Component | Enables | Arch | Debian / Ubuntu | Fedora | nixpkgs |
|---|---|---|---|---|---|
| NDI runtime (`libndi.so.6`) | Ndi operators and NDI output streams. Found via `NDI_RUNTIME_DIR_V6` or the system library path; source discovery needs `avahi-daemon` | AUR `ndi-sdk` | vendor download (ndi.video) | vendor download | `ndi` (unfree) |
| CUDA 12 runtime + cuDNN 9 (`libcudart.so.12`, `libcublas.so.12`, `libcublasLt.so.12`, `libcurand.so.10`, `libcufft.so.11`, `libcudnn.so.9`) | CUDA execution provider of the DepthAnything and StreamDiffusion operators (NVIDIA driver 525+, Pascal or newer GPU). Found via ld.so, `CUDA_PATH`/`CUDA_HOME`, `/usr/local/cuda-12*` or a `CudaRuntime` folder next to the operator. Without it the operators log the missing library and run on the CPU | NVIDIA packages (`cuda`, `cudnn`) | NVIDIA CUDA repo (`cuda-runtime-12-4`, `cudnn9-cuda-12`) | NVIDIA CUDA repo | `cudaPackages_12` |
| Vulkan validation layers | GPU debugging | `vulkan-validation-layers` | `vulkan-validationlayers` | `vulkan-validation-layers` | `vulkan-validation-layers` |

The CUDA 12 runtime must not be newer than the driver supports: on a GTX 1070 with driver 550 (CUDA 12.4), cuDNN 9.1 with
CUDA 12.4 ran Depth Anything at 16 ms per frame, while CUDA 12.9 with cuDNN 9.27 loaded but failed on the first convolution.
`nvidia-smi` shows the highest CUDA version the driver supports.

## Bundled in the tarball

These ship with TiXL, so packagers don't need to provide them. Distro packages may swap in the system
version where one exists.

| Component | Comes from | License |
|---|---|---|
| .NET runtime (Editor and Player; `--system-dotnet` leaves it out of the Editor) | Microsoft | MIT |
| SDL3 | `ppy.SDL3-CS` NuGet | Zlib |
| SkiaSharp, HarfBuzzSharp | NuGet | MIT / BSD-3-Clause |
| FFmpeg 7 (`libavcodec.so.61`, ...) | Video operator package | LGPL-3.0 |
| ONNX Runtime | NuGet | MIT |
| cimgui, GLFW | NuGet | MIT / Zlib |
| **BASS, BASSmix, BASSFLAC** | un4seen.com (not in git, fetched at build time, see `Dependencies/linux-x64/README.md`) | **Proprietary, free for non-commercial use** |

License texts for everything bundled are in `Dependencies/licenses/`.

## Not available on Linux yet

These are feature gaps, not packaging issues. The affected operators are expected to fail when used.

- **Emgu CV** (`cvextern`): only Windows natives are vendored.
- **Loopback audio capture**: BASS on Linux only enumerates real inputs, so microphones and line inputs
  work but capturing the system's own output doesn't. (WASAPI loopback is Windows-only.)
- **Spout**: Windows-only by design.

## Build dependencies

.NET 10 SDK, `git`, `bash`, `file`, `tar`, `unzip` (for the BASS archives), and network access to
NuGet. See `build-tarball.sh` and `.github/workflows/linux-build.yml`.
