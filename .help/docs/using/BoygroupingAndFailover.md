# Boygrouping and show failover

Two features for running a show on more than one computer. Both are experimental and use plain sockets, so they work on Windows, Linux and macOS.

## Boygrouping

Boygrouping distributes a show to render nodes. One TiXL instance is the server; the others join it as clients, either through LAN discovery or with a direct address. A server can hold several named groups.

- **Deploy & Sync** pushes the changed project files (hash-diffed, chunked and verified), the Output Setup and the live graph state to every client. Clients restart into the new state and rejoin automatically.
- The server drives playback and the clients follow its clock.
- In the [Output Setup](OutputSetup.md) every output has a **Render on machine** assignment, so each node presents only the outputs assigned to it. `(all machines)` keeps the default behavior. The machine-specific bindings in `outputs.machine.json` are never deployed.

Configure and manage servers in **Settings → Boygrouping**: node name, port and password, starting a server or joining one (direct address or LAN discovery), groups, deploy options, and the server-side clients and playback controls.

## Show failover

Show failover keeps a redundant pair of machines streaming. Both run the same composition; only the **Active** one sends Art-Net, sACN and streams. The **Standby** renders silently and takes over within `timeout factor × heartbeat` (about 1.5 s with the defaults) when the active machine stops sending heartbeats.

- Heartbeats are multicast on every real network interface, so a pair can be rehearsed on a single computer.
- The higher configured priority wins. Preemption, where a returning higher-priority machine takes the role back, is optional.
- A role change includes a short mutual mute so both machines never stream at the same time.
- `[ArtnetOutput]` and `[SacnOutput]` report `Standby` in their status while they are muted.

Configure it in **Settings → Show Failover**: show id (machines with the same id form a pair), priority, heartbeat interval, timeout factor and preemption.
