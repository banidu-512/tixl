# Troubleshooting

Symptom -> cause -> fix for the laser pipeline. Operators referenced: [[LaserCamera]], [[LaserOptimizer]], [[EtherDreamOutput]], [[PONKOutput]], [[PONKInput]], [[CITPLaser]].

## Nothing comes out of the projector

| Check | Fix |
|-------|-----|
| Output disabled / simulation still on | `Enable` on, `SimulationMode` off on the output operator |
| Status says "Connecting..." forever | Wrong DAC IP - enable `DiscoverDevices` and pick from the dropdown |
| Status shows warm-up retries | The projector light engine is still warming; wait (~60 s max), then check the projector itself |
| E-stop warning | Clear the stop on the hardware, then toggle `ClearEStop` |
| Points counted but nothing drawn | Frame may be all blanked points (`I = 0`) - check upstream of the output |
| PointCount is 0 after [[LaserCamera]] | Content behind the camera or outside the frustum is culled - aim the camera at the points |

## Image looks wrong

| Symptom | Cause | Fix |
|---------|-------|-----|
| Doubled/ghosted shapes, corners smeared | Scanners can't follow | Use [[LaserOptimizer]]; raise `BlankingDelayPoints`/`CornerAnchorCount`, lower `ScanRate` |
| Shapes drawn in chaotic order, lots of invisible jumps | Path order unoptimized | `EnableOptimization` on [[LaserOptimizer]] |
| Image scaled into a corner or squashed | Coordinate-space mixup | See [[Laser Data Model]]; for [[CITPLaser]] tune `ScaleX/Y` (default 1/16) and `OffsetX/Y` |
| PONK content zoomed/cropped wrongly | Sub-rectangle mapping | Set MinX/MaxX/MinY/MaxY on [[PONKInput]] back to -1..1 |
| Beam flickers when graph pauses | `LoopLastFrame` off | Enable it on the output operator to keep rescanning the last frame |

## PONK specific

| Symptom | Cause | Fix |
|---------|-------|-----|
| `FrameCount` stays 0 | Sender/receiver mismatch | Same port (default 5583), same multicast group, `ExpectedSenderId` = 0, firewall allows UDP |
| `ExpectedSenderId` filtering drops everything | Id is random per sender instance | Read the current value from the `SenderId` output |
| Chunks never complete | Fragment loss or timeout too low | Raise `Timeout`; check network for UDP loss |
| Log shows CRC mismatch | Corrupted datagrams, or a sender with the old 48-byte-header bug | Update the sender; see [[Laser Protocols]] |

## EtherDream specific

| Symptom | Cause | Fix |
|---------|-------|-----|
| Discovery finds nothing | DAC on another subnet/broadcast blocked | Connect by IP directly; check `LocalIpAddress` matches the DAC's subnet |
| "Discovery port 7654 already in use" warning | Another operator/app owns discovery | Harmless - shared discovery results are reused |
| Second app can't connect to the DAC | Ether Dream allows one session | Disconnect the first (disable the operator) |
| Send errors repeat, then reconnect loop | Unstable network/DAC | Check `PrintToLog` details; check cabling |

## CITP / Capture specific

| Symptom | Cause | Fix |
|---------|-------|-----|
| Capture never sees the feed | **Known limitation:** the current build hardcodes a direct connection (10.0.0.233:47999) and bypasses discovery - see note in [[CITPLaser]] | Remove the hardcoded connect in `StartCitpClient`, or point it at your Capture host |
| Feed listed but no frames | Capture hasn't sent `LaserFeedControl` start | Check `FeedActive`/`RequiredFps` outputs |

## Debugging tools

* `PrintToLog` on every operator - details in the log (open with **F12** in TiXL).
* Output operators publish `StatusMessage` (also visible via the node's status bar) and counters (`PointsSent`, `FrameCount`, `PacketsSent`, `BufferFullness`).
* [[Testing]] pages describe the protocol regression harnesses in `Core.Tests` - useful as executable protocol references.
