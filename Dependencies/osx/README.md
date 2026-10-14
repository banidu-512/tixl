# macOS natives

## MediaPipe

| File                   | Source                                                                    |
|------------------------|---------------------------------------------------------------------------|
| `libmediapipe_c.dylib` | homuler/MediaPipeUnityPlugin v0.16.3 release (`MediaPipeUnityPlugin-all-stripped.zip`, `Runtime/Plugins/libmediapipe_c.dylib`) |

A universal binary (x86_64 + arm64); the arm64 slice is the one TiXL uses. Unlike the Windows/Linux builds
it is CPU-only — exactly the export set the Windows `mediapipe_c.dll` has, which is what the vendored
binding and the mediapipe operators run against (CPU delegate only; the GPU classes in the vendor tree are
never called). Apache 2.0; licence text in `Dependencies/licenses`.

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
