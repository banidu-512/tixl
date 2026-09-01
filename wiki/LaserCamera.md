# LaserCamera

`Operators/Lib/laser/camera/LaserCamera.cs`

Projects 3D scene points into 2D laser space. It is the bridge between the normal TiXL 3D scene graph and the laser pipeline.

## What it does

1. Reads a structured GPU buffer of `Point` structs (`BufferWithViews`) - the same buffers particle/point systems produce.
2. Projects each point through the connected camera (`WorldToCamera * CameraToClipSpace`).
3. Culls points with invalid depth (`z <= 0.001` or `z > 1000`) and points that project absurdly far outside the view (|NDC| > 10).
4. Clamps the survivors to -1..1 and scales to ILDA 16-bit XY; converts the point color (float 0..1 RGB) to 16-bit with full intensity.
5. Publishes the result as `StructuredList<LaserPoint>` on **LaserPoints**.

## Inputs

| Input | Purpose |
|-------|---------|
| `PointBuffer` | Structured buffer of 3D points to project |
| `Camera` | Camera (`ICamera`) used for the projection; typically the scene camera |
| `Resolution` | Reserved for aspect handling; currently unused by the math |
| `StartIndex` / `MaxCount` | Read only a slice of the buffer; `MaxCount = 0` reads all remaining |
| `TriggerUpdate` | Pulse for a single read when `UpdateContinuously` is off (auto-resets) |
| `UpdateContinuously` | Read and reproject every frame |
| `UseAsync` | GPU read-back on a worker thread (result arrives ~1 frame later) |
| `PrintToLog` | Log read/cull counts per update |

## Outputs

| Output | Purpose |
|--------|---------|
| `LaserPoints` | The projected frame (`StructuredList<LaserPoint>`) |
| `PointCount` | Number of points after culling |
| `SampleX` / `SampleY` | First 10 converted coordinates, for debugging |

## Usage notes

* If nothing comes out, check the culling: points behind the camera are dropped, and content that is *in front of* the camera but far outside the view frustum gets clamped into the frame, which can produce long unwanted lines. Frame your content so the camera actually looks at it.
* **UseAsync** trades one frame of latency for not stalling the render thread during GPU read-back. For small buffers, synchronous mode is fine.
* Color: laser colors ignore transparency/materials - only the point color channel is used, at full intensity.
