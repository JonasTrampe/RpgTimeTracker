# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- **Documentation restructure**: the root README is now reStructuredText
  (`README.rst`), kept short (what the program is, how to build it, a
  link to the docs site) instead of the previous 300+ line feature list.
  The full feature-by-feature depth, plus a new FAQ, moved into a
  proper multi-page Sphinx site (`docs/`, Furo theme) published to
  GitHub Pages via a new `docs.yml` Actions workflow. Contributor-facing
  material (`architecture.md`, `design-decisions.md`, `protocol.md`,
  `legal-todo.md`) moved to `docs/internal/`, unchanged in content.

### Fixed

- **In-progress freehand strokes (Draw tool, player Shift+drag annotations)
  rendering as an invisible single point**: confirmed via diagnostic logging
  that a stroke's rendered Bounds always matched exactly the *first* point's
  coordinates, no matter how far the drag actually went - `Polyline.Points`
  was being mutated in place via `.Add()` as the drag progressed, but
  Avalonia's Polyline only recomputes its rendered geometry when the
  `Points` property itself is reassigned, not on in-place collection
  mutation. Persisted lines (SemiPermanent/Permanent) never showed this
  because they're rebuilt from scratch with the complete point list once
  the stroke finishes; Temporary-tier lines and player-drawn annotation
  strokes never get that "final rebuild" step, so they stayed stuck
  showing only their first point the entire time. Both call sites
  (MapEditCanvasControl's Draw tool, MapDisplayView's player-stroke
  capture) now reassign `Points` on every appended point instead of
  mutating the existing list.
- **Map annotation strokes/pings never actually disappearing**: confirmed via
  diagnostic logging that `AnnotationLayer.Children` grew monotonically
  across an entire session on the GM's own canvas - `Animation.RunAsync`'s
  returned Task was never completing, so the fade-then-remove logic never
  reached the removal step; every Temporary-tier stroke and ping ripple
  just piled up, fully opaque, forever instead of disappearing after a few
  seconds. Both duplicate implementations (MapEditCanvasControl's GM
  canvas, MapDisplayView's player-facing view) now drive the opacity
  animation fire-and-forget and remove the visual on a plain timer instead
  of awaiting the animation's own completion, so removal happens reliably
  regardless of the animation clock.
- **ColorPicker rendering completely blank everywhere it's used**: the real
  root cause was that `App.axaml` never included Avalonia.Controls.
  ColorPicker's own theme resources - `<FluentTheme/>` doesn't automatically
  merge that separate NuGet package's control templates, so every
  `<ColorPicker>` in the app (Draw-tool line color, fog color, item
  appearance color) was a completely bare, unstyled control with zero
  visual content. Added the missing `StyleInclude`, and additionally scoped
  the app-wide `Button` style away from ColorPicker's own internal button
  parts (`Button:not(ColorPicker):not(ColorPicker Button)`) since a bare
  `Selector="Button"` also matches Buttons inside third-party controls'
  templates, not just the app's own intentional buttons. Verified with a
  headless Avalonia render: the ColorPicker now applies its real template
  (previously zero-size, now a normal 64x32 swatch button) instead of
  rendering nothing.

### Changed

- **Draw tool brush cursor**: hovering over the canvas with the Draw tool
  selected now shows the same kind of visible brush-outline circle the
  fog/erase tools already have, sized to the current line thickness (and
  live-updating as you drag the thickness slider), instead of no cursor
  feedback at all.
- **Draw tool's line-thickness slider**: capped at 12px instead of 20px -
  the fog/erase brush's own size slider goes much larger since it controls
  an area radius in map cells, not a raw on-screen stroke width, so the
  Draw tool's own slider didn't need nearly the same range.
- **GM Draw tool color picker**: the GM can now pick any color for the next
  line before drawing (previously fixed to Gold) - persisted lines keep
  their own color from when they were drawn (SemiPermanent/Permanent), and
  the ephemeral Temporary-tier stroke is broadcast/rendered in that same
  picked color for every connected player instead of always Gold.
- **Map editor toolbar**: Reveal/Hide fog are now one "Fog" tool with a
  Reveal/Hide toggle beneath the shared brush-size slider, instead of two
  separate ComboBox entries. The Erase tool gained its own toggle to
  switch between erasing a whole touched line (default) and erasing only
  the individual points a brush drag passes over, trimming the line down
  instead of removing it outright (removing the line only once too few
  points remain to still draw one). Erasing points out of the middle of a
  line now splits it into separate lines instead of leaving a straight
  line visually bridging the gap where the erased points used to be.

### Fixed

- **GM's Temporary-tier Draw stroke showing as a red "????" tag**: it was
  reusing the player-stroke rendering path, which colors/labels a stroke by
  the drawing player's ClientId (PainterTagHelper) - an empty ClientId (the
  GM's own broadcast) fell into that helper's "unknown painter" fallback
  (red, tagged "????") instead of being recognized as the GM. It's now
  rendered Gold with no tag, matching the GM's other Draw-tool lines.
- **Fog tool's Reveal/Hide toggle not showing on first open**: the toggle's
  visibility was only synced from ToolCombo's SelectionChanged, which fires
  once during control initialization before the toggle even exists, so the
  default tool (Fog) never got its visibility set until the GM manually
  switched tools away and back.
- **Map editor sidebar layout**: the Fog/Erase mode toggles no longer overflow
  the sidebar (German labels like "Verstecken" were getting clipped) - each
  is now a single ToggleSwitch showing one label at a time instead of two
  fixed labels flanking the switch.
- **Persisted lines not rescaling with zoom**: SemiPermanent/Permanent lines
  on the GM's own map canvas stayed at their old pixel positions after
  zooming in/out (only the fog overlay and tokens were redrawn) - they're
  now redrawn at the new zoom level along with everything else.
- **GM map Draw tool**: a Temporary-tier line drawn with the GM's Draw tool
  now actually fades out after a few seconds instead of sitting on the map
  forever (it was being routed through the persisted-line path meant only
  for SemiPermanent/Permanent lines). The Erase tool now supports
  brush-drag erasing (reusing the same brush-size slider as the fog
  tools) in addition to the existing "Erase all" button. The Draw tool
  also gained its own brush-size slider to control line thickness,
  instead of a hardcoded stroke width.

### Added

- **Map annotation strokes**: a player can Shift+left-drag a freehand
  stroke on their own map view to point something out in more detail
  than a single ping - visible only to the GM, same one-way visibility
  and fade-out-after-a-few-seconds treatment as map ping.
- **Player info for Points of Interest**: POI library entries now have their
  own Markdown-authored player-facing bio field (previously only Characters
  had this), gated by its own player-visibility toggle - shown in a linked
  map token's hover tooltip the same way a Character's does.
- **Map ping**: a player double-clicking their map view pings only the GM;
  the GM pings every connected player (and the Host's own preview) by
  selecting the Ping tool, then double-clicking the map - rendered as a
  generic ripple that fades on its own after a couple of seconds, never
  persisted.
- **Player info in map tokens**: a Character's player-facing Markdown bio
  (previously authored but never transmitted) can now be shown in the map
  token's hover tooltip, gated by its own player-visibility toggle
  alongside Name/Portrait/Detail.
- **MapLiveWindow's "Vorschau" preview now live-mirrors the main canvas**:
  panning/zooming the GM's own map editor updates the preview thumbnail
  in real time (rather than fitting/zooming independently), so it always
  shows exactly what the GM is currently looking at; its own zoom
  controls were removed since they'd just get overridden by the next
  main-canvas zoom/pan anyway.
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
