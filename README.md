# TiXL (Tooll3) - A realtime animation toolkit

[![Discord](https://img.shields.io/discord/823853172619083816.svg?style=for-the-badge)](https://discord.gg/YmSyQdeH3S)
[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg?style=for-the-badge)](https://github.com/tixl3d/tixl/blob/main/LICENSE.txt)
[![Download](https://img.shields.io/badge/Download_TiXL_v4.1.1.1-FF3A9E?style=for-the-badge)](https://github.com/tixl3d/tixl/releases/download/v4.1.1/Tixl-v4.1.1.1.exe)

[![tooll-screenshot](https://user-images.githubusercontent.com/1732545/173256422-a4ef9894-d954-4bc3-8c24-000bfbe1c3ad.png)](https://www.youtube.com/watch?v=PrxhwOC9hLw "Tooll3 - A quick overview")

## Overview
TiXL is an open source software to create realtime motion graphics. We are targeting the sweet spot between real-time rendering, graph-based procedural content generation and linear keyframe animation and editing. This combination allows… 
- artists to build audio reactive vj content
- use advanced interfaces for exploring parameters
- or to combine keyframe animation with automation

Technical artists can also dive deeper and use TiXL for advanced development of fragment or compute shaders or to add input from midi controllers and sensors or sources like OSC or Spout. 

We strongly believe in usability and intuitive and beautiful interface design. That's why we experiment with different approaches before striking the right
balance between usability and powerful flexibility. Currently TiXL version 4 is an ongoing development. It's stable enough to produce high-end visuals
create motion graphics use many industry standard features like color correction, scopes and tone mapping, and export small standalone executables.

We hope to bring together a community of artists, developers and creative tinkerers to drive the design and development of the software as a free open source project.

## Installation
Please follow the [installation guide](https://github.com/tixl3d/tixl/wiki/help.Installation).

## Collaboration (experimental)
This branch adds live multi-user editing of a shared graph. Sessions are configured in **Settings → Collaboration**:

- **LAN**: one instance hosts on a configurable port; others join via network discovery or a direct IP. Optionally password protected (PBKDF2/HMAC challenge-response; the password never crosses the wire).
- **Online**: either a direct address (open port or VPN such as Tailscale), or a room on a relay via the included `CollabRelay` server project (`dotnet run --project CollabRelay` — the host shares the generated room code, joiners enter it; no port forwarding needed).
- **Identity**: local nickname, or Discord login (OAuth2, configured with a client id/secret from the Discord Developer Portal).

Synced edits: adding/deleting operators, connections, parameter value changes, renames and node moves. Participants see each other's cursors and selection outlines in the graph. Late joiners receive a replay of the session's edit history, so everyone should open the same project before joining. Undo histories stay local on purpose — remote commands are applied directly and never enter your undo stack.

## Get Help
To get started read the [documentation](https://github.com/tixl3d/tixl/wiki) or watch [tutorial videos](https://www.youtube.com/watch?v=eH2E02U6P5Q&list=PLj-rnPROvbn3LigXGRSDvmLtgTwmNHcQs&index=4).

If you have questions or feedback, please join us on Discord: https://discord.gg/YmSyQdeH3S 



