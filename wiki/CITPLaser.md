# CITPLaser

`Operators/Lib/laser/output/CITPLaser.cs`

Publishes laser point feeds to [Capture](https://capture.se) (or any CITP/CAEX-compatible media server) as laser preview feeds. CITP is the protocol Capture uses to visualize laser content of the connected console/media server; the CAEX laser extension carries the actual points. See [[Laser Protocols]] for wire details.

This is a **preview/visualization path** - it draws your laser content inside Capture's visualizer, it does not drive projectors.

## Behavior

1. **Discovery** - listens for CITP `PINF`/`PLoc` announcements on multicast `224.0.0.180:4809`. When a peer announces a TCP port, a connection is attempted (packets from other subnets than the selected `LocalIpAddress` are ignored).
2. **Session** - over TCP the operator answers `GetLaserFeedList` with one feed per connected `LaserPointsFeeds` input, announced as `<FeedName> 0`, `<FeedName> 1`, ...
3. **Streaming** - honors `LaserFeedControl` (per-feed start/stop and requested FPS, surfaced on `FeedActive`/`RequiredFps`), and streams each input as CAEX `LaserFeedFrame` datagrams (UDP back to the peer), converting ILDA 16-bit coordinates to CITP 12-bit and colors to RGB565.

> **Current limitation:** `StartCitpClient` currently *hardcodes a direct connection* (`10.0.0.233:47999`) and bypasses UDP discovery, apparently left over from handshake debugging. Until that is removed, discovery and the `CapturePort` override have no effect. See the TODO in the source.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `Enable` | off | Start/stop the CITP client |
| `LocalIpAddress` | - | Interface for discovery/streaming (dropdown) |
| `LaserPointsFeeds` | - | Multi-input: each connected list becomes one feed |
| `ScaleX` / `ScaleY` | 1/16 | Scale to CITP's 12-bit space (default maps full ILDA range) |
| `OffsetX` / `OffsetY` | 0 | Offset in 16-bit units, applied before scaling |
| `FeedName` | "Laser Feed" | Base name advertised in the feed list |
| `SourceKey` | 0 | Sender identity in CAEX messages; 0 = random per start |
| `ReconnectTrigger` | - | Pulse to restart discovery + TCP |
| `CapturePort` | 0 | Manual TCP port override for direct 127.0.0.1 connections (currently bypassed - see limitation) |
| `PrintToLog` | off | Discovery/connection/frame logging |

## Outputs

| Output | Purpose |
|--------|---------|
| `IsConnected` | TCP session with the peer is up |
| `ActiveFeeds` | Number of feeds that received points this frame |
| `FeedActive` | Whether the peer requested frames (LaserFeedControl) |
| `RequiredFps` | FPS the peer asked for |
| `StatusMessage` | Human-readable state |
| `Command` | Evaluation trigger |

## Usage notes

* Connect each laser scene you want to preview as a separate input; Capture shows them as separate laser fixtures.
* If points appear shifted/scaled wrong in Capture, adjust `Scale`/`Offset` - the defaults assume the ILDA range should fill Capture's 12-bit space.
