# Live collaboration

Several TiXL editors can work on the same graph at the same time. Everyone sees the other participants' cursors, selections and "editing" badges, and every graph edit is replayed on all machines.

Sessions are configured in **Settings → Collaboration**. The feature is experimental.

## Hosting and joining

- **LAN**: one editor hosts on a configurable TCP port (44700 by default); the others find it through LAN discovery or join with a direct IP address.
- **Shared project**: **Share project** picks which project the session works on. **Current project** follows whichever project is focused; choosing another one opens it when hosting starts. The **Projects** window offers the same operation per project in its context menu.
- **Online**: join a direct address (an open port, or a VPN such as Tailscale), or meet in a room on a relay. The relay is the `CollabRelay` project in this repository (`dotnet run --project CollabRelay`). The host shares the generated room code and the others enter it, so no port forwarding is needed.
- **Projects window**: running LAN sessions are listed next to the projects, and a project's context menu offers **Host Collaboration Session**.
- **Password**: a session can be password protected. Authentication is a PBKDF2/HMAC challenge-response; the password itself never crosses the network.
- **Identity**: a local display name, or **Log in with Discord**, which opens discord.com in the browser and uses the verified Discord profile (name and avatar). The login uses OAuth2 with PKCE and needs no client secret. A Discord application is registered once: paste its **Client ID** into the Collaboration settings and add the exact redirect URI shown there (`http://127.0.0.1:38270/callback/`) under OAuth2 → Redirects. Builds that ship a TiXL application id need neither field — only the button. The account must be a member of the TiXL Discord server (https://discord.gg/YmSyQdeH3S): the login requests the `identify` and `guilds` scopes, resolves the invite to its server id and compares it with the account's server list. An account that is not in the server is told to join and can retry with one click.
- **Network**: the **Interface** dropdown pins hosting, LAN discovery and presence to one adapter. Keep **Automatic** unless the machine also has Wi-Fi, VPN or Hyper-V adapters and the show runs on a specific Ethernet port.

On join a client downloads the host's project if it doesn't have it, restarts into it and rejoins automatically.

## What is synchronized

- Adding and deleting operators, connections, parameter changes, renames and node moves.
- **Undo and redo are session-wide while connected**: Ctrl+Z reverts the last edit of any participant. The host keeps the shared history.
- **Late joiners** receive a snapshot of every composition the session has touched, replacing their local state in those compositions.
- **Soft parameter locks**: a parameter someone is editing is shown disabled for everyone else until it is released.

The **Collaboration** window shows the participants and a session chat.

## Platforms

Collaboration uses plain TCP/UDP sockets and runs on Windows, Linux and macOS. Paths are compared case-sensitively on Linux and macOS and case-insensitively on Windows.
