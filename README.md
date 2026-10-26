# Chistara

[![Version](https://img.shields.io/badge/version-1.1.0-2563EB.svg)](CHANGELOG.md)
[![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)](#requirements)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![GPLv3](https://img.shields.io/badge/license-GPLv3-2EA44F.svg)](LICENSE)

Chistara is a lightweight, privacy-friendly music player and worldwide internet FM radio for Windows 11. It combines a Fluent Acrylic interface, local music playback, public radio discovery, artwork, sound effects, and live audio visualizers in one portable app.

## Download

Download `Chistara.exe` from the GitHub **Releases** page. It is a self-contained Windows x64 executable: no installer, administrator access, or separate .NET installation is required.

### Requirements

- Windows 11, 64-bit
- An audio output device
- Internet access only for FM radio, approximate Local FM discovery, and optional album-art lookup

The executable is not code-signed, so Windows SmartScreen may display an “unrecognized app” notice. Verify the published SHA-256 checksum before running it.

## Highlights

- Local MP3, WAV, WMA, AAC, M4A, FLAC, OGG, and AIFF playback
- Artists, albums, all songs, device scanning, history, artwork, seek, shuffle, and repeat
- Local and Worldwide FM through the community Radio Browser directory
- Unified, debounced search for songs, artists, albums, and stations
- Persistent Music sorting: Alphabetical, Recently Played, Date Added, and Date Modified
- Persistent FM sorting: Alphabetical, Recently Played, and Date Added
- Five live visualizers: Waves, Modern Waves, Topographic, Simple Spectrum, and CD Days
- Three-band equalizer, percentage-based Reverb, and continuous 0.5–2 second Crossfade
- Dark, Light, and System themes with selectable accents and animated RGB
- Resizable 1024 × 600 minimum window with taskbar-safe maximization
- Portable local settings with no accounts, advertisements, analytics, or telemetry

Search supports `artist:`, `album:`, `song:`, `music:`, `fm:`, and `radio:` prefixes.

## Privacy and storage

Chistara stores settings, history, library data, radio cache, and artwork under:

```text
%LOCALAPPDATA%\Chistara
```

It never renames, moves, edits, or deletes music files. Network requests are limited to IPWhoIs for approximate Local FM location, Radio Browser for stations, MusicBrainz and Cover Art Archive for missing artwork, and the selected broadcaster for playback.

## Build from source

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet restore Chistara.csproj -r win-x64 --locked-mode
dotnet build Chistara.csproj -c Release -r win-x64 --self-contained true --no-restore
dotnet publish Chistara.csproj -p:PublishProfile=Properties/PublishProfiles/Windows-x64.pubxml --no-restore
```

See [CHANGELOG.md](CHANGELOG.md) for release history, [DESIGN.md](DESIGN.md) for implementation notes, and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for third-party components and online services.

## Support Chistara

Chistara is free software and every feature is available without payment. If it makes your listening a little better, you can support continued development with a coffee. Support is always appreciated and never required.

[![Support Chistara on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N7D7292WUD)

You can also help by starring the repository, sharing the app, reporting clear bugs, or contributing improvements.

## Credits and license

Made by **KnightWin** with help from AI · October 2026.

Copyright © 2026 KnightWin.

Chistara is licensed under the **GNU General Public License v3.0 only** (`GPL-3.0-only`). See [LICENSE](LICENSE). Security reports should follow [SECURITY.md](SECURITY.md).
