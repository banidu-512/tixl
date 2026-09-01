# Laser Tools Wiki

TiXL operators and supporting libraries for driving **show lasers** from a graph: rendering 3D content into laser coordinates, optimizing it for scanners, and streaming it to hardware or other software.

## Operator map

```
                       TiXL scene graph
                              |
                       [LaserCamera]      3D points -> LaserPoints (ILDA 16-bit)
                              |
   PONKInput ---> LaserPoints [LaserOptimizer]  reorder, blanking, corner dwell
                              |
        +---------------------+--------------------+
        |                     |                    |
 [EtherDreamOutput]     [PONKOutput]          [CITPLaser]
   Ether Dream DAC     PONK/UDP receivers    CITP/CAEX preview
   (TCP 7765)          (UDP 239.255.10.24:5583)  (Capture media server)
        |                     |                    |
     show laser           MadMapper etc.        Capture preview
```

| Operator | Library | Purpose |
|----------|---------|---------|
| [[LaserCamera]] | - | Project 3D scene points into laser space |
| [[LaserOptimizer]] | - | Path reordering, blanking and corner dwell |
| [[EtherDreamOutput]] | [[LaserCore EtherDream Library\|LaserCore.EtherDream.Net]] | Stream frames to an Ether Dream DAC |
| [[PONKOutput]] | - | Send frames via the PONK UDP protocol |
| [[PONKInput]] | - | Receive frames via the PONK UDP protocol |
| [[CITPLaser]] | - | Publish laser feeds as CITP/CAEX previews |

All operators live in `Operators/Lib/laser/` and communicate through a shared data type: a `StructuredList` of **LaserPoint**. See [[Laser Data Model]] for the coordinate spaces involved - getting these wrong is the most common source of "my laser shows nothing / shows garbage" problems.

## Quick start

**Without hardware** (verify the pipeline):

1. Create `[LaserCamera]`, connect a point buffer and a camera.
2. Add `[LaserOptimizer]` between camera and output.
3. Add `[EtherDreamOutput]`, enable `SimulationMode` and `PrintToLog`, then enable the operator.
4. Watch the status message and log: points should be counted per frame.

**With an Ether Dream DAC**:

1. Enable `DiscoverDevices` on `[EtherDreamOutput]` - found DACs appear in the log and in the `IpAddress` dropdown.
2. Pick the DAC, turn `SimulationMode` off, then `Enable`.
3. Watch for light-engine warm-up retries in the status; after an emergency stop use `ClearEStop`.

**Exchange with MadMapper** (either direction):

* TiXL -> MadMapper: `[PONKOutput]` to the default multicast group `239.255.10.24:5583`.
* MadMapper -> TiXL: `[PONKInput]` listening on port `5583`.

## Where things live in the repository

| Path | Contents |
|------|----------|
| `Operators/Lib/laser/camera/` | [[LaserCamera]] (`.cs` symbol code, `.t3` pin definition, `.t3ui` UI/descriptions) |
| `Operators/Lib/laser/optimize/` | [[LaserOptimizer]] |
| `Operators/Lib/laser/output/` | [[EtherDreamOutput]], [[PONKOutput]], [[CITPLaser]] |
| `Operators/Lib/laser/input/` | [[PONKInput]] |
| `Operators/Lib/laser/LaserTypes.cs` | `DacPoint` wire struct (legacy, see [[Laser Data Model]]) |
| `Core/DataTypes/LaserPoint.cs` | The shared `LaserPoint` struct |
| `Core.Tests/EtherDreamDacProtocolTests.cs` | Fake-DAC protocol regression harness (see [[Testing]]) |
| `Core.Tests/PonkUdpWireFormatTests.cs` | PONK wire-format regression tests (see [[Testing]]) |
| `wiki/` | This wiki's source (push to the GitHub wiki) |

Operator descriptions (the tooltips and Help-window texts) live in each `.t3ui` file - edit them there, not in the C# source.

## Pages

* [[Laser Data Model]] - LaserPoint/DacPoint structs, coordinate spaces, glossary
* **Operators**: [[LaserCamera]] · [[LaserOptimizer]] · [[EtherDreamOutput]] · [[PONKOutput]] · [[PONKInput]] · [[CITPLaser]]
* [[Laser Protocols]] - Ether Dream, PONK and CITP/CAEX wire-level notes
* [[LaserCore EtherDream Library]] - the NuGet library's behaviors and quirks
* [[Testing]] - protocol regression tests and how to run them
* [[Troubleshooting]] - symptom -> cause -> fix
* [[Laser Safety]] - read this before pointing a real beam anywhere

## Related work outside this branch

An ISF-derived laser material (`MadMapperLaserMaterial`) lives on the stacked `feat/madmapper-laser-material` branch and is intentionally not part of `feature/laser-operators`; any stale copy under `bin/` is build residue.

## Tests

The protocol layers are covered by regression tests - see [[Testing]].

* `Core.Tests/EtherDreamDacProtocolTests.cs` - Ether Dream DAC packet/handshake behavior
* `Core.Tests/PonkUdpWireFormatTests.cs` - PONK header layout, chunking and checksum
