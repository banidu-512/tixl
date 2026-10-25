# Boygrouping and show failover

Two features for running a show on more than one computer. Both are experimental and use plain sockets, so they work on Windows, Linux and macOS.

## Boygrouping

Boygrouping distributes a show to render nodes. Enable it in **Settings → Boygrouping** and pick a role: **Master** on the machine that edits the show, **Slave** on every render node. Enabled instances announce themselves on the LAN — there is no address, password or scan step. The master lists the TiXL instances it finds and **Add** invites one to connect.

- **Deploy & Sync** sends the master's Output Setup files to the added render nodes. Project files are not transferred and clients do not restart: each slave writes the setups into its own copy of the project and switches to the master's active setup right away.
- Setup edits made on the master are forwarded as they are saved, so slaves follow a venue change live.
- The master drives playback and the slaves follow its clock: it sends a playback position for a future instant on its own clock, each slave measures the clock offset (NTP-style, best round trip) and switches when its monotonic clock reaches that instant. Timeline phase therefore does not depend on network jitter, and the time cursor advances every frame.
- Each slave presents the outputs connected in its own **[Output Setup](OutputSetup.md) → Local Bindings**. On a master, the connected nodes' displays are listed in that same column under the node's name: drop an output on one to route it on that machine. The master sends the request, the render node binds it in its own `outputs.machine.json` and reports back, so the edge, the bound state and **Unbind** always reflect the client's real state. Only outputs and setups are shared; `outputs.machine.json` is never overwritten by a deploy.

The master shows the added nodes, their local output bindings and their deployment status. A slave keeps its role and stays discoverable after a master disappears, so another master can add it without restarting the editor.

The **Network** section of the Collaboration, Boygrouping and Failover settings is shared: pick the show network's Ethernet adapter there when the machine also has Wi-Fi, VPN or Hyper-V adapters. **Automatic** keeps the previous behavior. A pinned adapter takes effect the next time boygrouping is enabled; failover rebinds immediately.

## Show failover

Show failover keeps a redundant pair of machines streaming. Both run the same composition; only the **Active** one sends Art-Net, sACN and streams. The **Standby** renders silently and takes over within `timeout factor × heartbeat` (about 1.5 s with the defaults) when the active machine stops sending heartbeats.

- Heartbeats are multicast on every real network interface (or only on the adapter selected under **Network**), so a pair can be rehearsed on a single computer.
- The higher configured priority wins. Preemption, where a returning higher-priority machine takes the role back, is optional.
- A role change includes a short mutual mute so both machines never stream at the same time.
- `[ArtnetOutput]` and `[SacnOutput]` report `Standby` in their status while they are muted.

Configure it in **Settings → Show Failover**: show id (machines with the same id form a pair), priority, heartbeat interval, timeout factor, preemption and network interface.
