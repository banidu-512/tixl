# PONKOutput

`Operators/Lib/laser/output/PONKOutput.cs`

Streams laser frames to a PONK-compatible receiver (e.g. MadMapper's Ponk input) via UDP multicast or unicast. PONK (*Pathes Over NetworK*) is the protocol MadMapper uses for laser path streaming; the wire format is documented in [[Laser Protocols]].

## Behavior

* Default destination: multicast group **239.255.10.24**, port **5583**.
* Each frame is packed in the mandatory `XY_F32_RGB_U8` format (float XY in -1..1, 8-bit RGB), prefixed with the two metadata entries `PATHNUMB` and `MAXSPEED`, then split into chunks of up to 1420 bytes per datagram. All chunks of a frame carry the same frame number and byte-sum checksum.
* Frames are capped at 32000 points (protocol limits: point count is a `ushort`, chunk count a `byte`).
* Sender identity: a 32-byte name (the `SenderName` input) plus a stable random `int` sender id generated per operator instance.
* **Threading model mirrors [[EtherDreamOutput]]**: a connection loop owns the UDP socket (bind + multicast interface/TTL/group options) and a send loop drains a queue of max 5 frames. Send errors are retried up to 20 times before the socket is rebuilt. The socket is closed off-thread to avoid multicast-leave hangs.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `LaserPoints` | - | Frame to send (`StructuredList<LaserPoint>`) |
| `Enable` | off | Master switch (restarts the socket when changed) |
| `SimulationMode` | on | Count frames without opening a socket; `IsConnected` fakes true |
| `UseMulticast` | on | Multicast group vs. unicast target |
| `TargetIpAddress` | 239.255.10.24 | Destination IP (dropdown offers the default group) |
| `Port` | 5583 | Destination UDP port |
| `LocalIpAddress` | - | Outgoing interface; matters for multicast routing |
| `SenderName` | "TiXL" | Name embedded in every packet header |
| `MaxScanSpeed` | 1.0 | `MAXSPEED` render hint (scanner-speed multiplier request) |
| `PathNumber` | 1 | `PATHNUMB` render hint (path identity across frames) |
| `LoopLastFrame` | on | Keep sending the last frame when the graph pauses |
| `PrintToLog` | off | Socket/retry logging |

## Outputs

| Output | Purpose |
|--------|---------|
| `IsConnected` | Socket is open and sending (simulated true in simulation mode) |
| `PacketsSent` | Total UDP datagrams sent (including multi-chunk frames) |
| `StatusMessage` | Human-readable state |
| `PointsSent` | Total points sent since creation |
| `Command` | Evaluation trigger |

## Usage notes

* MadMapper listens on the default group/port; for a specific machine over unicast, switch `UseMulticast` off and set its IP.
* Points are clamped into -1..1 and colors reduced to 8-bit - plan bright, saturated content accordingly.
* UDP is best-effort: frames may be dropped on congested networks. Keep frames compact ([[LaserOptimizer]] increases point count; sometimes less optimization is better for PONK).
