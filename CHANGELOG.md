# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Scenes/Tags/Calendars**: custom calendar engine (replacing `DateTime`,
  with bundled + importable calendars), a campaign-wide Tags system, a
  Scenes library (description, start date, media bundle) with its own
  pausable timeline, and an "activate Scene on fire" trigger option, now
  wired into the UI. Breaking, unmigrated save-format change.
- **Characters tab**: NPC/PC library with portrait, map token, GM notes,
  and variants/moods.
- **Fog-of-war maps**: multi-floor prep, brush-based fog editing, live
  streaming to players, per-client routing, zoom, and a player-accurate
  preview.
- **Music tab**: library, playlists, networked playback with per-window
  routing and a transport bar.
- **Sessions**: folder-scoped campaigns alongside the Shared Library, with
  their own export/import.
- Content-hash file dedup, stable Ids on library entries, delete-in-use
  warnings for Sound/Music, `.rtt-save` zip format, `--no-discovery` flag,
  auto-save/auto-load, and a unit/integration test suite in its own CI job.

### Changed

- Extracted the trigger-media editor, target-Scene picker, and Scene
  bundle's library-add row into three reusable controls, removing the
  duplicated markup/code-behind.
- Consolidated a "flat sub-panel" Border style (background/border/radius/
  padding), previously copy-pasted inline 8 times, into a shared `panel`
  style class.
- Moved MainWindow's ~165-line Host-only `<Window.Styles>` block out of the
  view markup into its own `HostStyles.axaml`.
- Removed ~78MB of unreferenced legacy theme background/button PNGs, byte-
  identical and dead in both `RpgTimeTracker/Assets` and
  `RpgTimeTracker.PlayerClient/Assets` (superseded by
  `RpgTimeTracker.Shared/SampleThemes`). Moved the built-in alarm sounds
  (`bell`/`digital`/`pling.wav`), which the Client never actually played,
  into `RpgTimeTracker.Shared` as the single source, propagated to both
  apps' output the same way `SampleThemes`/`BootstrapIcons` already are.
- Unified per-library scope/save methods and split `MainWindowViewModel.cs`
  into partial classes.
- Reworked the Characters tab's editor into collapsible cards with Markdown
  preview.

### Fixed

- Game time appeared frozen due to sub-second deltas being truncated
  instead of accumulated.
- Gregorian calendar's leap-year rule wrongly flagged century years.
- NPCs were missing from session export/import.
- Several audio-routing gaps left already-playing tracks/sounds
  unaffected by mute/reconnect.
- Fog-of-war: mask misalignment, truncated floor transfers, and a
  black-block flash before fog data resolved.
- Date/time and number formatting now follow the app's UI language, not
  the OS locale.
- Maps tab layout, brush sizing, and fog-rescale sampling bugs.

### CI

- Added a test asserting `de.json`/`en.json` have matching localization
  keys.
- Added a check (`third-party-notices.yml`) that fails if a shipped
  project's NuGet package isn't mentioned in `THIRD-PARTY-NOTICES.txt`.
- Added a weekly + push/PR check (`dependency-audit.yml`) that fails if
  `dotnet list package --vulnerable` reports a known CVE in any direct or
  transitive NuGet package, plus a Dependabot config for weekly NuGet/
  GitHub Actions update PRs.

## [1.0.0] - 2026-07-10

Initial release: a GM-side control app (`RpgTimeTracker`) and a read-only
player display app (`RpgTimeTracker.PlayerClient`), communicating over a
length-prefixed JSON-RPC protocol on the local network.

### Added

- Shared game clock with speed control, time jumps, and bookmarks.
- Timers, alarms, and recurring interval events with sound/trigger media.
- Media and sound libraries with gallery/slideshow support.
- Built-in TCP server with LAN/mDNS discovery, PIN, and version handshake.
- Per-client kick and automatic client reconnect.
- Calendar with recurring entries and ambience automation.
- JSON-based theme system with a custom color picker.
- Save/load of full app state; opt-in session log export.
- Bootstrap Icons set; English/German localization.
- About screen with license notices.
- CI build + release workflows; architecture/protocol docs.
