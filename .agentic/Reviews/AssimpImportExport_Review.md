# Review: [AssimpImport] / [AssimpExport]

Port of the Windows-era `feat/assimp` operators onto the cross-platform code base, reviewed with
the four lenses of [FEATURE_REVIEW.md](FEATURE_REVIEW.md). Part 1 lists what was wrong in the
original branch and how the port resolves it. Part 2 is the review of the port itself: findings
that were fixed during review, then the open points.

## Part 1: Original branch (`feat/assimp`, 0406448)

### Robustness

1. `AssimpExport.cs` — scenes were exported without a material.
   Problem: the native FBX, Collada, 3DS and X3D exporters crash the process (SIGSEGV) on a scene
   with no material; STL and PLY fail. Reproduced with Assimp 6.0.
   Fix: `AssimpModelExporter.Export` always adds a default material.
2. `AssimpImport.cs` — `AssimpContext.ImportFile` with post-processing.
   Problem: when validation fails during AssimpNet's post-processing step, Assimp has already
   released the scene and the wrapper releases it again; re-importing an Assimp-written binary PLY
   with `ValidateDataStructure` segfaults this way.
   Fix: the importer drives the native API directly (`ImportSnapshots`) and drops its pointer when
   `aiApplyPostProcessing` returns null.
3. `AssimpImport.cs:376` — file parsing in `Resource<T>`'s generator on the render thread.
   Problem: a 100 MB FBX freezes playback and the editor for seconds.
   Fix: parsing and conversion run on a worker; only the GPU upload stays on the render thread.
4. `AssimpImport.cs:121` — mesh node transforms ignored; vertices of all meshes concatenated raw.
   Problem: multi-object files (most FBX/glTF scenes) collapse their objects onto the origin.
   Fix: node hierarchy flattened, transforms baked (Assimp's column-vector matrices transposed).
5. `AssimpImport.cs:447` — `YUpToZUp` / `ZUpToYUp` swapped Y and Z.
   Problem: a swap is a mirror; the model came out inside-out (winding flipped, normals not).
   Fix: both are 90° rotations; the Flip modes mirror and reverse the winding.
6. `AssimpImport.cs:200` — non-triangle faces left uninitialized `Int3` entries with preset None.
   Problem: zero-index triangles in the index buffer (degenerate geometry, wrong `FaceCount`).
   Fix: triangulation always runs; the index buffer only holds validated triangles.
7. `AssimpImport.cs:192` — `Selection = color.X`.
   Problem: vertex color leaked into the selection channel; `ColorRgb` stayed empty.
   Fix: `ColorRgb` carries the vertex color, `Selection = 1`.
8. `Operators/Lib/io/assimp/` — operators outside `Symbols/`.
   Problem: Release packaging only copies `Symbols/**/*.t3`; the operators never shipped.
   Fix: moved to `Symbols/mesh/generate` and `Symbols/io/file`.
9. `AssimpImport.t3` — defaults `Scale = 0`, `Normalize = false`, `SelectedMeshIndex = 0`.
   Problem: a freshly placed operator showed an invisible, single-mesh model.
   Fix: defaults match the C# initializers (1, true, -1).
10. `AssimpExport.cs:548` — direct SharpDX staging buffers and `AppDomain.BaseDirectory` paths.
    Problem: doesn't compile on the Vulkan backend; writes into the (read-only on Linux/macOS)
    install folder.
    Fix: Graphics.Compat readback as in [WriteObj]; folders resolve into the current project.
11. `Core/Core.csproj` — AssimpNetter referenced from Core.
    Problem: widens the dependency for the Editor, the Player and every package.
    Fix: referenced from Lib only.

### Advertised but missing

The commit message advertised `SkeletonBuffers`, `BoneCount`, `AnimationCount` and `AnimationNames`
outputs that were not implemented. The port implements them through the existing skinning chain
(`Setup`, `SkinWeights`, `SkeletonPoints`).

## Part 2: The port

### Fixed during review

1. `AssimpImport.cs` — any settings change cancelled the running load and started a new one.
   Problem: an animated `Scale` re-parsed the file every frame and never produced a result.
   Fix: at most one load per operator; settings that change meanwhile start the next load when it
   completes. The parsed Assimp scenes are cached per file version and preset, so changing
   Scale, Normalize, axis or flip only re-runs the conversion.
2. `AssimpImport.cs` — the status string was formatted in `Update` every frame while loading.
   Fix: formatted on demand in `GetStatusMessage`.
3. `AssimpImport.cs` — a GPU upload failure left outputs pointing at half-updated buffers and was
   retried every frame.
   Fix: outputs are cleared on failure and the failed state is remembered.
4. `AssimpModelExporter.cs` — meshes on the root node.
   Problem: the 3MF exporter silently drops them (empty file).
   Fix: every mesh gets its own child node.
5. `AssimpModelExporter.cs` — point clouds written to STL/3DS/3MF/Collada produced garbage files.
   Fix: rejected with a message naming the formats that store points.
6. `AssimpExport.cs` — `Ply` mapped to binary PLY, which Assimp 6 can't read back.
   Fix: `Ply` writes ASCII; `PlyBinary` is a separate, documented option.
7. Naming — enums renamed to the plural convention (`AssimpPostProcessPresets`,
   `AssimpAxisConversions`, `ExportFormats`).

### Open points

1. ~~Native crashes can't be caught in-process.~~ Imports **and exports** run in a child process.
   Inside TiXL the child is the editor executable itself (`TiXL --assimp-host ...`, via
   `IsolatedAssimpHost` in Core), so self-contained builds need no `dotnet` installation. Outside an
   editor the `AssimpImportHost` launcher built into Lib's output is used with a `dotnet` executable.
   A native crash becomes an error status, a hang is killed on cancel or after 10 minutes, and the
   child exits as soon as the editor goes away (it watches its stdin pipe). Results come back as
   validated binary files (`AssimpModelDataSerializer`, fuzzed with truncation and bit flips);
   exports are serialized to the child the same way and a failed export removes its partial file.
   Only when no host is available at all do callers run in-process, and status/metadata says so.
2. In-process fallback (isolation switched off, or a non-editor host without `dotnet`) still can't
   abort a native hang; the render thread is never blocked, but the global Assimp lock stays held for
   that session. Imports in this mode are also not protected from a native crash. The setting and
   Metadata make the difference visible.
3. ~~Materials and textures are not imported.~~ Implemented: PBR materials with base color,
   emissive, normal and a CPU-baked roughness/metallic/occlusion map; textures decoded on the worker,
   reused across transform-only reloads and released from CPU memory after upload. Not handled:
   UV channel per texture (always UV0), texture transforms, alpha blending modes.
4. Concave N-gons in `Geometry` are fan-triangulated by [GeometryToMesh] (existing limitation of
   `GeometryMeshCompiler`); `MeshData` uses Assimp's proper triangulation.
5. Four influences per vertex (Assimp `LimitBoneWeights`), matching the skinning shader.
6. Export writes no skins, animations or materials.
7. Very large models (millions of vertices) cause one hitch when the finished load is uploaded to
   the GPU on the render thread; the hard limit is 50 M vertices / triangles.
8. Assimp's own Collada exporter writes key times ×1000 (Assimp bug); files from other tools are
   unaffected.
9. GlTF and OBJ exports write sidecars (`.bin`, `.mtl`) named after the main file. The number
   reservation covers the main file, so a sidecar with that name can be replaced; the operator
   documents this in its help text.

### Hardening pass (2026-11-09)

Brought `origin/multi/feat/assimp` onto this branch and closed the remaining robustness gaps:

- **Exports are isolated too.** `AssimpIsolatedImport.Export` serializes the worker snapshot to a
  temp file, runs `--export` in the same child-process host, and deletes a partial output file on
  errors, crashes and timeouts. `[AssimpExport]` has an `IsolateExport` input (default on) and falls
  back to in-process with the status saying so. This matters because the native FBX/3DS/Collada/3MF
  exporters are the same code that can segfault on unusual scenes.
- **The editor hosts the worker itself.** `IsolatedAssimpHost` in Core recognises
  `TiXL --assimp-host ...` at the very first line of the entry point and runs the operation instead of
  booting the UI. `AssimpIsolatedImport` prefers that form whenever it runs inside TiXL and only uses
  the standalone `AssimpImportHost` + `dotnet` elsewhere. Self-contained release builds therefore
  keep process isolation without shipping a runtime; the packaging gap from the branch is closed.
- **The isolation switches reload.** `IsolateImport` is not part of the conversion settings, so
  toggling it used to affect only the next load. It now cancels a running load and reloads
  immediately; both operators report the mode they actually used ("Imported in-process",
  "Exported ... (in-process)") instead of staying silent when isolation was switched off.
- **`TriggerReload` re-reads materials.** In isolated mode the material identity is the model's
  `FileVersion`, which does not change when only a texture next to the model is edited. Reloads now
  never reuse materials, so the documented "reload after changing referenced files" behaviour also
  applies to textures.
- **Input size limit.** `AssimpModelLoader` rejects files over 2 GB before handing them to Assimp,
  which expands its input in memory several times. The 50 M vertex/triangle limit is unchanged but
  now sits behind a cheap, local check instead of an OOM.
- **Texture memory budget.** One import may retain at most 1 GiB of decoded pixels across all
  materials; further textures (and an oversized baked RMO map) are skipped with a warning instead of
  exhausting memory. Image dimensions are read from the header before decoding, so a small file that
  declares huge dimensions can't allocate the image first. Colors, UVs and opacity read from
  materials are sanitized against NaN/infinite values.
- **CPU buffers are released after upload.** `AssimpModelData.DropUploadedBuffers` clears the vertex,
  triangle, point and skin arrays once the GPU buffers and the `Setup` exist. Without it the CPU copy
  stayed alive for the lifetime of the model, roughly doubling its memory footprint. Element counts
  are carried separately and restored by the serializer, so outputs and Metadata stay correct.
- **NaN/infinity sanitizing in conversion.** Vertex positions, tangents, UVs, vertex colors,
  joint rotations, animation key times and values are checked. Invalid values are replaced (zero,
  white, identity) and counted in the warnings instead of propagating into bounds, skinned matrices
  or GPU buffers.
- **Serialization limits.** `AssimpModelDataSerializer` now bounds arrays by **byte** count rather
  than `int.MaxValue / elementSize`, which previously rejected valid >26 M-vertex isolated imports.
  A corrupt result is rejected before allocating (8 GiB total, 4 GiB per array).
- **Thread-safety.** `AssimpNative.Version` is read on the render thread while a worker loads the
  library, so it is volatile. `_pendingLoad` is volatile because the status provider may read it
  off the render thread. The import outcome carries the isolation setting of its request instead of
  re-reading the input when the result arrives.
- **Failure cleanup.** `AssimpModelExporter` deletes its output when anything throws; the export host
  and the isolated export path delete the reserved file on crash/timeout/cancel. The host launcher
  reports assembly-load failures as text with exit code 65 instead of an unhandled exception.

Verification: Release build of Lib with analyzers is clean for the new files; headless host runs
cover probe, isolated import (OBJ → binary result, plain and textured, with sane vertex/triangle
counts), isolated export (OBJ and GLB, quads preserved), re-import of the exported GLB, corrupt-result
rejection and the 2 GB input limit. The editor integration test
`Tests/Editor.IntegrationTests/AssimpImportExportTests` passes on Windows against the Release editor:
isolated and in-process imports agree (280 vertices / 98 triangles / 3 meshes for the Examples glTF),
both exports write numbered files, and no errors are logged; GraphAcceptance/ProtocolBasics stay green
(8/8). The same test passes on **arton-linux** (LMDE 7, .NET 10.0.403): the full Release solution
builds with 0 errors and the editor test finishes in 6 s, so the editor-hosted worker, the Linux
native library and the export readback all work there. The Linux run also caught an invalid `--`
inside an XML comment in `AssimpImportHost.csproj`; fixed. The test passes with the standalone
`AssimpImportHost` folder removed on Windows, proving the editor executable hosts the worker;
`TiXL.exe --assimp-host ... --probe` and `dotnet TiXL.dll --assimp-host ... --probe` both answer.
See `.tests-manual/assimp-import-export.md` for the remaining visual pass.

### Verification

- Lib builds on Linux in Debug and Release (analyzers on) without warnings in the new files.
- Bundled natives for win/linux/osx × x64/arm64 land in `runtimes/<rid>/native` of the package
  and are probed explicitly by `AssimpNative`.
- Converter checked headless against glTF sample models: node transforms, multi-mesh chunks,
  welded N-gon geometry, normals/tangents unit length, valid indices, rest-pose skinning error
  < 1e-6, and animated skinned bounds within 1e-4 of an independent SharpGLTF evaluation, also with
  Normalize + YUpToZUp.
- Round-trips through obj, fbx, glb, gltf, stl, ply, collada, 3mf, x3d for mesh, geometry and points.
- 64 concurrent imports/exports with random settings and cancellation: no crash, corrupt files
  fail cleanly.
- Isolated imports produce results bit-identical to in-process ones; ~100-150 ms process overhead.
  A child that dereferences a bad pointer, one killed with SIGKILL, a cancelled import and a killed
  parent were all checked: the parent keeps running and no child is left behind.
- Not yet run in the editor: see `.tests-manual/assimp-import-export.md`.
