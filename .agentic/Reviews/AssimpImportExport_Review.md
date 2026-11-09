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

1. ~~Native crashes can't be caught in-process.~~ Imports run in a child process
   (`AssimpIsolatedImport` + the `AssimpImportHost` launcher built into Lib's output). A native crash
   becomes an error status, a hang is killed on cancel or after 10 minutes, and the child exits as
   soon as the editor goes away (it watches its stdin pipe). The result comes back as a validated
   binary file (`AssimpModelDataSerializer`, fuzzed with truncation and bit flips). Remaining gap:
   the launcher needs a `dotnet` executable with .NET 10+; self-contained release builds without
   one fall back to in-process import, and Metadata says so. Export still runs in-process.
2. A native hang during an in-process fallback import or an export can't be aborted; the render
   thread is never blocked, but the global Assimp lock stays held for that session.
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
