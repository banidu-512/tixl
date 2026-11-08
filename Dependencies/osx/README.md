# Native libraries for macOS

Dylibs here are copied into the Editor and Player output on macOS (see the `osx` item groups in
`Editor/Editor.csproj` and `Player/Player.csproj`).

`libtixlaudio.dylib` is built from `Native/TixlAudio/` by `build-macos.sh`. It needs Apple's SDK, so it has to run on a
Mac; the macOS CI job does this for every DMG. Without it, audio stays silent on macOS.

## MediaPipe

| File                   | Source |
|------------------------|--------|
| `libmediapipe_c.dylib` | homuler/MediaPipeUnityPlugin v0.16.3 release (`MediaPipeUnityPlugin-all-stripped.zip`, `Runtime/Plugins/libmediapipe_c.dylib`) |

A universal binary (x86_64 + arm64); the arm64 slice is the one TiXL uses. Unlike the Windows/Linux builds
it is CPU-only - the same export set as the Windows `mediapipe_c.dll`, which is what the vendored binding
and the operators run against (the GPU delegate classes exist but the macOS native cannot serve them, so
`Gpu` falls back to CPU). Apache 2.0; licence text in `Dependencies/licenses`.

Two local modifications to the release binary, applied offline:

1. The upstream build links with `-undefined dynamic_lookup`, leaving ~92 OpenCL (`cl*`) imports to be
   resolved from whatever the process has loaded. On Apple Silicon nothing provides OpenCL, so dyld
   refuses the image outright ("symbol not found in flat namespace '_clBuildProgram'"). Marking them weak
   is not enough in flat-namespace mode, so the process must carry the symbols: the checked-in
   `mediapipe-opencl-stub.c` exports no-ops for exactly those 92 symbols and is built into
   `libmediapipe_opencl_stub.dylib` by the `BuildMediapipeOpenClStub` target in `Editor.csproj` /
   `Player.csproj`, the macOS CI and `Installer/macOS/build-dmg.sh`;
   `MediapipeNativeLibrary` loads it before the first `mediapipe_c` P/Invoke. The CPU paths never call any
   of them.
2. The dylib's ad-hoc signature does not survive git round-trips untouched, so the file must be re-signed
   before macOS will load it: `codesign --force --sign - libmediapipe_c.dylib`. The macOS CI workflow and
   the DMG build do this automatically; on a dev machine run it once after checkout.
