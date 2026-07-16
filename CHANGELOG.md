# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Characters**: entries can now be marked as a player character (`Kind`
  toggle, alongside NPC), and each Variant gains a GM-only Health/condition
  freetext field plus a Status picker. Status is sourced from the active
  Theme's own configurable list (Name/tint color/"skips initiative turn"
  per entry) rather than a fixed set, with a small built-in default list
  for Themes that don't define their own - groundwork for map tokens and
  the initiative tracker.
- **Player-side map zoom/pan/fit**: the map view players see (and the
  Host's own local preview) now supports independent zoom (Ctrl+wheel),
  panning (drag with right/middle mouse button, or scroll/Shift+scroll),
  and a Fit button - a `LayoutTransformControl` replaces the old fixed
  fit-to-window `Viewbox`, so every existing floor-image/token binding
  needed no change. A GM setting ("auto-zoom to active character",
  toggleable from both Settings and the Map view itself) snaps every
  connected view to a configured zoom level centered on whichever token
  has the initiative tracker's current turn whenever a turn starts
  (switching floor first if needed) - players remain free to zoom/pan away
  again afterward until the next turn. Reaches already-connected clients
  live (`map.autoZoomChanged`) and newly-connecting ones via
  `session.snapshot`, same pattern as the fog render style. The Live
  window's sidebar is now resizable (`GridSplitter`).
- **Map tokens**: markers placed on a map, either freeform (own Name/
  Description/icon) or linked live to a Character+Variant or a Point of
  Interest (no data copied onto the token). Editable from both the
  Prepare and Live map windows: drag to reposition, a floor-filtered
  canvas overlay with a color-coded Reveal-mode border and a full-info
  hover tooltip, and a Tokens side panel to add/link/edit/delete (link
  kind, Reveal mode, per-field player-visibility toggles, move-to-floor).
  Now synced live to PlayerClient (`map.tokenUpsert`/`map.tokenRemove`,
  full resync on connect via `map.show`): the Host resolves and filters
  each token (link data, Reveal mode vs. live fog) before it ever reaches
  the wire, so a hidden or GM-only token's data never leaves the Host.
  Portrait image transfer (vs. a fallback icon glyph) is deferred to a
  follow-up.
- **Initiative tracker**: a manually-ordered (drag to reorder), possibly-
  repeating combat order tracked per map, lives as an "Initiative" tab
  alongside the map's Tokens panel in the Live window. Add Characters or
  freeform participants, Start/Stop/Next; advancing auto-skips a
  participant whose Status has "skips initiative turn" set, wraps to
  round 1→N, and - for a Character with a token placed on the current
  map - switches floor and pans the canvas to it, plus pushes a
  highlighted ring to connected PlayerClients via a new `IsCurrentTurn`
  flag on `map.tokenUpsert`. The game clock's behavior while a tracker
  is running (freeze it entirely, or jump forward a configurable number
  of game-seconds once per round) is a GM setting. Character tokens also
  gain a facing-direction arrow, adjusted via mouse wheel over the
  marker and synced to players the same way.
- **Points of Interest**: a new top-level library for non-Character map
  markers (chests, traps, signposts) - Name/GM-only Description, an icon
  (either a Media Library image or a Bootstrap icon), player-visibility
  toggles, and Tags, following the same Shared/session-local scoping as
  every other library. Groundwork for map tokens to link to something
  other than a Character.
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

- Map tokens now update their player visibility after resetting live fog to
  the prepared mask, render their permitted icon/detail fields correctly,
  stay hidden when positioned outside the map, and use stable snapshots
  during reconnect synchronization.
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
- The "New Alarm" target-time default (current time + 1h, was +8h) got
  stuck at the clock's construction-time value after loading a save or
  switching calendars, instead of tracking the actual current game time.

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
