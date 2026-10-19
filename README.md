# Chistara

Chistara is a lightweight, privacy-first music player and internet FM radio designed for Windows 11. Its layout follows the supplied blank canvas: restrained near-black layers, a compact header, contextual left rail, persistent transport bar, subtle edge accents, and a native Acrylic backdrop. Its official icon pairs a rounded deep-navy tile with cyan-to-violet equalizer bars and a crisp white music note, matching the supplied artwork exactly.

## Screenshots 

<img width="1919" height="1079" alt="Screenshot 2026-10-19 184658" src="https://github.com/user-attachments/assets/434b4bda-94c5-47d1-93d8-80b7e5612490" />
<img width="1919" height="1079" alt="Screenshot 2026-10-19 184644" src="https://github.com/user-attachments/assets/cb711bc1-d2d4-4629-bcc5-c7cbef070ae9" />
<img width="1919" height="1079" alt="Screenshot 2026-10-19 184628" src="https://github.com/user-attachments/assets/89993248-83d5-4e60-a2a5-2f138d3a62d1" />
<img width="1919" height="1079" alt="Screenshot 2026-10-19 184621" src="https://github.com/user-attachments/assets/e7b4b3b7-cca0-4893-af92-ab1edf34f0b3" />
<img width="1919" height="1079" alt="Screenshot 2026-10-19 184613" src="https://github.com/user-attachments/assets/37928167-7d0a-4e11-a7ad-e430516329ab" />
<img width="1919" height="1079" alt="Screenshot 2026-10-19 184600" src="https://github.com/user-attachments/assets/2f367434-f594-4153-ae08-12ba574043ba" />


This repository contains the stable **Chistara 1.0.0** release by **Mr KNIGHT**.

## Ready-to-run build

Download `Chistara.exe` from the repository’s **Releases** page. It is a self-contained Windows x64 application and does not need an installer, administrator access, or a separate .NET installation.

### Requirements

- Windows 11, 64-bit (x64)
- Audio output device
- Internet access for internet FM, approximate Local FM discovery, and optional automatic album-art lookup

Chistara is not code-signed. Windows SmartScreen may show an “unrecognized app” message for this custom build. Verify the SHA-256 file supplied beside the executable, then choose **More info → Run anyway** only if it matches.

## What works

- **Home:** separate Overview and Recently Played destinations, a fresh randomized greeting on each visit, Local and Worldwide FM recommendation cards, and a rotating now-playing disc. Recent music and FM history appears as a roomier responsive grid with at most 12 cards.
- **Unified search:** one top-bar search for songs, artists, albums, and FM stations. Local results are instant; targeted Radio Browser searches run after a short debounce. Search supports `artist:`, `album:`, `song:`, `music:`, `fm:`, and `radio:` scopes.
- **Music:** artists, albums, all songs, metadata, device scanning, cached album art, seek, previous/next, volume, mute, resume/pause, persistent shuffle, and explicit Repeat off/all/one modes. Shuffle and repeat appear only for local-music playback and stay hidden for FM streams. Use the left-side row button or double-click any song row to play it. Media folders are managed centrally in Settings › Player.
- **Audio visualizer:** Music and FM place the selected list on the right and a large visualizer on the left. All Songs and FM now share a long visualizer plus a compact 300-DIP right list. Their denser 54-DIP rows, 12-point titles, smaller metadata, reclaimed columns, and full-title tooltips expose substantially more of each song or station name. Artists, Albums, and Devices retain equal visualizer/content surfaces. Artist and album collections adapt from one to four evenly filled card columns; names receive a consistent two-line slot with a clean ellipsis and full-name tooltip. Lists sit directly on the page without a rounded outer shell. The selected visualization title floats inside the artwork with a small readability shadow, while the animation extends behind it to every card edge; hovering reveals its compact chooser. After opening or changing it, moving the pointer away restores the clean text-only title after a 1.5-second grace period instead of leaving focused chooser chrome behind. Choose Waves (PS3-inspired), Modern Waves (PSP-inspired), Topographic, or Simple Spectrum. Every mode uses the active accent—including the animated RGB gradient—and reacts to real post-effects audio with a Hann-windowed FFT, waveform energy, bass analysis, and beat-onset tracking. The shared choice is remembered. MediaElement-only FM fallbacks use temporary WASAPI output analysis so they remain sound-reactive.
- **Artwork:** embedded music artwork is preferred. If it is absent, Chistara searches MusicBrainz and retrieves available cover images from the Cover Art Archive at a rate-conscious pace, then caches them locally. Radio Browser poster/logo URLs are shown automatically throughout station lists, search, recommendations, history, and now-playing surfaces when available. Music covers, FM artwork, and accent-aware fallbacks use explicit circular geometry. Artist/Album cards clip the complete artwork composition—including their offset highlight—to one 64 px ellipse, so no rectangular or sharp right edge can escape the round frame.
- **Formats:** MP3, WAV, WMA, AAC, M4A, FLAC, OGG, AIFF/AIF. Playback of Media Foundation formats uses the codecs included with Windows 11.
- **Local FM:** uses approximate IP location to prioritize geotagged stations nearby, then includes every healthy public station available for the detected country.
- **FM playback:** resolves common M3U/PLS playlists, tries NAudio/Windows Media Foundation first, falls back to WPF media playback, and can retry a station's original directory URL when its resolved URL fails.
- **Worldwide FM:** scans the healthy public Radio Browser catalog in pages and displays stations incrementally. Normal mode scans the complete available catalog; Lite Mode has a documented 5,000-station safety limit.
- **Appearance:** neutral Dark, Light, and System modes; System, red, pink, matcha, purple, cyan, lime, and a subtly animated RGB-gradient accent. Accent changes update applicable surfaces immediately. Windows-derived System accents use the same visibility floor in Light and Dark modes while preserving their hue and saturation; an unusably near-black neutral DWM value falls back to recognizable Windows blue. The colorized DWM window outline remains suppressed. Custom-chrome maximization uses the active monitor’s native working area, keeping the taskbar visible and every edge inside the screen at per-monitor DPI. Home, Music, FM, and Settings share compact accented hover/selected boxes. Every contextual destination in the collapsible left pane uses the same toggle-backed checked state, so the active accent-filled pill stays highlighted in both expanded and icon-only modes until another destination is selected. The unified search remains mathematically centered in the top bar. The title/navigation area and persistent player are independently layered over Acrylic at 5% translucency in Dark mode and 2% in Light mode. The top area uses a one-sided neutral gradient that fades only downward into content, matching the supplied mockup without casting any shadow into or around the title bar. The persistent player keeps its approved soft upward shadow. The circular transport control retains its animated two-tone RGB wave, pane states keep quiet horizontal insets, Dark drop-down text is pure white, and right-edge scrollbars keep only a small outer gap.
- **Settings:** four separate destinations—Visual, Player, Troubleshoot, and About—rather than one continuous settings page.
- **Audio effects:** real three-band equalizer, the original lightweight percentage-based room reverb, and true two-deck crossfade for local music. Crossfade now uses a smooth, unsnapped 0.5–2 second range with a 2-second maximum.
- **Windows 11 performance:** long Music and FM lists use recycling virtualization, smooth pixel scrolling, and a small page cache. A bounded shared artwork cache avoids repeatedly decoding the same image as rows are recycled. Visualizers render only while their page is visible, pause their work while the window is minimized, reuse drawing and FFT buffers, and cache stable FFT maps/window coefficients. The shell restores before cached-library parsing, library progress is throttled to keep the dispatcher responsive, cache validation and drive discovery run away from the UI thread, and worldwide FM pages append incrementally instead of repeatedly copying the complete catalog.
- **Troubleshooting:** reset app preferences and radio/library caches without touching music files. Lite Mode also caps a library scan at 2,500 tracks, limits one online artwork pass to 30 albums, changes Acrylic to lower-cost Mica, and renders the visualizer at 30 rather than 60 frames per second.

> “FM” means public internet simulcasts. Standard PCs normally have no FM tuner hardware, so Chistara does not emulate or require one.

## Privacy and storage

Chistara has no account system, advertisements, analytics, or telemetry. Local settings, history, radio data, and artwork caches are stored under:

```text
%LOCALAPPDATA%\Chistara
```

On first launch, an existing `%LOCALAPPDATA%\Chistara Music` or `%LOCALAPPDATA%\Auralis` profile is migrated automatically if the Chistara profile does not yet exist. Saved settings, history, library data, radio caches, and cached-artwork references are retained.

The app reads music metadata and audio content. It never renames, moves, edits, or deletes music files. Removing a media folder or restoring settings only changes Chistara configuration. Visualizer samples exist only in a short in-memory ring buffer and are never recorded or saved. When an FM station requires WPF MediaElement fallback, loopback analysis runs only during that playback; it observes the active Windows output mix solely to animate the visualizer.

Network requests are limited to features that require them:

- `ipwho.is` resolves an approximate city/region/country and coordinates for Local FM and Home recommendations. Chistara caches only those useful location fields for 24 hours; it does not persist the returned IP address.
- Radio Browser supplies Home recommendations, station directory records, optional station logo/poster URLs, and public broadcaster stream URLs.
- MusicBrainz and the Cover Art Archive are queried for missing album artwork.
- The selected broadcaster receives the normal connection needed to play its stream.

To fully remove Chistara, close it, delete `Chistara.exe`, and optionally delete `%LOCALAPPDATA%\Chistara`.

## Build from source

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet restore Chistara.csproj -r win-x64 --locked-mode
dotnet build Chistara.csproj -c Release -r win-x64 --self-contained true --no-restore
dotnet publish Chistara.csproj -p:PublishProfile=Properties/PublishProfiles/Windows-x64.pubxml --no-restore
```

The project uses WPF plus the Windows DWM backdrop APIs. It targets `.NET 8 Windows`, is per-monitor-DPI aware, and requests no elevation. Pull requests are validated by the Windows CI workflow in `.github/workflows/build.yml`.

## Contributing

Bug reports, focused pull requests, documentation fixes, and accessibility feedback are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a change. For security-sensitive reports, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Optional support

Chistara is free software and every feature is available without payment. If the project is useful to you, a donation is always appreciated but never expected; no donation unlocks features or changes support priority. Until an official payment link is published by the maintainer, starring the repository, sharing Chistara, reporting a clear bug, or improving the documentation are equally valuable ways to help.

## License

Copyright © 2026 Mr KNIGHT.

Chistara is licensed under the **GNU General Public License v3.0 only** (`GPL-3.0-only`). You may use, study, modify, and redistribute it under the terms in [LICENSE](LICENSE). Distributed modified versions must continue to satisfy the GPL’s source and license obligations.

Third-party components and online services retain their respective licenses and terms; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Online directories

The station directory is provided at runtime by the community-operated [Radio Browser](https://www.radio-browser.info/) service. Music metadata is supplied by [MusicBrainz](https://musicbrainz.org/), and available cover images are retrieved from the [Cover Art Archive](https://coverartarchive.org/). Individual station availability, codecs, metadata, and artwork rights remain under the control of their respective providers and owners.
