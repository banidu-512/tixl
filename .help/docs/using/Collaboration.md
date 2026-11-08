# Live collaboration

Several TiXL editors can work on the same graph at the same time. Everyone sees the other participants' cursors, selections and "editing" badges, and every graph edit is replayed on all machines.

Sessions are configured in **Settings → Collaboration**. The feature is experimental.

## Hosting and joining

The primary flow is a relay room: **Start Session** gives you a session ID, and everyone joins with that ID — no port forwarding, works across networks.

1. **Log In with Discord** (optional but recommended). TiXL opens discord.com in your browser for the OAuth2 login; the browser returns to TiXL automatically. Your display name and avatar come from Discord. The account must be a member of the TiXL Discord server — the login checks membership and points you at the invite if not. Without a Discord login a local nickname is used.
2. **Start Session** registers a room on the relay and shows the **session ID**. Copy it (or **Copy Invite**, which carries the relay address and the ID together) and share it.
3. Others enter the **Session ID** under Join and press **Join Session** — or copy the whole invite and use **Paste Invite and Join**, which adopts the relay address from the invite.

The relay is the `CollabRelay` project in this repository (`dotnet run --project CollabRelay`). Its address defaults to the show LAN's relay; a custom relay is configured under **Advanced → Relay**. The relay only carries the session's traffic while it runs and never stores graph data.

## LAN and direct sessions

- **LAN**: **Advanced → Host on LAN** hosts on a configurable TCP port (44700 by default); the others find it through LAN discovery or join with a direct address under **Advanced → Join by address**.
- **Projects window**: running LAN sessions are listed next to the projects, and a project's context menu offers **Host Collaboration Session**.
- **Password**: a session can be password protected. Authentication is a PBKDF2/HMAC challenge-response; the password itself never crosses the network.

## Identity

Discord login uses the OAuth2 browser flow (PKCE, a public Discord application, no client secret involved). The identity persists in this machine's user settings until you log out. A local nickname is the fallback.

On join a client downloads the host's project if it doesn't have it, restarts into it and rejoins automatically.

## What is synchronized

- Adding and deleting operators, connections, parameter changes, renames and node moves.
- **Undo and redo are session-wide while connected**: Ctrl+Z reverts the last edit of any participant. The host keeps the shared history.
- **Late joiners** receive a snapshot of every composition the session has touched, replacing their local state in those compositions.
- **Soft parameter locks**: a parameter someone is editing is shown disabled for everyone else until it is released.

The **Collaboration** window shows the participants and a session chat.

## Platforms

Collaboration uses plain TCP/UDP sockets and runs on Windows, Linux and macOS. Paths are compared case-sensitively on Linux and macOS and case-insensitively on Windows.
