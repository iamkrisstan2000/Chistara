# Contributing to Chistara

Thank you for helping improve Chistara. Small, focused changes are easiest to review and safest for a media player.

## Before opening an issue

- Search existing issues first.
- Use a clear title and list the exact Windows version, Chistara version, and steps to reproduce.
- Explain what you expected and what happened instead.
- Remove usernames, local paths, IP addresses, station tokens, and other private information from screenshots or logs.
- For a security issue, follow [SECURITY.md](SECURITY.md) instead of filing a public report.

## Development requirements

- Windows 11 x64
- .NET 8 SDK
- Git
- An audio output device for playback testing

Restore and build from the repository root:

```powershell
dotnet restore Chistara.csproj -r win-x64 --locked-mode
dotnet build Chistara.csproj -c Release -r win-x64 --self-contained true --no-restore
```

Create the portable executable with the checked-in publish profile:

```powershell
dotnet publish Chistara.csproj -p:PublishProfile=Properties/PublishProfiles/Windows-x64.pubxml --no-restore
```

## Pull requests

1. Create a topic branch from `main`.
2. Keep the change focused; avoid unrelated formatting or generated files.
3. Preserve the Fluent/Acrylic visual language and existing playback behavior unless the pull request explicitly proposes a change.
4. Do not commit `bin`, `obj`, packages, logs, user settings, music, cached artwork, executables, or ZIP archives.
5. Build with zero warnings and test the affected flow on Windows 11.
6. Update README or release documentation when behavior changes.
7. Describe the testing performed in the pull request.

## Code conventions

- Nullable reference types and .NET analyzers remain enabled.
- Treat compiler and analyzer warnings as errors.
- Prefer cancellation-aware asynchronous I/O and keep blocking work off the WPF dispatcher.
- Keep visualizer/audio hot paths allocation-conscious.
- Preserve list virtualization and bounded caches.
- Never collect analytics or telemetry without an explicit project decision and prominent documentation.

## Licensing of contributions

By submitting a contribution, you agree that it may be distributed under the repository’s [GNU General Public License v3.0 only](LICENSE). Only submit work you have the right to license.
