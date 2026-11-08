# NanoVDB and OpenVDB volumes

Sparse volume support for TiXL. Loads and writes `.vdb` files, edits volumes with a CPU tool set, and flattens them into NanoVDB buffers for GPU raymarching. The whole stack is managed C#; it has no native dependencies and no P/Invoke.

## Layout

| Layer | Path |
|---|---|
| Sparse tree, grids, transforms, masks | `Core/DataTypes/Volume/` |
| LZ4/Blosc decoders and `.vdb` reader | `Core/DataTypes/Volume/IO/` |
| Noise, CSG, filters, meshing, NanoVDB builder | `Core/DataTypes/Volume/Tools/` |
| Operators (`Lib.volume.*`) | `Operators/Lib/Symbols/volume/` |
| NanoVDB compute shader | `Operators/Lib/Assets/shaders/volume/NanoVdbVolume.hlsl` |
| Example graphs | `Operators/Examples/Symbols/Lib/volume/` |
| Example assets | `Operators/Examples/Assets/volumes/` |
| Tests | `Core.Tests/Volume/`, `Core.Tests/VdbLayoutTests.cs` |

## Core model

- **`VdbTree<T>`** — the OpenVDB tree: root, 32³ upper nodes, 32³ lower nodes, 4³ leaves. Allocation happens on write only, so reading an empty region costs nothing.
- **`VdbGrid<T>` / `VdbVolume`** — a grid is one named tree plus its transform; a `VdbVolume` is a set of grids, which is what `.vdb` files hold. Most operators act on the first float grid; vector grids (velocity, color) are carried along.
- **`VdbTransform`** — voxel size, world transform and a cached inverse for world↔voxel conversion.
- **`NodeMask`** — 64-bit words per node, with iteration helpers used by the hot paths.
- **`VdbFile`** — reads uncompressed, zip and Blosc-compressed files as written by Houdini and Blender, and writes them back.
- **`Compression`** — LZ4 block format and the Blosc frame format, implemented in managed code. These are the only two codecs OpenVDB produces in practice.
- **`NanoVdbBuffer`** — serializes a tree into the NanoVDB binary layout for float and Vector3 grids, and reads `.nvdb` files back. Bulk slot-table fills and word-wise mask scans replaced per-slot writes, cutting the slot-table portion of a rebuild by roughly 10× on a 100-leaf grid.
- **Reading that layout back** — `LoadFile` reads a file straight into one grid-sized array (no second copy of the whole file), `ReadInfo` exposes name, class, value type, voxel size, world bounds and node counts, and `TryValidate` rejects tree offsets or leaf counts that would point a shader past the end of the buffer.

## Operators

### `Lib.volume.io`

| Operator | What it does |
|---|---|
| `LoadVdb` | Reads grids from a disk path or package address. Reloads when the file changes. |
| `LoadNanoVdb` | Reads a pre-converted `.nvdb` straight to the GPU buffer, with no CPU grid and no rebuild. Outputs mirror `VolumeToNanoVdb`, plus the grid name and world bounds. |
| `SaveVdb` | Writes the grids as a `.vdb` file readable by Houdini, Blender and Maya. |
| `SaveNanoVdb` | Writes the GPU-flattened grid as an `.nvdb` file. |

### `Lib.volume.generate`

| Operator | What it does |
|---|---|
| `VolumePrimitive` | Analytic primitive as a narrow-band level set, exact SDF. |
| `VolumeFromMesh` | Mesh to narrow-band SDF or fog volume. |
| `VolumeFromPoints` | Splats points into a density volume with a normalized falloff kernel. |
| `FieldToVolume` | Samples a scalar field into a sparse grid over a box region. |

### `Lib.volume.modify`

| Operator | What it does |
|---|---|
| `VolumeCsg` | Union, intersection and difference on level sets. |
| `VolumeFilter` | Gaussian and box smoothing, median, and value offset. |
| `VolumeMath` | Add, subtract, multiply, divide, min and max between two volumes or a constant. |
| `VolumeMorphology` | Dilate, erode, open and close. |
| `VolumeNoise` | Perlin, simplex and Worley noise with fBm, ridged and billow; add, multiply or displace. |
| `VolumeResample` | Changes voxel size with trilinear resampling. |

### `Lib.volume.convert`

| Operator | What it does |
|---|---|
| `VolumeToMesh` | Marching cubes isosurface with shared vertices and gradient normals. |
| `VolumeToPoints` | Active voxels above a threshold, or a random subset, as a point list. |
| `VolumeToTexture3D` | Bakes the volume into a dense R32Float texture plus world bounds. |
| `VolumeToField` | Wraps the volume as a CPU scalar field sampled trilinearly in world space. |

### `Lib.volume.render` and `.gpu`

| Operator | What it does |
|---|---|
| `VolumeToNanoVdb` | Flattens the volume into a NanoVDB buffer and uploads it as a structured buffer for HLSL. |
| `NanoVdbRender` | Raymarches up to three NanoVDB grids (liquid, smoke, fire) inside a glass box. |
| `VolumeField` | Samples a baked 3D texture as a field for `RaymarchField`. |

### `Lib.volume.analyze`

| Operator | What it does |
|---|---|
| `VolumeInfo` | Active voxels, memory footprint, value range and NaN/Inf counts. |

## Threading and memory

Graph evaluation in TiXL is single-threaded per instance, and the volume types follow that model.

- A `VdbTree<T>` is not thread-safe. One operator instance owns its tree while it computes.
- Volumes travel through slots by reference and are never mutated after they are published, so a value read by one branch of a graph is stable while another branch reads it.
- `LoadVdb` and `SaveVdb` move file IO off the evaluation thread. Each runs at most one background task at a time; when a new trigger arrives while a task is running, the old task is dropped rather than queued. Dropped tasks have no disposal obligations and are collected by the GC.
- Results are published on the evaluation thread, so graph state is never mutated from a worker.
- Everything is managed memory. Buffers are `byte[]`, trees are ordinary object graphs, and the only native interaction on the GPU path is uploading an existing byte array.
- A `.vdb` file is treated as untrusted input. Every size read from the file is checked against the bytes actually remaining before anything is allocated, and a Blosc frame's declared output size is checked against the block the reader expects, so a truncated or forged file fails with `InvalidDataException` instead of allocating gigabytes.

These rules mean a volume operator is safe inside the normal graph, and unsafe if you try to touch its `VdbTree` from your own thread. That is the same contract as the rest of the operator library.

## Cross-platform

- No P/Invoke and no platform conditionals in `Core/DataTypes/Volume` or the volume operators.
- LZ4 and Blosc decoding are implemented in managed code, so compressed files from Houdini and Blender open on every OS without shipping a codec DLL.
- File paths use `Path` and `FileResource`, so package addresses and platform separators behave like the other asset loaders.
- The CPU operators (load, save, CSG, noise, meshing, NanoVDB build) are platform-independent.
- The GPU operators need a working device: D3D11 or Vulkan. They are the only part that depends on the render backend.

## Tests

`Core.Tests/Volume/` covers the tree, coordinates, transforms, index order, the file reader and writer, the LZ4/Blosc decoders, and the volume tools. `Core.Tests/VdbLayoutTests.cs` checks the NanoVDB binary layout.

Run them with:

```
dotnet test Core.Tests/Core.Tests.csproj --filter "FullyQualifiedName~Volume|FullyQualifiedName~Vdb|FullyQualifiedName~NanoVdb|FullyQualifiedName~Lz4|FullyQualifiedName~Blosc"
```

Current state: 99 tests, all passing on Windows. The full Release solution build has no errors and no warnings from the volume code.

## Examples

Fourteen graphs under `Operators/Examples/Symbols/Lib/volume/`:

`VolumeCsgExample`, `VolumeFilterExample`, `VolumeFireSmokeExample`, `VolumeMeshRoundtripExample`, `VolumeMorphologyExample`, `VolumeNoiseDisplaceExample`, `VolumeShakingWaterExample`, `VolumeVdbFilesExample`, `VolumeNanoVdbBoxExample`, `VolumeNanoVdbCsgExample`, `VolumeNanoVdbFireExample`, `VolumeNanoVdbLiquidExample`, `VolumeNanoVdbSaveExample`, `VolumeNanoVdbSmokeExample`.

They load `Examples:volumes/bunny.vdb` and `Examples:volumes/smoke.vdb`, which are the only binary assets the feature adds (about 18 MB).

## Related change

`Core/Operator/Slots/Slot.cs` carries two robustness fixes that the volume work depends on. A slot whose connected update action runs with no connections now keeps its last value instead of throwing out of the evaluator. A type mismatch during connection is rejected before the update action is switched, which previously left an input connected but empty and crashed on its first evaluation.



