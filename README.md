# Open Esport FPS

[🇩🇪 Deutsch](README-lang/deutsch.md) [🇳🇱 Nederlands](README-lang/nederlands.md) [🇫🇷 Français](README-lang/français.md) [🇧🇬 Български](README-lang/български.md) [🇭🇷 Hrvatski](README-lang/hrvatski.md) [🇨🇿 Čeština](README-lang/čeština.md) [🇩🇰 Dansk](README-lang/dansk.md) [🇪🇪 Eesti](README-lang/eesti.md) [🇬🇷 Ελληνικά](README-lang/ελληνικά.md) [🇭🇺 Magyar](README-lang/magyar.md) [🇮🇪 Gaeilge](README-lang/gaeilge.md) [🇮🇹 Italiano](README-lang/italiano.md) [🇱🇻 Latviešu](README-lang/latviešu.md) [🇱🇹 Lietuvių](README-lang/lietuvių.md) [🇲🇹 Malti](README-lang/malti.md) [🇵🇱 Polski](README-lang/polski.md) [🇵🇹 Português](README-lang/português.md) [🇷🇴 Română](README-lang/română.md) [🇸🇰 Slovenčina](README-lang/slovenčina.md) [🇸🇮 Slovenščina](README-lang/slovenščina.md) [🇪🇸 Español](README-lang/español.md) [🇸🇪 Svenska](README-lang/svenska.md) [🇸🇦 العربية](README-lang/العربية.md)

## Description

**Open Esport** is an open source competitive FPS project developed with the **Godot 4** engine. It aims to provide a modular, extensible, and easily deployable foundation for creating modern esport experiences.

The primary goal is to offer a **self-hosted** and **autonomous infrastructure**, independent from traditional publishers, in order to bypass limitations or policy changes imposed for commercial purposes.  
The project is designed to be **compatible with modern deployment environments** (Docker, Kubernetes, Azure, AWS, ...), while remaining lightweight and easy to host locally.

This solution is intended for developers, tournament organizers, open source communities, and anyone wishing to create, manage, and evolve a free, transparent, and sovereign esport platform.

## Current Project Status
The game is playable online with an authoritative dedicated server:
- **6 game modes**: Battle Royale (plane drop, shrinking zone, loot), Deathmatch, Team Deathmatch, Capture the Flag, Sabotage (rounds, economy, shop, bomb) and Control
- **Online play**: 2 to 100 players, teams of 2 to 5, bots filling empty slots, matchmaking that launches one Docker game server per match
- **Accounts**: registration/login, up to 12 characters per account, overall and per-mode rankings with ratings and statistics (profile page)
- **Combat**: Apex Legends-inspired weapons, shields, healing, grenades, optics, backpacks, downed/revive system in teams
- **Bots**: cover and peek-and-hide behaviour, team orders through pings (attack / defend), loot sharing with human teammates
- **Character creator** (body, face, clothes) and 24 languages, all texts translated

## Features
- Modular menu system and flexible scenes
- Authoritative .NET server (LiteNetLib) with client-side prediction and interpolation
- Matchmaker and accounts services, deployable with Docker Compose (see [netcode/README.md](netcode/README.md))
- Tunable balance: weapons, grenades and modes in `netcode/balance.json`
- Ability-based character controller (walking, running, jumping, crouching, sliding, swimming, air control, slopes)
- Centralized logging and an automated test suite for the netcode (`netcode/OpenEsport.Tests`)

## Technologies Used
- Godot Engine
- C#
- .NET
- Plugins:
  - [Character Controller](https://github.com/expressobits/character-controller) – Modular character controller
  - [AmbientCG](https://github.com/mohsenph69/godot-ambientcg) – PBR texture importer
  - [FPS Hands](https://codeberg.org/Bytez/godot-fps-hands) – FPS hands system

## Project Structure
```
open-esport/
├── scripts/                  # Source code
│   ├── managers/             # Global managers
│   ├── modules/              # Functional modules
│   ├── structures/           # Data structures
│   ├── ui/                   # User interface components
│   └── utils/                # Utilities
├── netcode/                  # Dedicated server, matchmaker, accounts service, tests (.NET 8)
├── localization/             # Translations (one .lang file per language)
├── maps/                     # Map data
├── scenes/                   # Godot scenes
├── assets/                   # Assets (images, sounds, etc.)
├── configuration/            # Configuration files
└── addons/                   # Godot plugins
    ├── character-controller/ # Character controller
    ├── ambientcg/            # PBR texture importer
    └── fps-hands/            # FPS hands system
```

## Architecture
The project follows a modular architecture with:
- Managers for global functionalities
- An event-based communication system
- A clear separation of concerns
- Centralized log management
- A character system based on abilities:
  - Walking
  - Running
  - Jumping
  - Crouching
  - Flying
  - Swimming
  - Air control
  - Slope handling

## Prerequisites
- Godot Engine 4.7 (.NET / Mono edition)
- .NET SDK 8
- Docker (to host the servers)
- Visual Studio or VS Code (recommended)

## Installation
1. Clone the repository:
```bash
git clone https://github.com/efs91/open-esport.git
```
2. Open the project in Godot Engine
3. Ensure that all addons are correctly installed

## Release
Latest release (v0.6) for Windows 64-bit and Linux x86_64:
- [Download](https://github.com/efs91/open-esport/releases/latest) — Windows: unzip and run `open-esport.exe` ; Linux: `tar xzf open-esport-linux-v0.6.tar.gz` then `./open-esport.x86_64`

The client connects to the public server set in `data/network.cfg` (`host` / `port`).

**Automatic updates** (from v0.5): at startup the exported game asks GitHub for the latest release of `efs91/open-esport`. If its tag is newer than `GameVersion.Current` (`scripts/menus/UpdateScreen.cs`), it downloads the archive for its platform (Windows: the asset whose name contains `win` and ends with `.zip`; Linux: `linux` + `.tar.gz`) into `_update/` next to the executable, quits, and a small script (`update.cmd` / `update.sh`) extracts it over the installation and restarts the game. If the folder is not writable or the download fails, the player gets a link to the release page and can keep playing. Start with `--no-update` to skip the check.

To publish a release: bump `GameVersion.Current` (and the protocol in `Messages.cs` if the netcode changed), export both presets, package `open-esport-win64-vX.Y.zip` and `open-esport-linux-vX.Y.tar.gz` with the files at the root of the archive, and create the GitHub release with tag `X.Y`.

## Hosting your own server
The server, matchmaker and accounts service run with Docker Compose from the `netcode/` folder. See [netcode/README.md](netcode/README.md).

## Contribution
Contributions are welcome! Please follow these steps:
1. Fork the project
2. Create a branch for your feature
3. Commit your changes
4. Push to your branch
5. Open a Pull Request

## License
This project is licensed under the GNU General Public License v3.0 (GPL-3.0). See the [LICENSE](LICENSE) file for details.

This license ensures that:
- The source code is freely accessible
- Modifications must be shared under the same license
- Users have the right to use, modify, and distribute the code
- Changes must be documented

## Credits
- [Character Controller](https://github.com/expressobits/character-controller) by Rafael Correa
- [AmbientCG](https://github.com/mohsenph69/godot-ambientcg) by mohsenph69
- [FPS Hands](https://codeberg.org/Bytez/godot-fps-hands) by Bytez

## Support
For any questions or issues, please open an issue in the GitHub repository.
