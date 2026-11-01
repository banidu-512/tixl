# Live collaboration

Several TiXL editors can work on the same graph at the same time. Everyone sees the other participants' cursors, selections and "editing" badges, and every graph edit is replayed on all machines.

Sessions are configured in **Settings → Collaboration**. The feature is experimental.

## Hosting and joining

- **LAN**: one editor hosts on a configurable TCP port (44700 by default); the others find it through LAN discovery or join with a direct IP address.
- **Shared project**: **Share project** picks which project the session works on. **Current project** follows whichever project is focused; choosing another one opens it when hosting starts. The **Projects** window offers the same operation per project in its context menu.
- **Session ID**: **Start Session** registers a room on the relay and shows a six-character session ID with **Copy ID** and **Copy Invite**. Everyone else types the ID under **Join** and presses **Join Session**, or copies the invite and presses **Paste Invite and Join**. No port forwarding is needed. The relay is the `CollabRelay` project in this repository (`dotnet run --project CollabRelay`, TCP 44710 by default); it must be reachable by everyone, and hosts and joiners must use the same one. It defaults to the relay built into the editor; a different one can be entered under **Advanced → Relay server**.
- **Advanced**: the relay address, the LAN and direct-connection options (**Host on LAN**, a direct address, LAN discovery) and the network interface. Online sessions can also use a direct address (an open port, or a VPN such as Tailscale).
- **Projects window**: running LAN sessions are listed next to the projects, and a project's context menu offers **Host Collaboration Session**.
- **Password**: a session can be password protected. Authentication is a PBKDF2/HMAC challenge-response; the password itself never crosses the network. Joining a password-protected session without a saved password, or with a wrong one, opens a small **Session Password** window to enter it.
- **Identity**: one button, **Log In with Discord**, which opens discord.com in the browser and uses the verified Discord profile (name and avatar) as your collaboration identity. Before the first login your OS user name is shown. The login uses OAuth2 with PKCE and needs no client secret. A Discord application is registered once: TiXL ships the id of its own application (TixlCollab), so only the button is needed; its exact redirect URI `http://127.0.0.1:38270/callback/` must be listed under OAuth2 → Redirects in the Discord Developer Portal. A different application id can be put in the `CollabDiscordClientId` entry of the user settings file. The account must be a member of the TiXL Discord server (https://discord.gg/YmSyQdeH3S): the login requests the `identify` and `guilds` scopes, resolves the invite to its server id and compares it with the account's server list. An account that is not in the server is told to join and can retry with the button.
- **Network**: the **Interface** dropdown pins hosting, LAN discovery and presence to one adapter. Keep **Automatic** unless the machine also has Wi-Fi, VPN or Hyper-V adapters and the show runs on a specific Ethernet port.

On join a client downloads the host's project if it doesn't have it, restarts into it and rejoins automatically. This also works through a relay: the joiner remembers the relay address and session ID, so the restart returns to the same room.

## Peer-to-peer sessions

Relay sessions do not keep all traffic on the relay. After a participant authenticates, both machines try a direct **WebRTC** connection and exchange only the connection setup (SDP and ICE candidates) through the relay; the session itself then runs over an encrypted, reliable data channel. The relay connection stays open as a fallback, so a participant that cannot reach the host directly — or a direct path that drops — keeps working without leaving the session.

- **Participants** shows `P2P` next to a participant once its direct path is up. During setup it shows `P2P...`; nothing is shown while it runs over the relay.
- **Advanced → Peer-to-Peer** controls the feature: **Use peer-to-peer connections** (on by default), **Share preview with participants** and **STUN/TURN servers**. Relay-address and LAN settings are independent of it.
- **STUN/TURN**: the default uses a public STUN server. A TURN server is only needed when both peers are behind symmetric NATs; enter one or more comma-separated URLs. Leaving the field empty uses the default STUN server.
- **Host preview**: the host's rendered output (the pinned output window, capped at 1280×720) is encoded as H.264 and shown in the **Collab** window as **Host Preview**. **Share preview with participants** turns it off for participants that join afterwards. The preview needs the Video operator package, which ships with the editor.
- **Platforms**: the direct path needs a bundled libdatachannel binary. Windows (x64), Linux (x64) and macOS (x64) have one; on other platforms the session simply stays on the relay.

## What is synchronized

- Adding and deleting operators, connections, parameter changes, renames and node moves.
- **Undo and redo are session-wide while connected**: Ctrl+Z reverts the last edit of any participant. The host keeps the shared history.
- **Late joiners** receive a snapshot of every composition the session has touched, replacing their local state in those compositions.
- **Soft parameter locks**: a parameter someone is editing is shown disabled for everyone else until it is released.

The **Collaboration** window shows the participants and a session chat.

## Managing participants

**Settings → Collaboration** lists the session as a table: the participant's name (with `host` and `you` markers), the link it uses (`P2P`, `relay` or `direct`), the round-trip time of its heartbeat and whether it is editing or idle.

The host can press **Kick** on a participant and confirm. The participant is told to leave, its connection is closed, and it disappears from everyone's list. A kick is not a ban: the participant can join again unless the session uses a password (or another gate). Clients that predate this feature ignore the message; a direct connection is still closed, while a relayed one may linger until its own timeout.

The **RTT** column is measured per connection: the host sees every participant's latency, while a participant in a relay session only sees its own link to the host.

## Platforms

Collaboration uses plain TCP/UDP sockets and runs on Windows, Linux and macOS. Paths are compared case-sensitively on Linux and macOS and case-insensitively on Windows.
