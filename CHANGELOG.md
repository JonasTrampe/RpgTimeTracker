# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- The bundled Gregorian calendar used a plain "every 4 years" leap rule,
  which wrongly marks century years like 1900/2100 as leap years (the
  real rule skips those unless also divisible by 400, e.g. 2000). Added
  a proper `CalendarLeapYearRuleKind.Gregorian` rule implementing the
  actual 4/100/400 logic, switched the bundled Gregorian calendar and the
  Simple Calendar importer's "gregorian" rule mapping to use it, and
  added regression tests covering 1900/2000/2024/2100/2400.

### Added

- Scenes library (Phase 2 of the Scenes/Tags/Calendars project): a new
  `SceneLibraryItemViewModel` (Name, GM-only Markdown description with a
  preview toggle, a start date on the custom calendar via `GameInstant`,
  and an optional bundle of Media/Sound/Music/Map references) with
  Shared-vs-SessionLocal storage identical to the Characters library.
  Registered into `LibraryUsageRegistry` so deleting a referenced Media/
  Sound/Music/Map item is guarded the same way NPC references already are,
  and Scenes can carry Tags like every other library item. A new "Scenes"
  tab lists and edits them, mirroring the Characters tab's list+detail
  layout, plus a "▶ Activate" button that pushes the Scene's bundled
  Image/Map/Music/Sounds to players atomically through the existing
  per-kind send paths (the same ones a Media Library double-click or a
  Playlist already use). A Scene-scoped timeline and full-session
  export/import wiring aren't built yet.
- Tags (Phase 1 of the Scenes/Tags/Calendars project): a flat, campaign-wide
  list of freeform tags (name + color) manageable from a new "Tags" group in
  Settings. Media, Sound, Music, Map, and NPC library items each gained a
  `TagIds` list and a new "🏷" button opening a `TagSelectorFlyout` to
  toggle which tags are assigned. Deleting a tag clears it from every item
  that had it via the existing `LibraryUsageRegistry` mechanism, with no
  confirmation prompt (a tag carries no content of its own, unlike
  deleting a media file). Each item's tag-assignment flyout also has an
  inline "new tag" row (name field + Add button) so a GM doesn't have to
  leave the flyout and go to Settings to define a tag before assigning it.
  The Media/Sound/Music library panels now also
  have a tag-filter bar: a row of toggleable chips (generated from the
  campaign's tag list) above each library's items - select any number of
  tags to show only items carrying at least one of them, deselect all to
  show everything again. Map and NPC libraries use a simpler list UI
  outside `LibraryPanelView` and aren't covered by this filter bar yet.
- Bundled calendars can now seed their real holidays as recurring calendar
  entries (`CalendarDefinition.DefaultEntries` + a "Import calendar's
  default events" button in Settings) - Harptos's five festivals
  (Midwinter, Greengrass, Midsummer, Highharvestide, Feast of the Moon) and
  DSA's Namenlose Tage plus 19 real Aventurian feast days imported directly
  from Simple Calendar's own bundled DSA notes (Sommersonnenwende,
  Wintersonnenwende, Tag des Phex, ...). Opt-in and idempotent (matched by
  title), not applied automatically on every calendar switch.
- "Import calendar file..." button in Settings + `SimpleCalendarImporter`:
  imports a custom calendar either in our own schema or as a Foundry VTT
  Simple Calendar predefined-calendar export (auto-detected and converted -
  see the README's "More calendars" section for where to find more of
  those). Some Simple Calendar exports also bundle a "notes" array of
  real holidays/festivals (not all - it's per-calendar-author) - Yearly-
  recurring notes are imported as `CalendarDefinition.DefaultEntries` too,
  with HTML in their descriptions stripped to plain text. Documented
  approximations (e.g. weekday-epoch alignment, non-yearly notes skipped)
  are surfaced back to the GM as a status message.
- Custom calendar engine (Phase 0 of the Scenes/Tags/Calendars project): game
  time is now represented by a calendar-agnostic `GameInstant` (elapsed
  seconds) instead of `DateTime`, converted to/from a human date by a
  pluggable `CalendarDefinition` (month/weekday names and lengths, leap-year
  rule, hours/day, minutes/hour, seconds/minute, seasons, moons) - matching
  Foundry VTT Simple Calendar's predefined-calendar schema closely enough
  to adapt its calendars. Four calendars are bundled (Gregorian, a
  Harptos-style fantasy calendar with tendays and intercalary festivals,
  Voidreach - a 10-month/8-day-week/20-hour-day sci-fi calendar, and an
  Aventurian/Das Schwarze Auge-style calendar with the Twelve Gods' months
  and the five Namenlose Tage), selectable from Settings and persisted per
  campaign; GMs can also drop their own
  calendar JSON into `%AppData%/RpgTimeTracker/Calendars`. A new
  `CalendarDateInput` control (Y/M/D/H/M/S box grid, replacing the old
  Gregorian-only `DateTimeInput`) reads its bounds from the active calendar
  everywhere a date is entered (alarms, calendar entries, jump-to-date).
  **Breaking, unmigrated save-format change**: saves from before this
  update (`AppStateDto.Version < 5`) are rejected on load with a clear
  error instead of silently corrupting game time.
- Unit test guard (`SaveFormatVersionGuardTests`) that fails if
  `AppStateDto`'s serialized shape changes anywhere in its object graph
  without a matching bump to `AppStateDto.Version` - forces a deliberate
  decision about whether a data-model change needs a save-format version
  bump, the same way the RPC protocol-version CI check does for the wire
  format.
- CI check (`RPC Protocol Version` workflow) that fails a PR if
  `RpcMethods.cs`/`RpcParams.cs` change by more than whitespace without
  `ProtocolInfo.Version` also being increased - catches wire-format changes
  that forgot to bump the version the host/client handshake relies on to
  reject mismatched builds. See `docs/protocol.md`'s new "Protocol
  versioning" section.

### Changed

- Double-clicking a character's portrait now shows it to players (same
  action as double-clicking a Media Library tile), instead of only being
  previewable inside the editor. Portrait and Map Token are now laid out
  side by side instead of stacked, each still in its own bordered box.
  With just the Default variant, the variant name/"Standard" badge row
  above them is now also hidden (it was redundant chrome once the tab
  strip is already hidden for the same reason).
- Renamed the Characters tab's "Variants" section/button to "Profiles"
  ("Steckbriefe"/"Steckbrief hinzufügen" in German) - the underlying
  concept (a character's named moods/variants) is unchanged, but the
  section is really the player-facing bundle (portrait, token, player
  info, sounds), and "Steckbrief" (character dossier) reads more
  naturally for that than "Variant" did.
- Renamed a character's named mood/preset from "State" to "Variant"
  throughout the Characters tab and its underlying code, since "state" read
  as a UI/lifecycle term rather than "Neutral"/"Angry"/"Wounded" etc. The
  on-disk file format is unchanged - existing characters and their
  moods/presets keep loading exactly as before.
- Restructured the Characters tab's detail editor: GM Info and Variants
  are now their own collapsible sections instead of one long scrolling
  column, and a compact header shows the selected character's name plus
  a glance at the Active variant's portrait/token - editing a variant no
  longer requires scrolling past every GM info block first. Both sections
  now stretch to the panel's full width. The Variants list only appears
  once a second variant has actually been added - with just the
  always-present Default variant, its editor is shown directly instead
  of a one-row list above a redundant "Selected Variant" heading, with a
  short hint explaining why, and the editor heading now always names the
  variant it's showing so deleting your only extra variant doesn't look
  like the character's data got wiped (it just switches back to Default).
- Reworked the Characters tab's GM Info/Variants sections again: both are
  now wrapped in the same bordered card look used everywhere else in the
  app (the GM Info Expander itself is transparent/borderless, so only the
  wrapping card's background/border shows, instead of Avalonia's default
  Expander chrome looking out of place). The "Add" button for each section
  now sits in its header row instead of below it. Variants switched from a
  list to tabs once a character has more than one - Default-only
  characters show their (single) editor directly, with no tab strip.
  Each sound attached to a variant is now its own bordered row, matching
  GM info blocks, instead of a plain list line.
- GM Info blocks and Player Info now render as Markdown (via the same
  ClassIsland.Markdown.Avalonia control already used for the About
  screen) instead of only ever showing as plain text. Each of those text
  fields has a small toggle button in its top-right corner to switch
  between the plain-text editor and the rendered preview. Both start in
  preview mode - opening a character shows rendered notes, not raw
  Markdown source, until you toggle to edit.
- Refined the Characters tab further: GM Info/Player Info text areas now
  cap out at 60% of the window's height instead of growing unbounded, and
  a genuinely empty field (a brand-new GM info block, or a variant with no
  Player Info yet) starts in edit mode instead of an empty preview with no
  obvious way to start typing. The Active-variant name and the "only
  Default variant" hint in the compact header now only show once a
  character actually has more than one variant. The "Variants" section
  title is de-emphasized to match Player Info's label styling. Deleting
  the active variant moved into the section header (to the right of "Add
  Variant", so its own visibility never shifts Add's position) instead of
  living inside the per-variant editor. Adding/deleting a variant no
  longer scrolls the whole tab back to the top. Portrait, Token, and
  Player Info are each now their own bordered card (matching GM Info
  blocks), and Portrait/Token replaced their Choose/Clear text buttons
  with a single edit (✎) button opening a small menu, so the image itself
  gets more room instead of competing with button rows.
- Internal cleanup pass, no user-visible behavior change: unified the
  per-library `Move*ItemToScope` and `Save*LibrarySettings` methods (Media/
  Sound/Music/Map/Character) behind two shared helpers instead of five
  near-identical copies each, removed two now-pointless single-line copy
  wrappers left over from the content-hash dedup work, extracted the
  repeated "move to Shared Library"/"move to this Session" icon-button
  pair (Media/Sound/Music tiles) into a reusable `MoveToScopeButtons`
  control, and split the ~6,300-line `MainWindowViewModel.cs` into
  per-library partial-class files for readability.

### Fixed

- Creating a new Alarm could fail with an "invalid alarm date" error and
  refuse to add it, even with no obviously invalid input. `AddAlarm` now
  falls back to "1 hour from now" if the target-time field can't be parsed,
  the same "always succeeds via a sensible default" behavior Timer/
  IntervalEvent already had for their duration fields, instead of blocking
  the add.
- Alarm creation/editing could also fail with an "invalid repeat interval"
  error even though the Repeat field visually looked empty - the underlying
  `AllowEmpty` `TimeSpanInput` control could end up bound to `"00:00:00"`
  instead of a true empty string. Rather than chase every way that could
  happen, `AddAlarm`/`ApplyEdits` now treat a zero-valued repeat interval
  the same as an empty one (a one-time alarm) - only a string that fails to
  parse at all is a genuine error. Also fixed `TimeSpanInput`'s `AllowEmpty`
  handling to clear its Days box along with Hours/Minutes/Seconds (it was
  the one box left out), and to re-render from the authoritative `Text`
  value on a limit-property change instead of recomputing `Text` from the
  boxes.
- Characters were silently losing data on every app launch: loading a
  saved character (rebuilding it from `settings.json`) triggered the same
  save-on-change path used for interactive edits, before that character
  was actually registered in the library - each save serialized the
  library as it stood at that moment, missing the character currently
  being loaded and any not yet reached. In practice this meant the last
  character in your Shared library quietly disappeared from disk every
  time you started the app (and the last session-local character every
  time you opened a session), even though nothing you did in the UI
  triggered it. Loading a character now suppresses those saves until it's
  fully built and back in the library.
- Sessions shipped without any way to actually move an item between the
  Shared Library and a session after adding it - the backend for Media/
  Sound/Music already existed but had no UI, and Maps/Characters had
  neither. Every library tile now has a "move to Shared Library"/"move to
  this Session" action (Maps and Characters via their "⋮" menu, the
  others as a small icon button), and Maps/Characters gained the missing
  move logic itself (a map moves its whole per-map folder in one step; a
  Character has no files to move, just its Scope).
- The Map Live/Prepare editors' toolbar rows (floor selector, brush tools,
  zoom controls, and the Live window's Reset/Show-to-players row) used a
  fixed-width horizontal layout that squeezed together or overflowed on a
  narrower window instead of wrapping - same fix already applied to the
  Maps tab's floor-action row, now applied here too.

### Added

- New "Characters" tab: a library of NPCs/PCs, each with a portrait, a map
  token (an image, a Bootstrap icon, or initials derived from the name -
  whichever is set, in that order), GM-only reference notes (any number of
  named, ordered blocks, e.g. "Motivation"/"Secrets"), and any number of
  named variants/moods (e.g. "Neutral"/"Angry") that can each override the
  portrait, token, player info, and connected sounds - left unset, a variant
  falls back to the character's Default variant, so you only need to
  override what actually changes. A character's portrait and sounds
  reference the Media/Sound Libraries rather than owning copies; deleting
  a referenced item now warns if a character still uses it, the same way
  it already does for trigger media and playlists. Player info is
  GM-facing only in this pass - not yet sent to connected players.

- Adding an image/video, sound, or music track now names the stored file
  by its content hash instead of a random ID - adding or importing the
  exact same file twice into the same library (Shared, or the same open
  session) reuses the existing copy instead of storing it again. Only
  applies within one storage location; the same file added to both the
  Shared Library and a session is still stored once per location, by
  design.

- Media and Sound Library entries now carry a stable `Id` (a `Guid`), like
  Music Library entries already did - groundwork for an upcoming Characters
  (NPC) library, which needs to reference a portrait/sound by value instead
  of by its mutable name/file path. Existing entries get a fresh Id
  assigned automatically the next time settings are loaded.

- **Sessions**: an optional, folder-scoped campaign the GM can create or
  open via "New Session…"/"Open Session…" in the status bar, sitting
  alongside the always-present Shared Library. With no session open, the
  app behaves exactly as before this feature existed. While a session is
  open:
  - Adding a new Media/Sound/Music item, or creating a new map, asks
    whether it belongs to the Shared Library (reusable across every
    campaign) or to this session only - the file is copied into the right
    place accordingly.
  - Closing the session hides its session-local items again (nothing is
    deleted); reopening the same folder brings them back.
  - "Export Session"/"Import Session" (the existing 📦 buttons) now bundle
    just that session's own state and session-local library entries, and
    importing always creates a brand-new session folder with fresh IDs
    rather than merging into the Shared Library. With no session open,
    these buttons keep their previous meaning (the whole Shared Library).

- Deleting a Sound or Music Library item now warns if it's still in use
  (a sound assigned as a timer/alarm/interval's event medium, or a track
  still in a playlist) and offers the same Cancel / clear-and-delete /
  keep-file choice the Media Library already had - previously Sound
  deletion had no safeguard at all, and Music deletion silently dropped
  playlist references with no warning.

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
- Resent sounds also get an estimated mid-playback seek: when re-enabling
  Sound for a client, a sound longer than a configurable threshold (default
  10 seconds, "Seek threshold for resent sounds" in Settings) is resumed at
  roughly the position it's already at, wrapped into its own loop/repeat
  cycle if applicable - matching the estimated seek playlist music already
  got. Shorter sounds always restart from the beginning, since seeking into
  a brief one-off effect isn't meaningful.
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
- Maps tab redesigned for usability and to properly separate preparing a
  map from showing it: the Maps tab now uses a wrapping layout instead of a
  rigid two-column split, so it no longer squishes on narrow windows. The
  old single "Edit…" map editor is now two separate windows - a per-floor
  "Edit…" window that only ever touches an offline prepare/starting mask
  (never seen by players, no matter what), and a per-map "Show…" window
  that paints the live, players-visible fog directly (unchanged from
  before) plus a renamed "Reset to Prepared" button that pulls in whatever
  was last painted in the Edit window, and the Open/Close-to-players
  toggle. Both editors now show a live brush-outline cursor so it's clear
  exactly what a stroke will affect. Default new-floor cell size is halved
  (finer grid) with brush radius doubled to match the old visual size.
  Fog render style (color/opacity/blur) can now be overridden per map from
  the Edit window, falling back to the existing global Settings-tab values
  when left unset. Cell size is now GM-editable per floor (with a warning
  for very small values) from the Edit window; changing it rescales the
  existing mask instead of corrupting it, and new floors inherit a
  per-map default cell size rather than a single global constant. Editing
  (the "Edit" window) and showing (the "Show" window) a map are now
  mutually exclusive: the Show button is disabled while a floor is being
  edited, and the Edit button is disabled while that map is shown to
  players, so a cell-size rescale can never happen while the mask is
  actively being streamed.
- Per-client Image/Video/Map routing: the Connected Clients list gets three
  separate toggles - "Images", "Videos", and "Maps" - next to Music/Sound,
  so a multi-monitor GM setup can dedicate one window to maps while
  another only shows images, or exclude a window (e.g. a tablet) from a
  specific kind entirely. Each mirrors the existing Music/Sound routing
  pattern independently, including persistence per window and resending/
  clearing currently shown content of that kind on toggle.
- Both map editors (Edit/Prepare and Show/Live) now have a live preview
  panel showing the map exactly as players actually see it - the same
  blurred/tinted fog render used by the real PlayerClient and the Host's
  own PlayerWindow, not the flat editor tint used for painting precision.
  The floor selector, brush toolbar, and paintable canvas (previously
  near-duplicated between the two editor windows) are now a single shared
  `MapEditCanvasControl`, so the two windows differ only in what they do
  with a stroke afterward (broadcast vs. debounced file save) and what
  extra controls they show (Reset/Open-to-players vs. fog style/cell size).
- Both map editors can now zoom in/out (25%-400%, "−"/"+"/"Fit" buttons or
  Ctrl+scroll wheel over the canvas), useful for precise brush placement on
  large maps - previously the map always scaled to fit the window with no
  way to work at a larger effective resolution. A newly opened floor starts
  fit-to-window as before; zooming keeps whatever point was centered in
  view centered afterward instead of jumping to a corner.
- Maps tab layout: the map-library panel is narrower and stays a fixed
  width (sized for its entries), while the floor-gallery panel now resizes
  with the window instead of a fixed width, so wider windows actually use
  the extra space. Floors show as a name list with a small thumbnail
  (instead of a thumbnail-card grid) and can be reordered with per-row
  up/down buttons - a map's floors are stacked layers, so their order is
  meaningful, unlike the map library list itself. The Add floor/Edit/Show
  buttons moved to the bottom of the floor panel in a wrapping row, so they
  drop to a second line instead of overflowing when the panel is narrow.
  The Maps tab's description text is now as short as the other tabs'.
  The button row is now docked to the bottom of the panel itself (instead
  of just following the floor list), so it stays pinned there regardless
  of how many floors a map has, rather than drifting down as the list grows.
- A map's floor/layer order is now saved explicitly (not just implied by
  list position), so reordering floors with the up/down buttons survives a
  save/load round-trip through settings, a single-map (`.rtt-map`) export,
  or a full-session (`.rtt-session`) export.
- Maps now carry an explicit format version, saved alongside the map data
  and exports. Every existing/new map is version 1 today with no behavior
  change - this just gives a future format change something to check
  against so it can detect and upgrade an older map on load instead of
  guessing.

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

- NPCs (the Characters library) were silently left out of both "Export
  Session" and "Export Full Session" (and their matching imports) - a
  bundle looked complete but every character was simply gone on the other
  end. Import now also relinks each variant's portrait/token/sound references
  to the freshly-imported copies bundled in the same zip, and a variant's
  Active variant selection now survives a save/load round-trip too (it
  previously always reset to the Default variant on reload, since a fresh Id
  was minted for every variant even when restoring one that already had one).
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
- Same gap for Sound: re-enabling it for a client didn't resend anything
  currently playing, so the window stayed silent until the next brand-new
  sound happened to fire. Re-enabling now resends every currently active
  sound to that client, with the same estimated mid-playback seek as music
  for sounds longer than the configurable threshold.
- The Host's own local preview had the same gap as connected clients: on
  re-enabling PlayMusicLocally mid-track, it stayed silent until the next
  track instead of resuming - it now resumes the current track locally
  with an estimated seek, matching the network path.
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
- Maps tab's left-hand map list was cramped and hard to read (name field
  squished to a few characters) once German button labels were factored
  in - Export and Delete are now collapsed into a single "⋮" overflow menu
  per map (matching the existing Media/Sound library pattern), leaving the
  name field enough room to actually show a name. The per-floor Edit/Delete
  buttons in the floor gallery had the same problem - their full German
  text button labels overflowed the 140px floor tile - now small icon
  buttons matching the same tile-card pattern used elsewhere.
- The brush size in both map editors was expressed as a raw cell count, so
  the same brush setting painted a wildly different physical area depending
  on a floor's CellSizePx - switching to a coarser grid (or rescaling one)
  made the brush look "comically large" without the GM changing anything.
  Brush size is now a physical-pixel radius under the hood, so it stays the
  same visual/effective size regardless of a floor's CellSizePx.
- Moving the brush cursor toward the right/bottom edge of a map editor made
  the scrollable viewport jump around erratically - the cursor was
  positioned via Margin, which (unlike a render transform) participates in
  the canvas's own layout measurement, so a large left/top margin kept
  growing the canvas itself and fed back into the scroll position.
  Positioning now uses a RenderTransform, which is purely visual and can't
  affect layout.
- Rescaling a fog mask on a CellSizePx change sampled from each new cell's
  top-left corner instead of its center, introducing a systematic
  directional bias that visibly drifted the revealed/hidden boundary after
  repeated size changes. Rescaling now samples from cell centers, and
  CellSizePx is restricted to even multiples of 2 so repeated rescales use
  clean ratios instead of compounding rounding error.

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
