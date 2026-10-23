# Laser projection

TiXL can drive real laser projectors: render geometry into a laser point stream
([LaserCamera]), shape it with the output's optimization and safety settings, and
stream it to the projector over EtherDream (ILDA), PONK or CITP — all configured in
the [Output Setup](OutputSetup.md).

**Start here:**

- [Laser safety](laser/Laser%20Safety.md) — read before enabling a real output.
- [Output Setup](OutputSetup.md) — laser outputs, devices and the master enable.

## The operators

| Operator | Purpose | Doc |
|---|---|---|
| **LaserCamera** | Camera + scene capture: frames any geometry into the laser's viewport | [doc](laser/LaserCamera.md) |
| **MeshToLaserPoints** | Bakes triangle meshes into laser point streams | [doc](laser/MeshToLaserPoints.md) |
| **LaserPointsToBuffer** | Bridges laser frames back into the graph (preview, re-projection) | [doc](../operators/lib/laser/convert/LaserPointsToBuffer.md) |
| **PONKInput** | Receives PONK laser frames over UDP | [doc](laser/PONKInput.md) |

Placement, optimization, safety and the transports are not operators: they are
edited on the laser output and its plug in the Output Setup.

## The transports (Output Setup)

| Transport | Purpose | Doc |
|---|---|---|
| **EtherDream DAC** | ILDA over TCP — real show lasers | [doc](laser/EtherDream.md) |
| **PONK endpoint** | PONK over UDP — e.g. MadMapper | [doc](laser/PONK.md) |
| **CITP capture** | CITP/CAEX preview feed — e.g. Capture | [doc](laser/CITP.md) |

The safety pipeline (calibration gate, safe zone, dwell/jump/brightness limits)
and the optimization pipeline (path reorder, blanking, corner anchors) are edited
on the output: [Laser optimization](laser/LaserOptimizer.md),
[Laser safety](laser/Laser%20Safety.md).

## Background

- [Laser data model](laser/Laser%20Data%20Model.md) — how laser points flow
  through the setup.
- [Points vs. Lines](laser/Points%20vs.%20Lines.md) — what the difference in
  the beam looks like and when to use which.
- [Laser protocols](laser/Laser%20Protocols.md) — EtherDream / ILDA, PONK and
  CITP compared.
- [LaserCore EtherDream library](laser/LaserCore%20EtherDream%20Library.md) —
  the DAC transport implementation in Core.

## Running a show

- [Testing](laser/Testing.md) — bench-testing the chain before a venue.
- [Troubleshooting](laser/Troubleshooting.md) — no output, jitter, dropped
  frames and other common issues.
