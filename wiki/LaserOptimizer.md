# LaserOptimizer

`Operators/Lib/laser/optimize/LaserOptimizer.cs`

Prepares a laser frame for output. Scanners are physical devices with inertia: jumping long distances or hitting corners at full speed smears the image. This operator reorders the drawing and inserts helper points so the scanners can keep up.

## Processing stages

1. **Segment detection** - the input is split into segments at blanked points (`I == 0`). Everything between two blanks is one shape.
2. **Path optimization** *(if `EnableOptimization`)* - greedy nearest-neighbour ordering: starting from the current position, the closest unvisited segment endpoint is chosen (either end of the segment; choosing the end flips the segment's direction). This minimizes total travel and therefore maximizes effective frame rate.
3. **Blanking delay** - before drawing a shape, if the jump from the current position is longer than `MaxJumpDistance`, up to `BlankingDelayPoints` blanked points are inserted at the destination, scaled by jump distance (`distance / MaxJumpDistance * delay`, clamped to at least 1).
4. **Corner dwell** - corners with a turn angle below `CornerAngleThreshold` degrees repeat the corner point `CornerAnchorCount - 1` extra times so the beam lingers and the corner renders sharply.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `LaserPoints` | - | Input frame; blanks (`I = 0`) mark segment boundaries |
| `MaxJumpDistance` | 5000 | Jumps longer than this (16-bit units, full screen = 65534) count as long |
| `BlankingDelayPoints` | 5 | Max blanked points per long jump (clamped 0..100), scaled by distance |
| `CornerAngleThreshold` | 30° | Angles below this count as sharp corners (180° = straight) |
| `CornerAnchorCount` | 3 | Repeats per sharp corner (1..10) |
| `EnableOptimization` | on | Off = keep original order, only apply blanking/corner processing |

## Outputs

| Output | Purpose |
|--------|---------|
| `OptimizedPoints` | The processed frame |
| `PointCount` | Output point count (grows with inserted blank/corner points) |

## Usage notes

* The optimizer increases the point count. If the output was already near its per-frame point budget, corners/blanking push frames longer - watch `PointCount` against your scan rate.
* Turn `EnableOptimization` off to compare, or when the input order matters (e.g. text where drawing order defines stroke overlaps).
* The nearest-neighbour pass is greedy, not a full TSP solve - cheap and predictable, but not always the globally shortest path.
