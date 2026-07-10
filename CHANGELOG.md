# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Full session export/import: a "📦 Export session…"/"📦 Import session…"
  pair in Settings > Save & load bundles the game state and the Media,
  Sound, and Map libraries into a single `.rtt-session` file - for a full
  backup or moving everything to a new machine in one step, rather than
  exporting the save file and each library separately. Import restores game
  state (replacing it, like a normal load) and adds library items
  (appending, like the existing per-library imports).


- (In progress) Fog-of-war maps: a new "Maps" tab lets the GM prepare
  multi-floor maps ahead of time (name a map, add floor images, export/import
  a single map as a self-contained `.rtt-map` file to back it up or share it).
  Each floor starts fully fogged; the eraser/pen editor and live reveal to
  players are not implemented yet - this only builds the map/floor storage
  and library UI skeleton.

- Unit test project (`RpgTimeTracker.Tests`, xUnit) covering `GameClockService`
  (time jumps, speed multiplier) and the `TimerItem`/`AlarmItem`/
  `IntervalEventItem` model logic that both the Host and PlayerClient use to
  reconstruct timer/alarm state from time-jump deltas. Runs in its own CI
  workflow (`tests.yml`, separate from the plain compile check in
  `build.yml`) with its own status badge, so a broken test shows up
  distinctly from "does it compile".
- Optional auto-save on close and auto-load on startup (Host app), using the
  location of the last manual save/load. Both are opt-in toggles in the
  Settings tab; existing behavior is unchanged unless enabled.
- The save/load file format is now a zip container with the `.rtt-save`
  extension (was a bare `.json` file) - chosen so a save file isn't mistaken
  for arbitrary JSON data, and to make room for upcoming binary data
  (fog-of-war maps) that doesn't belong in JSON. Old `.json` saves still
  load fine and are upgraded to the new format automatically the next time
  you save.

### Fixed

- Date/time display (game clock, calendar, alarm trigger time) and the
  speed-factor/trim/lead-time number inputs now follow the app's own
  selected UI language (English/German) instead of the OS locale - a
  German-Windows user switching the app to English no longer sees German
  day names or a comma decimal separator, and vice versa.
- Two remaining hardcoded German UI strings (connected-since label,
  player-header speed label) moved to the localization files.

## [1.0.0] - 2026-07-10

Initial release: a GM-side control app (`RpgTimeTracker`) and a read-only
player display app (`RpgTimeTracker.PlayerClient`), communicating over a
length-prefixed JSON-RPC protocol on the local network.

### Added

- Shared game clock with adjustable speed (0.1x–300x), forward/backward time
  jumps with undo history, and configurable time-of-day bookmarks.
- Timers, alarms, and recurring interval events that run on game time and
  react correctly to time jumps, each with an optional sound and trigger
  media.
- Media library (images/videos/audio) with thumbnails, trim/volume/repeat
  settings, and one-click sending to all connected players, including a
  multi-item gallery with slideshow auto-advance.
- Separate sound library for background ambience/effects, independent of
  timer/alarm trigger sounds, with per-sound volume, trim, and repeat count.
- Built-in TCP server with LAN/mDNS discovery (merged into one list entry
  per server), manual IP:port connection, optional connection PIN, and a
  protocol-version handshake that rejects mismatched clients with a clear
  error instead of undefined behavior.
- Per-client connection list on the host with individual disconnect
  ("kick"), and automatic reconnect with backoff on the client side after
  an unexpected disconnect.
- Calendar with custom recurring entries, day-cell view, and (optional)
  automatic time-of-day ambience background switching.
- JSON-based theme system (8 bundled sample themes) shared by both apps,
  including a real color picker for custom accent colors.
- Save/load of the full app state (game time, speed, timers, alarms,
  bookmarks) as a JSON file.
- Opt-in session log with explicit save-location export.
- Full Bootstrap Icons set (2000+ icons) for timers/alarms/sounds, backed
  by a single bundled JSON catalogue instead of hand-copied SVG paths.
- English/German UI localization, switchable at runtime without restarting.
- About screen (per app) with version, own MIT license, and third-party
  license notices, rendered from Markdown.
- CI build workflow (Windows + Linux compile check) and a release workflow
  that publishes self-contained `win-x64`/`linux-x64` build artifacts to
  GitHub Releases.
- Architecture, protocol, and design-decision documentation under `docs/`.
