# Laser Data Model

## LaserPoint

`T3.Core.DataTypes.LaserPoint` (`Core/DataTypes/LaserPoint.cs`) is the currency of the laser operators. It is a packed struct of six 32-bit integers:

| Field | Meaning | Range |
|-------|---------|-------|
| `X`, `Y` | Position | -32768..32767 (ILDA-style 16-bit, origin at center, +Y up) |
| `R`, `G`, `B` | Color | 0..65535 |
| `I` | Intensity (blanking) | 0..65535, **0 = beam off / blanked jump** |

Points flow between operators as a `StructuredList<LaserPoint>`.

Blanking is the convention everything builds on: `LaserOptimizer` uses `I == 0` runs to detect shape segments, and `LaserPoint.CreateBlanked(x, y)` produces the delay points it inserts.

## DacPoint (legacy, currently unused)

`Operators/Lib/laser/LaserTypes.cs` defines a second struct, `DacPoint` (18 bytes packed: control u16, X/Y i16, R/G/B/I u16, two user u16), with a conversion constructor from `LaserPoint` (control bit 0x40 set when intensity is 0). **Nothing currently instantiates it** - [[EtherDreamOutput]] converts to the NuGet library's `DacPointDto` directly. It documents the wire layout and is kept for future DAC-level work; see [[LaserCore EtherDream Library]].

## Coordinate spaces

Three different coordinate conventions exist across the laser stack. All conversions happen inside the operators; as a user you mostly need to know which one an operator speaks.

| Space | Used by | Range | Notes |
|-------|---------|-------|-------|
| **ILDA 16-bit** | LaserPoint, EtherDreamOutput | -32768..32767 both axes | The internal standard. Origin at center. |
| **PONK normalized** | PONKOutput, PONKInput | -1.0..1.0 (float) | PONK's mandatory `XY_F32_RGB_U8` format; colors 8-bit |
| **CITP 12-bit** | CITPLaser | 0..4095 unsigned | CAEX LaserFeedFrame packs X/Y into 12 bits; colors RGB565 |

Conversions performed by the operators:

* **LaserCamera** projects world-space points via a camera, normalizes to -1..1, clamps, then scales by 32767 -> ILDA 16-bit.
* **PONKOutput** clamps ILDA -32768..32767 into -1..1 (divide by 32767) and reduces colors to 8-bit (`R >> 8`).
* **PONKInput** maps PONK -1..1 onto the configured MinX/MaxX/MinY/MaxY sub-rectangle, then scales to ILDA 16-bit; colors scale 8 -> 16-bit (`<< 8`).
* **CITPLaser** applies `ScaleX/Y` (default 1/16) and `OffsetX/Y` and masks to 12 bits; ILDA full scale maps roughly onto the full CITP range.
* **EtherDreamOutput** passes ILDA values through essentially unchanged (clamped to short/ushort), which is why ILDA 16-bit is the internal standard.

## Frame semantics

A `StructuredList<LaserPoint>` is one *frame* - a complete drawing intended to be scanned repeatedly at the output's point rate. Conventions:

* Runs of blanked points (`I == 0`) separate shapes; the DAC/receiver keeps the beam off during them.
* There is no path metadata inside LaserPoint itself - path identity travels out-of-band (PONK `PATHNUMB`, CITP feed index).
* Output operators keep re-sending the last frame when the graph pauses (`LoopLastFrame`), because a laser only shows an image while it is constantly rescanned.

## Glossary

| Term | Meaning |
|------|---------|
| **DAC** | Digital-to-analog converter between software and the scanners (e.g. Ether Dream) |
| **Scanner / galvo** | Mirror galvanometers that deflect the beam; their speed and inertia shape everything |
| **kpps** | Kilo points per second - how fast the DAC scans points (30kpps is typical) |
| **Blanking** | Beam off while moving; encoded as `I = 0` in LaserPoint |
| **ILDA** | Interactive Laser Show Association; its 16-bit coordinate format is the de-facto standard |
| **Jump** | Move with the beam off; **stroke** = draw with beam on |
| **Corner dwell** | Repeating points at sharp corners so scanners slow down enough to render them |
| **E-stop** | Emergency stop; cuts the beam and requires explicit clearing (see [[EtherDreamOutput]]) |
| **MPE** | Maximum permissible exposure - the legal safety limit for beam exposure (see [[Laser Safety]]) |
