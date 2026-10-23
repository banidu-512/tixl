# Laser optimization

Scanners are physical devices with inertia: jumping long distances or hitting corners at full speed smears the image. Every laser output runs an optimization stage in the Output Setup (select the output - the **Optimization** section) that reorders the drawing and inserts helper points so the scanners can keep up. The same settings apply no matter which plug the output streams to.

## Processing stages

1. **Segment detection** - the frame is split into segments at blanked points (`I == 0`). Everything between two blanks is one shape. This is also how dots stay separate from lines: [LaserCamera](LaserCamera.md) (dots) and [MeshToLaserPoints](MeshToLaserPoints.md) (vertices mode) encode each dot as *blanked jump + dwell run*, which shows up here as its own single-shape segment.
2. **Path optimization** *(if `Reorder path` is on)* - greedy nearest-neighbour ordering: starting from the current position, the closest unvisited segment endpoint is chosen (either end of the segment; choosing the end flips the segment's direction). This minimizes total travel and therefore maximizes effective frame rate.
3. **Blanking** - the beam is always off while traveling between shapes: every move inserts at least 1 blanked point at the destination, scaled by jump length up to `Blanking points` (`ceil(distance / Max jump * delay)`). Without this rule, nearby shapes (and dot dwell runs) would be connected by visible lit lines. Sub-pixel moves insert nothing - the beam is already there. `Max jump` is therefore a *scaling reference*, not an on/off threshold.
4. **Corner dwell** - corners with a turn angle below `Corner angle` degrees repeat the corner point `Corner anchors - 1` extra times so the beam lingers and the corner renders sharply. (Dwell runs of dots evaluate as 180° and are never duplicated.)

## The settings

| Setting | Default | Purpose |
|---------|---------|---------|
| `Reorder path` | on | Off = keep original order, only apply blanking/corner processing |
| `Max jump (units)` | 5000 | Scaling reference for blank delay (16-bit units, full screen = 65534) |
| `Blanking points` | 5 | Max blanked points per inter-shape move (clamped 0..100), scaled by distance; always ≥ 1 for real moves |
| `Corner angle (°)` | 30° | Angles below this count as sharp corners (180° = straight) |
| `Corner anchors` | 3 | Repeats per sharp corner (1..10) |

## Usage notes

* The optimization increases the point count. If the output was already near its per-frame point budget, corners/blanking push frames longer - watch the point count against your scan rate.
* Turn `Reorder path` off to compare, or when the input order matters (e.g. text where drawing order defines stroke overlaps).
* The nearest-neighbour pass is greedy, not a full TSP solve - cheap and predictable, but not always the globally shortest path. It is O(n²) in the segment count, so with very large dot clouds (hundreds+ of dots) consider turning `Reorder path` off and letting blanking do the work in input order.

## See also

- [Laser safety](Laser%20Safety.md) - the safety stage that always runs after this one.
- [Laser data model](Laser%20Data%20Model.md) - how frames reach the optimization stage.
