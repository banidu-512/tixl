# EtherDreamOutput

`Operators/Lib/laser/output/EtherDreamOutput.cs`

Streams laser frames to an [Ether Dream](https://etherdream.com) DAC over TCP (fixed port **7765**). This is the primary path for driving real show lasers.

## Threading model

* **Connection loop** (background task): the library's `Dac` constructor performs a *blocking* TCP connect, so it runs off-thread. It retries every 2 s until cancelled and rebuilds the connection after fatal errors.
* **Send loop** (background task): dequeues queued frames and calls `StreamPoints`, which paces points into the DAC buffer at the configured scan rate. Transient errors are retried (up to 5 in a row before reconnecting); `InvalidOperationException` from the light engine is treated as warm-up and retried for up to ~60 s.
* **Discovery loop** (optional): polls Ether Dream UDP broadcasts on port 7654, logs new devices and feeds the `IpAddress` dropdown via the library's shared static device list.
* The UI thread only enqueues frames (max 10 queued frames, oldest dropped) and publishes status; the DAC is disposed off-thread because its stop command can block for seconds.

## Inputs

| Input | Default | Purpose |
|-------|---------|---------|
| `LaserPoints` | - | Frame to stream (`StructuredList<LaserPoint>`) |
| `Enable` | off | Master switch; tearing down the connection when switched off |
| `IpAddress` | 192.168.1.100 | DAC address; dropdown lists discovered devices |
| `Port` | 7765 | **Unused** - the protocol uses fixed TCP 7765 |
| `ScanRate` | 30000 | Points per second (clamped 100..100000) |
| `SimulationMode` | on | No hardware: count points, fake `IsConnected`/`BufferFullness` |
| `DiscoverDevices` | off | Listen for DAC broadcasts (UDP 7654); fills the IP dropdown |
| `LocalIpAddress` | - | Interface used *for discovery only* |
| `LoopLastFrame` | on | Keep rescanning the last frame when the graph pauses |
| `ClearEStop` | off | Toggle to clear an emergency stop on the device |
| `PrintToLog` | off | Verbose connection/queue logging |

## Outputs

| Output | Purpose |
|--------|---------|
| `IsConnected` | TCP connection state (simulated true in simulation mode) |
| `BufferFullness` | DAC point-buffer fill level, as reported by the device |
| `StatusMessage` | Human-readable state (connecting / streaming / warm-up / e-stop) |
| `PointsSent` | Total points streamed since creation |
| `Command` | Evaluation trigger used to drive the update |

The operator also implements `IStatusProvider` - its state shows on the node's status bar in the graph.

## Light engine & safety states

* While the projector's light engine warms up, sends fail with `InvalidOperationException`; the operator waits (up to `MaxWarmupRetries` = 120 x 500 ms) and retries.
* An active emergency stop surfaces as a warning ("E-stop active on device - set 'ClearEStop' to resume"). Toggling `ClearEStop` sends the clear command on the send-loop thread.

## Usage notes

* Test with `SimulationMode` first, then discover, connect, and only then point hardware at anything. See [[Laser Safety]].
* If frames look unstable, the usual culprits are: too few points per frame for the scan rate (use [[LaserOptimizer]] and/or raise point count), `ScanRate` set higher than the scanners can follow, or frame drops from an overloaded queue (check `PrintToLog`).
* Only one operator can own the discovery port; a second instance reuses the shared discovery results automatically.
