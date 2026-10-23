# LaserCamera

`Operators/Lib/Symbols/laser/camera/LaserCamera.cs`

Geometry-to-laser converter **and** a standard TiXL camera in one: like the common Camera op it renders the scene connected to **Scene** through its own lens (selectable in the output window camera dropdown, mouse-drivable), while every point/line/mesh drawn inside that scene is captured and encoded into the same laser frame - dots, line segments and mesh wireframes merged, with the `Group`/transform scopes active at draw time applied.

The legacy direct inputs (PointBuffer / LineBuffer / Mesh) keep working and merge into the same frame. A camera op connected to **Camera** still drives the projection, so older patches behave unchanged.

## What it does

1. Builds its camera matrices from its own inputs (Position/Target/FieldOfView/...), or takes them from a connected camera op when **Camera** is wired (backwards compatibility).
2. If **Scene** is connected: evaluates the scene subtree like a Camera op does (the scene renders normally through this lens) while a `GeometryCapture` collector on the `EvaluationContext` records every draw - the stock draw ops register the geometry wired into their internal `Draw`/`DrawInstancedIndirect` op's `CaptureVertices`/`CaptureIndices` inputs, and `_DispatchSceneDraws` registers PBR scene meshes per dispatch, each with the live `ObjectToWorld` transform.
3. Reads the direct inputs from the GPU (point buffer, line buffer, mesh vertex+index buffers) via CPU readback - only when something changed, or `UpdateContinuously`/`TriggerUpdate` says so. Scene capture reconverts every frame.
4. Projects every position through `WorldToCamera * CameraToClipSpace` (captured entries prefixed with their `ObjectToWorld`), culling points with invalid depth (`z <= 0.001` or `z > 1000`) or absurd view offsets (|NDC| > 10), clamping survivors to -1..1 and scaling to ILDA 16-bit XY.
5. Encodes each source as its primitive (see below) so the stream matches what the scanners will draw.
6. Publishes the combined frame as `StructuredList<LaserPoint>` on **LaserPoints**.

## Capture rules

* Capture registration lives in the low-level **`Draw`/`DrawInstancedIndirect` ops** (and `_DispatchSceneDraws`), because those are command slots that re-run **every frame** - the data ops that bind buffers (`GetBufferComponents`, ...) are frozen by TiXL's dirty-flag caching in static subtrees and would never re-run. The stock draw graphs wire their geometry into the Draw op's `CaptureVertices`/`CaptureIndices` inputs; custom draw compositions can wire the same inputs to capture too.
* Point vs. line content is told apart **after readback**: line strips written by `PairPointsForLines` carry NaN-scale separator points, point clouds don't. Meshes are recognized by their `PbrVertex` stride plus paired index buffer.
* The same buffer drawn repeatedly under the same transform is captured once; under a different transform it is captured again. Max 256 entries per frame (truncated beyond that).
* The **Capture Dots/Lines/Meshes** toggles mute whole kinds - e.g. turn off meshes to keep generated template meshes (DrawTubes/DrawRibbons) or a solid backdrop out of the beam.
* If the render path already evaluated the scene this frame, the laser pull reuses that frame; in laser-only rigs (nothing renders the Output) the laser pull evaluates the scene itself.
* Only buffer-based geometry is captured - content rasterized purely into images is invisible to the laser, same as before.

## PointBuffer - dots

Every projected point becomes an **isolated dot**: a blanked jump to it, then `DotDwellPoints` lit repeats. Because the move happens with the beam off, scattered point clouds (DrawPoints-style content) are never connected by lit lines - the dot encoding travels inside the stream itself (see [Points vs. Lines](Points%20vs.%20Lines.md)).

## LineBuffer - line segments

TiXL line buffers are strips: consecutive points form segments, and breaks are marked by separator points (`Scale.x = NaN/Inf`, as written by `PairPointsForLines`) which duplicate positions and produce zero-length joints. A segment is drawn only if neither endpoint is a separator and it has real length; every drawn segment gets a **blanked approach point** so separate lines are never connected by lit strokes. Both stroke setups and pair-style buffers decode correctly under this rule.

## Mesh - wireframe

Each *unique* triangle edge (shared edges deduplicated) becomes an isolated lit run: blanked approach point, then straight-line samples every `MaxEdgeStep` units (256 samples max per edge, 65536 edges max). Direct-input positions are model-space; transform the mesh before this operator. Scene-captured meshes are transformed by their draw-time `ObjectToWorld`. Color comes from the edge's start vertex `ColorRgb`.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `Scene` | - | Subtree to render and capture - everything drawn inside merges into the laser frame |
| `Position` / `Target` / `Up` | (0,0,2.4141) / origin / +Y | Own camera placement (used when `Camera` is unconnected) |
| `FieldOfView` / `Roll` / `ClipPlanes` / `AspectRatio` | 45° / 0° / 0.01..1000 / -1 | Own projection parameters; aspect -1 = render resolution |
| `CaptureDots` / `CaptureLines` / `CaptureMeshes` | on | Per-primitive capture toggles for scene content |
| `PointBuffer` | - | Direct point content - encoded as dwell dots |
| `LineBuffer` | - | Direct line content (TiXL line strip conventions) - encoded as blank-separated segments |
| `Mesh` | - | Direct mesh content (`MeshBuffers`) - encoded as wireframe edges |
| `Camera` | - | Optional external camera override (`ICamera`); drives projection exactly like before the op became a camera |
| `DotDwellPoints` | 8 | Lit repeats per dot (1..64) - brighter/stabler dots vs frame budget |
| `MaxEdgeStep` | 4000 | Wireframe sample spacing in 16-bit units |
| `StartIndex` / `MaxCount` | 0 | Slice applied to the direct point and line buffers; 0 = all |
| `TriggerUpdate` | - | Pulse for a single re-read when `UpdateContinuously` is off (auto-resets) |
| `UpdateContinuously` | off | Re-read and reproject every frame (implied when `Scene` is connected) |
| `PrintToLog` | off | Log per-source counts (captured sources/dots/segments/edges) per update |

## Outputs

| Output | Purpose |
|--------|---------|
| `LaserPoints` | The combined frame (`StructuredList<LaserPoint>`) |
| `PointCount` | Output points including blanked approaches and dwell repeats |
| `SampleX` / `SampleY` | First 10 converted coordinates, for debugging |
| `Output` | The scene rendered through this camera as a render command - plug into a render chain; enables the camera dropdown and mouse control |
| `Reference` | This instance as `ICamera` for camera-reference ops |

## Usage notes

* If nothing comes out, check the culling: points behind the camera are dropped, and content far outside the view frustum gets clamped into the frame, which can produce long unwanted lines. Frame your content so the camera actually looks at it.
* Scene capture costs one CPU readback per unique buffer per frame - keep scenes lean.
* Instanced meshes (DrawMeshAtPoints) capture as one template mesh, not N instances.
* Color: laser colors ignore transparency/materials - the point/vertex color channel is used directly.
* The frame this operator outputs is what **all** outputs ([EtherDream](EtherDream.md), [PONK](PONK.md), [CITP](CITP.md)) draw - see [Points vs. Lines](Points%20vs.%20Lines.md) for why the image matches across them.

## History

* 2026-09-02: replaced the manual `Mode` dropdown (Lines/Points) with automatic per-input encoding, and absorbed the mesh conversion of the short-lived `MeshToLaserPoints` operator (now removed). `UseAsync` was dropped in the rework - readback is change-gated instead.
* 2026-09-02: became a standard camera op (`ICamera` + scene command wrapper) with subtree geometry capture over the new `Scene` input (`GeometryCapture` collector on `EvaluationContext`, stage/flush hooks in `GetBufferComponents`, `_MeshBufferComponents`, `Draw`, `DrawInstancedIndirect` and direct registration in `_DispatchSceneDraws`); the old `Camera` input stayed as an optional external-camera override.
