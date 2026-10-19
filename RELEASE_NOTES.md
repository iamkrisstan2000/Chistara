# Chistara 1.0.0

Chistara 1.0.0 is the first production release: a portable Windows 11 music player and internet FM radio with a Fluent Acrylic interface.

## Release highlights

- Local music library with metadata, embedded/online artwork, folders managed from Settings, device discovery, unified search, history, Shuffle, Repeat Off/All/One, seek, volume, and mute.
- Local and Worldwide FM discovery through the public Radio Browser directory, stream playlist resolution, fallback playback, recommendations, search, history, and station artwork.
- Four real-audio-reactive visualizers: Waves, Modern Waves, Topographic, and Simple Spectrum.
- Three-band EQ, original percentage-based Reverb, and continuous 0.5–2-second Crossfade for local music.
- Dark, Light, and System themes; selectable saturated accents; animated RGB accent; native Windows 11 Acrylic/Mica; taskbar-safe per-monitor maximization.
- Official supplied Chistara icon used consistently by the executable, title bar, and About page.
- Privacy-first local storage with no accounts, advertisements, analytics, or telemetry.

## Production hardening

- Recycling/pixel virtualization for long Music and FM lists and a bounded decoded-artwork cache.
- Visible-only, minimize-aware visualizer rendering with reused drawing/FFT buffers and precomputed analysis constants.
- Cached-library validation and drive discovery run away from the UI thread; scan progress is throttled; Worldwide FM pages append incrementally.
- Removed unused private API, model data, XAML resources, generated names, and redundant imports without changing approved behavior.
- Deterministic warning-free Release builds, nullable analysis, .NET analyzers, package auditing, and a reproducible NuGet lock file.

## Open-source release

- Licensed under the GNU General Public License v3.0 only (`GPL-3.0-only`).
- Includes Git ignore/attributes rules, coding conventions, contribution and security guidance, and a Windows GitHub Actions build workflow.
- Friendly optional-support wording is included in the README; no payment or donation is required for any feature.

The application is self-contained for Windows x64 and does not require a separate .NET installation.
