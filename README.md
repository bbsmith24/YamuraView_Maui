# YamuraView

A .NET 10 MAUI application for viewing and analyzing logged run data — a synchronized
**strip chart**, GPS **track map**, and **traction (G-G) circle** across one or more runs,
with per-channel display filtering and cross-chart cursor tracking. Runs on Windows,
Mac (Mac Catalyst), iOS, and Android.

## Building

- **Mac (Mac Catalyst) and iOS** — see **[BUILDING-iOS-Mac.md](BUILDING-iOS-Mac.md)**:
  prerequisites, VS Code setup and one-click build/run/publish tasks, debug builds,
  Pair-to-Mac from Windows, and release/distribution (App Store, TestFlight, notarized Mac).
- **Windows** — open [YamuraView/YamuraView.sln](YamuraView/YamuraView.sln) in Visual Studio,
  or from the CLI:
  ```
  dotnet build YamuraView/YamuraView.csproj -f net10.0-windows10.0.19041.0
  ```

## Project layout

- `YamuraView/` — the MAUI app: pages, chart drawables (strip chart, track map, traction
  circle), and settings.
- `YamuraView.Core/` — data model and processing: log-file parsing, run alignment, delta-time,
  and channel filters.
