# CITP

`Core/Output/LaserOutputStream.cs`

Publishes the laser output's frame to [Capture](https://capture.se) (or any CITP/CAEX-compatible media server) as a laser preview feed. CITP is the protocol Capture uses to visualize the laser content of the connected console/media server; the CAEX laser extension carries the actual points. See [Laser protocols](Laser%20Protocols.md) for wire details.

This is a **preview/visualization path** - it draws your laser content inside Capture's visualizer, it does not drive projectors.

The transport lives in the Output Setup: add a **CITP capture** under *Add laser device* and bind a laser output to it.

## Device settings (on the plug)

| Setting | Default | Purpose |
|---------|---------|---------|
| `Address` | - | The capture host for a direct connection. Empty = PLoc multicast discovery |
| `Port` | 0 | Direct TCP port. 0 (with an address) or empty address keeps PLoc discovery |
| `Local interface` | any | Interface for discovery and streaming |
| `Feed name` | "Laser Feed" | Name advertised in the peer's feed list |
| `Source key` | 0 | Sender identity in CAEX messages; 0 = random per session |
| `Brightness ceiling` | 1.0 | Final intensity ceiling (0..1) applied to every point right before encoding |
| `Swap color channels` | off | For peers that interpret the packed color as BGR |
| `Keepalive` | on | Keeps the feed streaming while the LASER master is off - Capture keeps showing the show while the real beam is dark |

## Behavior

1. **Discovery** - listens for CITP `PINF`/`PLoc` announcements on multicast `224.0.0.180:4809`. When a peer announces a TCP port, a connection is attempted (packets from other subnets than the selected interface are ignored). If discovery times out, a direct connection to `127.0.0.1:51245` is tried - that is Capture's default.
2. **Session** - over TCP the stream answers `GetLaserFeedList` with one feed, announced as `<FeedName> 0`, and acknowledges `EnterShow`.
3. **Streaming** - honors `LaserFeedControl` (the receiver asks for the feed and requests an FPS; the feed sends nothing before that), then streams CAEX `LaserFeedFrame` datagrams back over UDP to `224.0.0.180:4809`, converting ILDA 16-bit coordinates to CITP 12-bit and colors to RGB565.
4. A frame larger than one datagram (~12,000 points) is truncated with a warning - reduce the point density upstream.

The plug's status line follows the session: listening, connected to the peer, feed active at the requested fps. The feed list is re-announced every few seconds on the control session, so Capture re-lists the feed after either side restarts.

## Usage notes

* The frame streams only after Capture requests the feed (it shows a start button per feed) and while the **LASER** master is armed - the same master enable as every laser transport.
* If points appear shifted/scaled wrong in Capture, note that TiXL maps the full ILDA range onto Capture's 12-bit space; frame content already sits in that range, so no extra scaling should be needed.
