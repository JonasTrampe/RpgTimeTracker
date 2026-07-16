# RPG Time Tracker

[![Build](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml/badge.svg)](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml)
[![Tests](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/tests.yml/badge.svg)](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/tests.yml)
[![License: MIT](https://img.shields.io/github/license/JonasTrampe/RpgTimeTracker)](LICENSE)

> **Status: 1.0.0.** See [CHANGELOG.md](CHANGELOG.md) for a history of
> notable changes.

Two AvaloniaUI desktop apps (Windows/Linux/macOS) for a shared game clock
(timers, alarms, recurring intervals) at the pen & paper table: a GM-side
control app and a read-only display app for players on a second screen,
including image/video sending and a separate sound library for
background sounds/effects.

## The two apps

- **`RpgTimeTracker`** (GM/Host) — the control app. Owns the
  authoritative game clock as well as all timers/alarms/intervals, manages
  the media library, and runs the TCP server that any number of
  player clients can connect to simultaneously.
- **`RpgTimeTracker.PlayerClient`** (player client) — a pure display app.
  Connects to the host via LAN/mDNS discovery or manual IP entry,
  derives its own game clock locally from that, and shows the timeline as
  well as images/videos sent by the host (sounds play without their own display).

Both apps communicate over a lightweight, length-prefixed
JSON-RPC protocol on TCP; details on this (frame format, complete
method list, media streaming) can be found in [`docs/protocol.md`](docs/protocol.md).

## Features

- **Game clock**: date & time freely settable, start/pause, keeps
  running automatically afterward.
- **Speed**: time factor from 0.1x to 300x (presets + slider) —
  e.g. "1 real second = 1 hour of game time" during a rest.
- **Time jump forward & backward**: free-form jump text field (`hh:mm:ss` or
  `t.hh:mm:ss` for days, `-` for backward) plus quick-select buttons (+1 hr,
  +8 hr, +1 day, -1 hr, -1 day). The most recent jump (including via quick
  select or bookmarks) can be undone with a button.
- **Configurable bookmarks**: e.g. "Dawn" (06:00), "Noon" (12:00), "Evening"
  (18:00), "Midnight" — freely nameable, editable, deletable. ▶/◀ jump to the
  next/previous occurrence of that time of day.
- **Timers**: any number of countdown timers with progress bar and
  remaining-time display, run on game time and react correctly to time
  jumps in both directions while running. An attached image/video is sent
  to all players automatically on expiry (see trigger media below);
  resetting the timer closes it again if still shown.
- **Alarms**: any number of alarms with a fixed target time, optionally
  recurring (e.g. every 24 game hours). Jumping back before the target time
  disarms an already-triggered alarm.
- **Save & load**: the complete state (game time, speed, timers, alarms,
  bookmarks) can be exported as a `.rtt-save` file via the 💾/📂 buttons top
  left, or imported again. Older plain-JSON saves still load and are
  upgraded automatically on next save.
- **Sessions (optional, folder-scoped campaigns)**: "New/Open/Close
  Session" in the status bar creates or opens a folder holding one
  campaign's own Media/Sound/Map/Character libraries, alongside the
  always-present Shared Library - purely opt-in, no session behaves as
  before. New items ask Shared Library or session-only, changeable later
  via "Move to Shared Library"/"Move to this Session". Closing a session
  hides its items from library lists (files stay on disk).
- **Full session export/import**: Settings' "📦 Export/Import session…".
  With a session open, it bundles that session's state and local
  Media/Sound/Map/Character entries plus copies of any referenced
  Shared-sourced assets, so it's self-contained elsewhere; import always
  creates a fresh session with new IDs, never merging into the target's
  Shared Library. With no session open, it exports the game state plus
  the entire Shared Library as one `.rtt-session` file - a full backup or
  one-step machine move. (Music/playlists use their own Export/Import in
  the Music tab.)
- **Maps / fog-of-war (its own "Maps" tab)**: a library of maps, each made
  of one or more floors (image + grid cell size). A floor's fog (brush
  reveal/hide, zoom, resizable cell size) is prepared offline in a
  "Prepare" window — never visible to players — separate from the "Show"
  window, which paints and broadcasts the *live* fog and can reset it to
  the prepared state. Both windows have a live, player-accurate preview
  and can be open side by side. A map can override fog color/opacity/blur,
  falling back to the global default (Settings tab). Floors are
  reorderable, thumbnailed layers whose order survives export/import.
  Only one map shows to players at a time, and not while its Prepare
  window is open. Exportable individually (`.rtt-map`) or in a session
  bundle.
- **Characters library (its own "Characters" tab)**: NPCs/PCs with a
  portrait, a map token (image, Bootstrap icon, or initials from the name -
  whichever is set), any number of named, ordered GM-only reference note
  blocks (e.g. "Motivation"/"Secrets", rendered as Markdown), and any number
  of named variants/moods (e.g. "Neutral"/"Angry") that can each override
  the portrait, token, player info, and connected sounds - an unset variant
  falls back to the Default variant. Portrait and sounds reference the
  Media/Sound Libraries by ID rather than owning copies; deleting a
  referenced item warns if a character still uses it, the same 3-way
  confirm-delete used for trigger media and playlists. Included in the
  Sessions' Shared-vs-session-local split and export/import.
- **Map tokens**: markers placed on a map (drag to reposition), either
  freeform or linked live to a Character+Variant or a Point of Interest, with
  a Reveal-mode border, a hover tooltip, and per-field player-visibility
  toggles - synced live to connected players (`map.tokenUpsert`/
  `map.tokenRemove`), with hidden/GM-only fields resolved on the Host so
  they never reach the wire.
- **Initiative tracker**: a drag-to-reorder combat order per map (Start/
  Stop/Next), with auto-skip for incapacitated participants, a facing arrow
  (mouse wheel to rotate) on Character tokens, and a per-campaign choice of
  whether the game clock freezes or advances a fixed amount per round while
  it runs.
- **Player-side map zoom/pan**: players (and the Host's own local preview)
  can independently zoom (Ctrl+wheel), pan (right/middle-drag, or scroll/
  Shift+scroll), and fit-to-window. A GM setting can snap every connected
  view to a configured zoom centered on the current initiative turn.
- **Networked player display**: the GM starts a TCP server on a freely
  selectable port/name (in the mDNS/LAN announcement - useful with several
  hosts on one network); clients find it via mDNS/LAN discovery (merged
  into one entry per server) or manual IP:port. Connected clients are
  listed with connection time and can be disconnected individually. On an
  unexpected drop, the client retries with backoff and, on success, gets
  the same full state as a fresh connection - a manual disconnect stays
  disconnected. The client offers to delete cached temp media before
  closing. An optional PIN gates connections (LAN access control, not real
  auth); client/host also exchange protocol version, rejecting a mismatch
  clearly instead of misbehaving. For each connected client (and the
  Host's own preview), the GM can independently route Music/Sound/Image/
  Video/Map - e.g. a second-monitor client for maps/images, a tablet for
  Music only. The preference persists across reconnects; re-enabling a
  kind catches the client up immediately, disabling it clears the display.
- **Send image/video to players**: selecting an image or video (up to
  ~1900 MB) opens a dedicated media window on all connected clients
  (images inline, video via embedded VLC), and locally for the GM if no
  player is connected. The GM closes it for everyone via "Remove";
  fullscreen vs. normal window is set independently per side, and the
  GM's "player fullscreen" toggle switches all clients and the GM's own
  window at once - each can shrink back locally via Escape (or a button
  for the GM) without affecting the other side. The client reports real
  video start/end (actual VLC duration, not an estimate) for things like
  an automatic clock pause during playback.
- **Media library**: add images/videos ahead of time (persists across
  restarts, with thumbnails and free renaming) — double-clicking shows an
  entry immediately to the GM and all connected players. Backed up via
  "Export"/"Import" (Library tab) into any folder.
- **Slideshow/gallery (image/video)**: every sent item is kept instead of
  replaced by the next - the GM and each player browse independently
  through everything shown so far (arrow keys or clicks, purely local).
  The GM can "highlight" an item for everyone (a suggested jump) or
  remove it from the gallery; an optional seconds-per-image setting
  auto-advances everyone (videos are never interrupted). Event media
  (timer/alarm/interval) briefly interrupts the gallery without joining
  it, resuming once the trigger resets.
- **Trigger media**: an image/video (or a sound, picked directly from
  disk) can be assigned to timers, alarms, and interval events, sent
  automatically on trigger, optionally fullscreen and looping instead of
  auto-closing. Video additionally pauses the clock for its actual
  duration, even when it only plays locally for the GM.
- **Sound library (its own "Sounds" tab, separate from image/video)**:
  send sounds to all players (and locally, if no player is connected)
  with no display window, without touching any shown image/video or
  stopping other sounds - any number play simultaneously. Each tile's ⚙
  overlay covers volume, infinite loop or a fixed repeat count, start/end
  trimming, and a "GM test" preview. A sound triggered via a timer/alarm/
  interval's "sound" field (instead of the built-in Ping/Bell/Digital
  tones) is likewise sent to players and ends when the trigger resets.
  Exportable/importable like the media library.
- **Status bar (bottom, visible on every tab)**: server status
  (running/stopped, address:port, connected players), the current
  image/video with a close button, a sound counter whose flyout lists
  playing sounds with live volume control and per-item/all-together stop
  (across all clients), a bell showing recent action messages, and two
  fullscreen toggles (GM preview, remote player display). Placed outside
  the tab content so nothing "jumps" as sounds or media change in the
  background.
- **Ingame calendar (its own host tab "Calendar")**: freely definable
  events on an ingame date/time with title, description, icon, color,
  recurrence (daily/weekly/monthly/yearly, optional end date), and an
  optional image/video/sound trigger (same event-media path as a
  timer/alarm). A "Timeline ↔ Calendar" toggle appears in the shared
  header once a player-visible event exists; otherwise it stays a plain
  timeline. Persisted in the save state and exportable/importable
  separately as JSON; changes sync live, and new connections get the
  full calendar.
- **Custom calendars (Settings tab)**: game time is calendar-agnostic
  (`GameInstant`, elapsed seconds), so a campaign can pick a different
  calendar - month/weekday names and lengths, leap-year rule,
  hours/minutes/seconds per day, seasons, moons. Four ship bundled
  (Gregorian, Forgotten Realms-style Harptos, a Das Schwarze Auge-style
  Aventurian calendar, and Voidreach, a fictional sci-fi calendar), each
  with an "Import calendar's default events" button for its real holidays
  (Harptos's five festivals, DSA's Namenlose Tage, ...). Switching
  calendars only changes how dates display - elapsed time is unaffected.
  - **More calendars**: [Foundry VTT's Simple Calendar module](https://github.com/vigoren/foundryvtt-simple-calendar/tree/main/src/predefined-calendars)
    ships several dozen more (Dark Sun, Eberron, Exandria, Golarion,
    Greyhawk, Warhammer, ...). Its schema differs from ours - use
    Settings' "Import calendar file..." button, which auto-detects and
    converts a Simple Calendar export (see
    `RpgTimeTracker.Shared/Services/SimpleCalendarImporter.cs` for the
    field mapping and its weekday-epoch approximations). GM-authored
    calendar JSON (our schema) can also be dropped directly into
    `%AppData%/RpgTimeTracker/Calendars`
    (Linux/macOS: `~/.config/RpgTimeTracker/Calendars`).
- **About dialog (host and client, "ℹ" button)**: title/description from
  `About/AboutContent.md` (rendered via ClassIsland.Markdown.Avalonia),
  version number, the app's project license, and third-party license
  notices (`LICENSE`/`THIRD-PARTY-NOTICES.txt`, not duplicated
  separately), each in a collapsed section.
- **Media windows (host preview and client)**: alongside the image/video,
  a small clock display (game time stays visible even full-area) and
  their own minimize button next to close, since fullscreen/borderless
  mode has no OS title bar.
- **Sound per item**: timers/alarms/intervals play a selectable sound on
  trigger — a built-in tone (Ping/Bell/Digital, local to the GM) or a
  library sound (sent to players). Built-in tones can repeat (configurable
  count, 0 = infinite, stopping only on reset/acknowledge/delete); a
  library sound with repeat count 0 loops the same way until reset.
- **Advance warning (GM-only)**: an optional notice banner a configurable
  number of game-minutes before a timer/alarm/interval triggers — never
  sent to players.
- **Session log**: an optionally recordable list of triggered events (time
  jumps, triggers, sent media) with a game-time stamp, exportable as text.
- **Themes (JSON designs)**: 8 bundled themes (Shadowrun, Medieval,
  Warhammer 40k, Alien, Steampunk, Post-Apocalypse, Eldritch Horror, Gothic
  Vampire) plus any custom ones as `theme.json` files (colors, gradients,
  fonts, background/button images) — bundled ones under `SampleThemes/`,
  custom GM themes in `%AppData%/RpgTimeTracker/Themes/`, auto-appearing in
  the selector. A selected theme is transmitted to clients by ID; a client
  renders it correctly if it has the same `theme.json` locally (bundled
  for all 8 defaults; a new custom theme still needs manual copying).
- **Ambient automation (optional)**: for a theme with multiple named
  backgrounds, switches day/night background by game time - no visible
  effect for single-background themes (currently all of them).

All timers and alarms are updated with the corresponding game-time delta on every
tick of the game clock *and* on every manual time jump —
if the clock is sped up, slowed down, or jumps forward/backward, the
behavior of all timers/alarms automatically changes accordingly. The player client
reconstructs the same behavior locally from the deltas sent by the host,
without needing a network message on every tick.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json`;
  all three projects target `net10.0`)
- Internet access for the first build (NuGet: Avalonia, CommunityToolkit.Mvvm,
  LibVLCSharp, Serilog)
- For video/audio playback (images always work without extra software):
  - **Windows**: nothing extra needed — native VLC is bundled via the
    `VideoLAN.LibVLC.Windows` NuGet package.
  - **Linux**: VLC must be installed system-wide, e.g.
    `sudo apt install vlc` (Debian/Ubuntu) or `sudo dnf install vlc` (Fedora).
    Without it, images still work but video/audio sending errors instead
    of playing.

## Building & Running

```bash
dotnet restore RpgTimeTracker.sln

# GM/Host app:
dotnet run --project RpgTimeTracker/RpgTimeTracker.csproj

# Player client:
dotnet run --project RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj
```

For a standalone Windows exe (for host and client respectively):

```bash
dotnet publish RpgTimeTracker/RpgTimeTracker.csproj -c Release -r win-x64 --self-contained true
dotnet publish RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj -c Release -r win-x64 --self-contained true
```

For Linux, use `-r linux-x64` accordingly.

Prebuilt `win-x64`/`linux-x64` archives for both apps are attached to each
[GitHub Release](https://github.com/JonasTrampe/RpgTimeTracker/releases),
built the same way by `.github/workflows/release.yml`.

## Project structure

```
RpgTimeTracker.Shared/          # referenced by both apps, nothing duplicated
├── Network/    # NetworkFrame, MediaLimits, DTOs
├── Rpc/        # RpcMessage/RpcNotification<T> (JSON-RPC envelope)
├── Models/     # TimerItem, AlarmItem, IntervalEventItem, MediaKind
├── Services/   # GameClockService, MediaTypeHelper, VlcMediaService
├── Visuals/    # VisualItemHelper (Bootstrap icons)
├── Logging/    # AppLogging (Serilog bootstrap for both apps)
└── Styles/     # AppStyles.axaml - shared window chrome

RpgTimeTracker/                 # GM/Host app
├── Models/         # TriggerMediaConfig and other host-only models
├── Network/        # TcpPlayerServerService, PlayerMdnsAnnouncer, LanDiscoveryResponder
├── Persistence/    # AppStateDto (save/load schema)
├── ViewModels/      # MainWindowViewModel, TimerItemViewModel, ConnectedClientItemViewModel, ...
├── Views/           # MainWindow.axaml(.cs)
├── App.axaml / App.axaml.cs
└── Program.cs

RpgTimeTracker.PlayerClient/    # player client app
├── Network/        # PlayerTcpClientService, MdnsDiscoveryService
├── Services/       # ClientSettingsService, ClientThemeService
├── ViewModels/      # ClientMainWindowViewModel
├── Views/           # ClientMainWindow.axaml(.cs), MediaWindow.axaml(.cs)
├── App.axaml / App.axaml.cs
└── Program.cs
```

## Documentation

More detailed technical documentation lives in [`docs/`](docs/):

- [`docs/architecture.md`](docs/architecture.md) — system overview: the three
  projects, their responsibilities, the threading model, and the data flow.
- [`docs/protocol.md`](docs/protocol.md) — frame format and the complete
  JSON-RPC method list (both directions), media streaming, clock sync.
- [`docs/design-decisions.md`](docs/design-decisions.md) — why certain
  things are built the way they are, including a few decisions that
  deliberately reversed earlier solutions.

## Possible extensions

- Sound/popup notification when a timer expires or an alarm triggers
- Auto-save on closing the app / remember the last-used file
- Video chunking with progressive playback already during transfer
- Characters: send player info to connected clients, automatic/triggered
  variant-switching (currently only manual GM switching of a character's
  Active variant), and a live side-by-side Markdown preview for GM info
  blocks/player info (currently a toggleable preview, not split-view)
- Standalone Character export/import (folder-based, like Media/Sound/Music)
  and a `.rtt-map`-style single-character export
