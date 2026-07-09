# RPG Time Tracker

[![Build](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml/badge.svg)](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/JonasTrampe/RpgTimeTracker)](LICENSE)

> **Status: 1.0.0-alpha.** Works and is actively used, but the API/
> protocol/save format may still change between alpha releases.

Two AvaloniaUI desktop apps (Windows/Linux/macOS) for a shared game clock
(timers, alarms, recurring intervals) at the pen & paper table: a
GM-side control app and a read-only display app for the players on a
second screen, including image/video sending and a completely separate
sound library for background sounds/effects.

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
  `t.hh:mm:ss` for days, with `-` for backward) as well as quick-select buttons
  (+1 hr, +8 hr, +1 day, -1 hr, -1 day). The most recent time jump
  (including via quick select or bookmarks) can be undone again with a button.
- **Configurable bookmarks**: e.g. "Dawn" (06:00), "Noon"
  (12:00), "Evening" (18:00), "Midnight" — freely nameable, editable, and
  deletable. For each bookmark, ▶ jumps to the next and ◀ to the previous occurrence
  of that time of day.
- **Timers**: any number of countdown timers with progress bar and
  remaining-time display, run on game time (not real time) and react
  correctly to time jumps in both directions (only while actively running).
  Optionally, an image/video can be attached to each timer, which is
  automatically sent to all players when it expires (see trigger media below);
  manually resetting the timer also closes this media again, if
  it is still being shown.
- **Alarms**: any number of alarms with a fixed target time, optionally
  recurring (e.g. every 24 game hours). Jumping back in time before the
  target time "disarms" an already-triggered alarm again.
- **Save & load**: the complete state (game time, speed,
  all timers, alarms, and bookmarks) can be exported as a JSON file via the
  💾/📂 buttons in the top left, or imported again.
- **Networked player display**: the GM starts a TCP server in the
  control app on a freely selectable port with a freely selectable
  server name (this is included in the mDNS/LAN announcement and thus appears
  in the clients' server list — useful once several hosts are running on
  the same network); player clients find it automatically via mDNS
  and LAN broadcast discovery (both sources are merged into one
  entry per server, with an indication of how it was found), or
  connect manually via IP:port. Connected clients are listed in the
  control app along with their connection time and can be disconnected
  individually with a click. If the connection drops unexpectedly (Wi-Fi
  hiccup, host restart), the client automatically retries the connection with
  increasing backoff, and on success receives the same complete
  state as a freshly connected client would — a manual "disconnect",
  on the other hand, stays disconnected. Before closing, if there are
  still cached images/videos, the client asks whether these temp files
  should be deleted. A PIN can optionally be set in the network settings
  that a client must know when connecting (empty =
  no PIN required) — pure LAN access control against accidentally
  connecting foreign devices, not real encryption/authentication.
  Client and host also exchange their
  protocol version when the connection is established; if they don't match, the
  host rejects the connection with a clear error message instead of undefined behavior.
- **Send image/video to players**: select an image or
  video (up to about 1900 MB) in the media library — this immediately opens a
  dedicated media window on all connected player clients (images inline,
  videos via the embedded VLC playback), optionally also locally on the GM's
  side (only when no player is currently connected, to avoid
  duplicate display). The window can be closed for everyone by the GM via
  "Remove"; whether it is shown in fullscreen or as a normal window
  is set independently and locally by each side (GM and each player). The GM's
  "player fullscreen" toggle switches both all connected player clients
  and the GM's own player window to fullscreen;
  each window (GM and client) can be shrunk back down at any time, purely
  locally, via the Escape key (or, for the GM, additionally via a button), without
  affecting the fullscreen state on the other side.
  The player client also reports back to the host when a
  video actually starts and ends playing (real VLC duration instead of
  an estimate), which e.g. an automatic clock pause during the video
  is based on.
- **Media library**: add images/videos to the library ahead of time
  (persists across restarts, including a thumbnail for images and
  free renaming) — double-clicking an entry shows it immediately
  to the GM and all connected players. Can be backed up via "Export"/
  "Import" ("Library" tab) into a separate, freely selectable folder,
  or read back in on a different machine.
- **Slideshow/gallery (image/video)**: every sent image/video is
  kept instead of being replaced by the next one — the GM and each player
  browse independently through everything that has already been shown, using the arrow
  key or clicks in the player window (purely locally, without asking the
  host). The GM can "highlight" an item for everyone (a
  suggested jump, not a requirement) or remove it from the gallery entirely, and
  an optional seconds-per-image setting advances everyone
  automatically (videos are never interrupted by this). Event
  media (timer/alarm/interval) take priority: they briefly interrupt the
  gallery display without becoming part of it themselves, and the gallery
  only continues after the triggering item is reset.
- **Trigger media**: an image/video (or a sound, selected directly from
  disk) can be assigned to timers, alarms, and interval events,
  which is sent automatically when triggered, optionally with fullscreen and
  an infinite loop instead of closing automatically. For video, there's additionally
  a clock pause during playback (pauses the clock exactly until the
  actual end, not just an estimated duration, even if
  the video only plays locally on the GM's side).
- **Sound library (its own "Sounds" tab, completely separate from
  image/video)**: send sounds to all players with a single click (and
  locally to the GM, if no player is currently connected) —
  entirely without its own display window, without replacing any currently shown
  image/video, and without being stopped itself by a new image/video or
  another sound. Any number of sounds can play simultaneously. Each
  library tile has a ⚙ icon with the settings (volume,
  infinite loop or a fixed repeat count, trimming — start/end
  in seconds — and an "GM test" button to preview locally without
  sending); this opens as an overlay, without shifting the
  tile layout. A sound triggered via the "sound" field of a timer/alarm/
  interval (instead of the built-in tones Ping/Bell/
  Digital) is also sent to the players and ends automatically
  when the triggering item is reset. The sound library can
  also be exported/imported like the media library.
- **Status bar (bottom, visible on every tab)**: server status
  (running/stopped, address:port, number of connected players), the currently
  shown image/video with a close button, a sound counter whose
  flyout lists all currently playing sounds with a live volume control
  and allows stopping each individually or all together (also across
  all connected player clients), a bell whose flyout shows the most recently
  triggered action messages ("... sent", etc.) as a history,
  as well as two fullscreen toggles (the GM's own local preview, remote player
  display). Deliberately placed outside the tab content so that nothing
  "jumps" while sounds start/end or a medium
  changes in the background.
- **Ingame calendar (its own host tab "Calendar")**: freely definable
  events on an ingame date/time with title, description, icon, color,
  recurrence (daily/weekly/monthly/yearly, optionally with an
  end date), and an optional image/video/sound trigger (triggers the usual
  event media, like a timer/alarm). A small toggle
  "Timeline ↔ Calendar" appears in the shared header area of the
  host's player window and the client — but only if at least one event
  visible to players exists; otherwise it stays as a plain timeline,
  with no empty calendar option. Persisted in the normal save state and
  additionally exportable/importable separately as JSON; changes are
  automatically sent to all connected players, and newly connected clients
  receive the full calendar upon connecting.
- **About dialog (host and client, "ℹ" button)**: title/description from an
  app-specific Markdown file (`About/AboutContent.md`, rendered via
  ClassIsland.Markdown.Avalonia), version number, as well as the app's own
  project license and the license notices of all
  third-party libraries used (`LICENSE`/`THIRD-PARTY-NOTICES.txt`, the same
  files this repo also draws its license information from — not
  duplicated separately), each in a collapsed section.
- **Media windows (host preview and client)**: in addition to the image/
  video, they show a small clock display (the current game time stays
  visible even with a full-area medium) as well as their own minimize button next to
  the close button, since in fullscreen/borderless mode there is no operating-system
  window title bar with native minimize available.
- **Sound per item**: timers/alarms/intervals play a
  selectable sound when triggered — either one of the built-in default tones (Ping/
  Bell/Digital, purely local on the GM's side) or a sound from the
  sound library (sent to players, see above). For the
  built-in tones, optionally multiple times in a row (configurable
  repeat count, 0 = infinite — only stops once the respective item
  is reset, acknowledged, or deleted); a library sound
  with a repeat count of 0 loops infinitely accordingly, until the item
  is reset.
- **Advance warning (visible only to the GM)**: optionally a notice banner a
  configurable time window (minutes of game time) before a timer/alarm/
  interval triggers — never sent to players.
- **Session log**: an optionally recordable list of triggered events
  (time jumps, triggers, sent media) with a game-time stamp; exportable
  as a text file to a freely selectable location.
- **Themes (JSON designs)**: all 8 bundled themes (Shadowrun, Medieval,
  Warhammer 40k, Alien, Steampunk, Post-Apocalypse, Eldritch Horror, Gothic
  Vampire) as well as any number of custom ones exist as `theme.json` files (colors,
  gradient colors, fonts, named background/button images) —
  the bundled ones under `SampleThemes/`, custom GM themes additionally in
  `%AppData%/RpgTimeTracker/Themes/`, where they automatically show up in the
  theme selector. When the GM selects a theme, it is also transmitted to
  connected player clients (by theme ID) — the client displays it
  correctly if it has the same `theme.json` locally (bundled for all 8
  default themes; a brand-new custom GM theme currently still has to be
  copied manually to the players' machines as well).
- **Ambient automation (optional)**: for a theme with several
  named backgrounds, automatically switches between day and night background,
  depending on the game time. For themes with only one background (currently all
  of them), it has no visible effect.

All timers and alarms are updated with the corresponding game-time delta on every
tick of the game clock *and* on every manual time jump —
if the clock is sped up, slowed down, or jumps forward/backward, the
behavior of all timers/alarms automatically changes accordingly. The player client
reconstructs the same behavior locally from the deltas sent by the host,
without needing a network message on every tick.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json`;
  all three projects target `net10.0`)
- Internet access for the first build (NuGet packages: Avalonia, CommunityToolkit.Mvvm,
  LibVLCSharp, Serilog)
- For video/audio playback (images always work without additional software):
  - **Windows**: no additional installation needed — the native VLC library
    is bundled via the `VideoLAN.LibVLC.Windows` NuGet package.
  - **Linux**: VLC must be installed system-wide, e.g.
    `sudo apt install vlc` (Debian/Ubuntu) or `sudo dnf install vlc` (Fedora).
    Without VLC installed, image display still works, but video/
    audio sending shows an error message instead of playing.

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
- Custom fantasy calendar (different month lengths, weekdays, holidays)
  instead of the standard `DateTime`
- Multiple parallel "sessions"/campaigns with their own game time
- Auto-save on closing the app / remember the last-used file
- Video chunking with progressive playback already during transfer
