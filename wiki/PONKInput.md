# PONKInput

`Operators/Lib/laser/input/PONKInput.cs`

Receives laser frames from any PONK-compliant sender (e.g. MadMapper) and turns them into TiXL laser points - the mirror image of [[PONKOutput]].

## Behavior

* A background listener thread binds a UDP socket to `Port` (default **5583**) on the selected interface and joins the default multicast group **239.255.10.24**; unicast packets to the same port land in the same socket.
* Packets are validated (magic `PONK-UDP`, protocol version, chunk indices), reassembled per frame number (chunks may arrive in any order; a later chunk disagreeing with the frame's chunk count/CRC/sender resets the frame), and verified against the byte-sum checksum before decoding.
* Both mandatory formats decode: `XY_F32_RGB_U8` (11 bytes/point) and `XY_U16_RGB` (10 bytes/point). Metadata entries are skipped - render hints are not applied server-side.
* Decoded points are remapped from PONK -1..1 onto the MinX/MaxX/MinY/MaxY sub-rectangle and scaled to ILDA 16-bit; colors scale 8 -> 16-bit.
* The listener thread auto-restarts if it dies (checked every ~2 s while `Active`).
* Incomplete frames older than `Timeout` seconds are dropped to free their buffers.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `Active` | on | Start/stop the listener |
| `LocalIpAddress` | - | Interface to bind; empty = all interfaces |
| `Port` | 5583 | UDP port to listen on |
| `Timeout` | 1.2 s | Age at which incomplete frames are discarded |
| `ExpectedSenderId` | 0 | Accept only this sender id (0 = any) |
| `MinX` / `MaxX` | -1 / 1 | Horizontal sub-rectangle stretched to the full output |
| `MinY` / `MaxY` | -1 / 1 | Vertical sub-rectangle stretched to the full output |
| `PrintToLog` | off | Bind/join, CRC-mismatch and per-frame logging |

## Outputs

| Output | Purpose |
|--------|---------|
| `LaserPoints` | Latest complete frame (`StructuredList<LaserPoint>`) |
| `StatusMessage` | Listening / receiving / error state |
| `FrameCount` | Total complete frames received |
| `SenderName` | Name of the last frame's sender |
| `SenderId` | Numeric id of the last frame's sender (use for `ExpectedSenderId`) |

## Usage notes

* To isolate one sender among several, read `SenderId` while only that sender transmits, then set `ExpectedSenderId`.
* The Min/Max inputs are a crop-and-zoom: e.g. MinX = -0.5, MaxX = 0.5 takes the middle half of the incoming content and stretches it across the full laser output.
* Multiple listeners on one machine can share the port (`ReuseAddress`); each receives a copy of the multicast traffic.
