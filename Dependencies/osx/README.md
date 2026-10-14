# macOS natives

## MediaPipe

| File                   | Source                                                                    |
|------------------------|---------------------------------------------------------------------------|
| `libmediapipe_c.dylib` | homuler/MediaPipeUnityPlugin v0.16.3 release (`MediaPipeUnityPlugin-all-stripped.zip`, `Runtime/Plugins/libmediapipe_c.dylib`) |

A universal binary (x86_64 + arm64); the arm64 slice is the one TiXL uses. Unlike the Windows/Linux builds
it is CPU-only — exactly the export set the Windows `mediapipe_c.dll` has, which is what the vendored
binding and the mediapipe operators run against (CPU delegate only; the GPU classes in the vendor tree are
never called). Apache 2.0; licence text in `Dependencies/licenses`.

Two local modifications to the release binary, applied offline (see the commit that introduced them):

1. The upstream build links with `-undefined dynamic_lookup`, leaving ~92 OpenCL (`cl*`) imports to be
   resolved from whatever the process has loaded. On Apple Silicon nothing provides OpenCL, so dyld refuses
   the image outright ("symbol not found in flat namespace '_clBuildProgram'"). Those imports are marked as
   weak (`N_WEAK_REF`) so dyld binds them to NULL instead; the CPU paths never call them.
2. Both modifications invalidate the ad-hoc signature, so the file must be re-signed before macOS will load
   it: `codesign --force --sign - libmediapipe_c.dylib`. The macOS CI workflow and the DMG build do this
   automatically; on a dev machine run it once after checkout.

## BASS

Copied into the Editor and Player output on macOS only (see the `osx` item groups in `Editor/Editor.csproj`
and `Player/Player.csproj`). They are universal binaries; the arm64 slice is the one TiXL uses.

| File                | Package (un4seen.com/files/) | Take from the archive |
|---------------------|------------------------------|-----------------------|
| `libbass.dylib`     | `bass24-osx.zip`             | `libbass.dylib`       |
| `libbassmix.dylib`  | `bassmix24-osx.zip`          | `libbassmix.dylib`    |
| `libbassflac.dylib` | `bassflac24-osx.zip`         | `libbassflac.dylib`   |

`ManagedBass` P/Invokes the plain names `bass`, `bassmix` and `bassflac`; .NET maps those to `lib<name>.dylib`
on macOS. As on Linux, there is no BASSWASAPI, so live audio input is unavailable for now.
