# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- New "Music" tab with its own Music Library, deliberately separate from
  the existing Sound Library (own storage directory, own settings section,
  own export/import) - music will later be organized into playlists and
  referenced by a planned Scenes feature, so it needs to stay independent
  of one-off sound effects rather than sharing their list. This first pass
  covers the library itself (add/rename/set icon+volume/delete/export/
  import, single-click local preview); network playback to players and
  per-window (Host/Client) routing controls are follow-up milestones.
- Playlists: create/rename/delete named playlists, add tracks from the
  Music Library, reorder or remove them, and toggle "loop playlist" /
  "shuffle" per playlist. Purely local editing for now - actually playing
  a playlist to connected players is a follow-up milestone.
- Playlist playback: pressing Play on a playlist now actually streams music
  to all connected players and the Host's own local preview, entirely on
  its own channel from Sound Library effects (separate `Music` media kind,
  own player instance client-side, so a sound effect never interrupts or
  gets interrupted by music). The Host drives playback: it decides what
  plays next (respecting Loop/Shuffle) once a client reports a track ended,
  with a duration-estimate fallback so playback never gets stuck if no
  client is connected. A "Now Playing" bar in the Music tab shows the
  current playlist/track with Previous/Next/Stop and a live volume slider.
  Per-window (Host/Client) routing toggles for which windows play Music
  vs. Sound are a follow-up milestone.
- Per-window Music/Sound routing: a "Connected Clients" panel in Settings
  lets the GM independently turn Music and Sound on/off for each connected
  player window and for the Host's own local preview ("Host (this window)").
  Turning Sound off for a window, for example, stops it from receiving sound
  effects while it keeps getting Music (and vice versa) - useful for a
  spectator/stream window that should only hear ambience, or a player window
  that should stay silent while everyone else hears a jump-scare sting. Each
  player window now gets a stable identifier on first launch (stored locally)
  so the GM's choice for that window is remembered across reconnects instead
  of resetting to "on" every time; the muted window itself shows a small
  "muted by GM" indicator instead of silently receiving nothing.
- Full session export/import: a "📦 Export session…"/"📦 Import session…"
  pair in Settings > Save & load bundles the game state and the Media,
  Sound, and Map libraries into a single `.rtt-session` file - for a full
  backup or moving everything to a new machine in one step, rather than
  exporting the save file and each library separately. Import restores game
  state (replacing it, like a normal load) and adds library items
  (appending, like the existing per-library imports).


- Fog-of-war maps: a new "Maps" tab lets the GM prepare multi-floor maps
  ahead of time (name a map, add floor images, export/import a single map
  as a self-contained `.rtt-map` file to back it up or share it), plus a
  new "Edit…" window per map with a brush (reveal/hide, adjustable size),
  "Reset to starting", and a "Show to players" toggle. Revealed/hidden
  cells stream live to connected players (`map.show`/`map.fogUpdate`/
  `map.fogReset`/`map.hide`), with a full resync (all floor images +
  current fog) to any client that connects or reconnects while a map is
  open, so nothing ends up partially revealed/hidden by accident. Players
  see hidden cells as a solid opaque block by default and can browse
  between floors locally, independent of what the GM is currently
  editing. The Host's own local player-window preview always shows the
  exact same live map/media as connected players (not just when testing
  solo) - both reuse a shared `MapDisplayView`/`MapDisplayViewModel`
  (Shared project) instead of duplicating the rendering logic per app.
- Fog-of-war maps: player-side render style is now configurable in
  Settings ("Fog of war (player view)" - color picker, opacity slider,
  blur on/off toggle, blur strength slider), one shared preference
  pushed live to all connected players (`map.renderStyleChanged`) and
  sent to newly connecting/reconnecting clients as part of
  `session.snapshot`. Hidden cells now show a blurred, tinted view of
  the actual map underneath (cut out via the fog shape) rather than a
  flat blurred color block, so the blur slider has a real visible
  effect; the blur toggle turns that image-content blur off entirely
  (leaving just the flat tint) without losing the configured strength.
  Brush size in the map editor also got finer-grained (smaller default
  grid cells, wider brush range).

- Unit test project (`RpgTimeTracker.Tests`, xUnit) covering `GameClockService`
  (time jumps, speed multiplier) and the `TimerItem`/`AlarmItem`/
  `IntervalEventItem` model logic that both the Host and PlayerClient use to
  reconstruct timer/alarm state from time-jump deltas. Runs in its own CI
  workflow (`tests.yml`, separate from the plain compile check in
  `build.yml`) with its own status badge, so a broken test shows up
  distinctly from "does it compile".
- End-to-end map/fog integration tests (`MapFogIntegrationTests`) that spin
  up a real Host server and PlayerClient over an actual localhost socket
  (not mocks) and exercise the same wire path as a real session: a floor
  image larger than one network chunk, fog reveal/hide/reset, render-style
  settings, and reconnect resync. Directly guards against the class of
  bugs found and fixed this session (a map-floor transfer silently
  truncating, a fog mask only covering a corner of the image).
- `--no-discovery` CLI flag (Host app) skips starting the mDNS/LAN-broadcast
  responders when the network server starts - useful on restricted
  networks or when running multiple local instances side by side.
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

- A currently playing sound that finished purely on a remote client (Host
  window closed, or Sound turned off locally) never left the GM's "Active
  Sounds" panel - the natural-end report only ever checked whether it
  matched the currently-tracked video, so a sound's end was silently
  ignored and the entry lingered indefinitely.
- Turning Music or Sound off for a connected client only affected future
  broadcasts - a track/sound already playing on that window kept running
  until it happened to end on its own instead of stopping immediately.
- Muting the Host's own local preview ("Host (this window)") had the same
  gap: it only gated whether a *new* track/sound would start locally, so
  anything already playing kept running until it ended naturally.
- Turning Music back on for a client after muting it left the window
  silent until the Host's playlist sequencer happened to advance to the
  next track - re-enabling now resends the currently playing track right
  away (with the same estimated mid-track seek used for a fresh catch-up).
- A player connecting (or reconnecting) while a playlist was already
  playing never received the current track - music was deliberately left
  out of the general "catch a new client up" cache, since a Host-driven
  sequencer was assumed to make that unnecessary, but nothing actually
  caught the new client up on the track already in progress. The Host now
  remembers the currently playing track and sends it to any client that
  connects mid-playback (respecting that client's Music routing),
  estimating how far in the track already is (elapsed wall-clock time
  since it started) and seeking the new client there instead of always
  restarting the track from 0 - not frame-accurate, but close enough that
  a late joiner isn't noticeably out of sync with everyone else.
- Fog-of-war maps: the fog mask only affected a small corner of the
  floor image instead of the whole thing - the mask brush needed an
  explicit source/destination rect to stretch across the image; Stretch
  alone isn't enough for a brush used as an OpacityMask.
- Fog-of-war maps: a floor's image never fully arrived on connected
  clients (or the Host's own local preview) - the map-floor media
  transfer never set the expected total byte count, so the client
  considered the transfer "complete" after the very first chunk,
  decoded/used a truncated file, and silently dropped the rest of that
  floor's data. This was the actual cause of "the client doesn't show
  anything" for maps.
- Fog-of-war maps: a floor could render as a solid near-black block
  instead of the map image whenever its fog data hadn't resolved yet
  (e.g. still deserializing on the PlayerClient) - an Avalonia
  `OpacityMask` bound to a not-yet-set brush renders fully opaque and
  unclipped rather than fully transparent, so the tint layer briefly (or
  permanently, if fog never arrived) covered the whole image. The blur
  and tint layers are now skipped entirely until a real fog mask exists.
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
