# Boygrouping and show failover

Two features for running a show on more than one computer. Both are experimental and use plain sockets, so they work on Windows, Linux and macOS.

## Boygrouping

Boygrouping distributes a show to render nodes. Enable it in **Settings → Boygrouping** and pick a role: **Master** on the machine that edits the show, **Slave** on every render node. Enabled instances announce themselves on the LAN — there is no address, password or scan step. The master lists the TiXL instances it finds and **Add** invites one to connect.

- **Deploy & Sync** sends the master's Output Setup files to the added render nodes. Project files are not transferred and clients do not restart: each slave writes the setups into its own copy of the project and switches to the master's active setup right away.
- Setup edits made on the master are forwarded as they are saved, so slaves follow a venue change live.
- The master drives playback and the slaves follow its clock: it sends a playback position for a future instant on its own clock, each slave measures the clock offset (NTP-style, best round trip) and switches when its monotonic clock reaches that instant. Timeline phase therefore does not depend on network jitter, and the time cursor advances every frame.
- Every applied anchor is checked against the position the slave's own timer predicted for that instant. The measured correction is shown per node on the master (`sync` in ms) and on the slave, so a node that drifts can be spotted without watching the outputs.
- **Delay −10/+10** on a render node shifts its presentation in time (up to ±500 ms, positive = later). The value is remembered per node and re-sent when it reconnects, which lines up projectors or capture chains that process the signal at different speeds.
- **Play all / Pause all / Stop all** drive the master and the render nodes together. **Stop all** follows the master's timeline settings: with "reset time after playback" enabled the show returns to the bar playback started from, otherwise it stops at the current bar. Playback started from the master's own transport keys is followed by the render nodes just the same.
- Each slave presents the outputs connected in its own **[Output Setup](OutputSetup.md) → Local Bindings**. On a master, the connected nodes' displays are listed in that same column under the node's name: drop an output on one to route it on that machine. The master sends the request, the render node binds it in its own `outputs.machine.json` and reports back, so the edge, the bound state and **Unbind** always reflect the client's real state. Only outputs and setups are shared; `outputs.machine.json` is never overwritten by a deploy.

The master shows the added nodes, their local output bindings and their deployment status, plus per-node diagnostics: round trip, clock offset, measured sync error, time since the last sync, frame rate and version. Heartbeats run at 1 Hz on both sides and a silent peer is dropped after 4 s, so a cable pull or a crashed master is noticed immediately instead of leaving the slave dead-reckoning a show that no longer exists. A slave keeps its role after a master disappears and reconnects on its own: it remembers the master's name and rejoins as soon as that master — or, for a slave that never joined one, the first master that appears — is on the network. Boygrouping therefore always wins over whatever the machine was doing before once it is enabled.

The **Network** section of the Collaboration, Boygrouping and Failover settings is shared: pick the show network's Ethernet adapter there when the machine also has Wi-Fi, VPN or Hyper-V adapters. **Automatic** keeps the previous behavior. A pinned adapter takes effect the next time boygrouping is enabled; failover rebinds immediately.

## Input replication

A render node does not need the controllers or cameras the show was built with. The master forwards its hardware input to the nodes that are connected:

- **MIDI** messages are captured once at the device layer and replayed through the same `[MidiInput]` operators on every node, so device, channel, controller, note and control-range filters produce exactly the master's values. MIDI clock and note/CC traffic are both carried.
- **OSC** packets are forwarded as their original bytes and parsed on the node, so multi-argument messages keep every value and type. Port and address filters behave as they do on the master.
- **[VideoDeviceInput]** streams its captured frame over **OMT**. The master advertises each camera under a stable name (`MACHINENAME (TiXL <node> <device> <hash>)`) and a node presents that stream instead of opening a local camera. The node's camera stays closed even while the stream is still connecting. No video is sent when the project has no video input.
- Input traffic is only captured while at least one render node is connected. Events are batched once per frame; an overflowing queue drops events and reports it in the log rather than growing without bound.
- A slave that truly has its own controllers still uses them while the master sends no MIDI/OSC, and switches to the master's input as soon as replicas arrive.

Video replication requires the OMT codec (`libvmx`). It ships with the Omt package on Windows and Apple Silicon; on other platforms build it from the libvmx sources and place it next to the Omt package (see the `OmtInput` status message). Without it, MIDI and OSC still replicate and the video input falls back to the machine's own camera.

## Show failover

Show failover keeps a redundant pair of machines streaming. Both run the same composition; only the **Active** one sends Art-Net, sACN and streams. The **Standby** renders silently and takes over within `timeout factor × heartbeat` (about 1.5 s with the defaults) when the active machine stops sending heartbeats.

- Heartbeats are multicast on every real network interface (or only on the adapter selected under **Network**), so a pair can be rehearsed on a single computer.
- The higher configured priority wins. Preemption, where a returning higher-priority machine takes the role back, is optional.
- A role change includes a short mutual mute so both machines never stream at the same time.
- `[ArtnetOutput]` and `[SacnOutput]` report `Standby` in their status while they are muted.

Configure it in **Settings → Show Failover**: show id (machines with the same id form a pair), priority, heartbeat interval, timeout factor, preemption and network interface.
