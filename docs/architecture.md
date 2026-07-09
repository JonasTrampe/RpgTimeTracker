# Architecture

## Overview

RpgTimeTracker is a two-application system for running a shared game-time clock
(timers, alarms, recurring "OnTime" intervals) at a tabletop RPG session and
projecting it to a second screen for the players, optionally with images/video and
a fully separate sound library for background sounds/effects.

```
┌─────────────────────────────┐         TCP :48550          ┌──────────────────────────────┐
│   RpgTimeTracker (Host)      │  JSON-RPC over length-      │  RpgTimeTracker.PlayerClient  │
│   - GM/SL controls           │  prefixed binary frames     │  - read-only display          │
│   - authoritative clock      │ ───────────────────────────>│  - derives its own clock       │
│   - timers/alarms/intervals  │                              │  - renders timeline + media    │
│   - sends media to players   │ <─── media.playbackStarted/  │  - reports video playback      │
└──────────────┬───────────────┘      media.playbackEnded    └───────────────┬────────────────┘
               │                                                              │
               │  UDP :5353 (mDNS) / UDP :48551 (LAN broadcast)               │
               └─────────────────────── discovery ───────────────────────────┘

Both reference:
┌────────────────────────────────────────────────────────────────────────┐
│                        RpgTimeTracker.Shared                            │
│  Network frame format · JSON-RPC envelope/methods/params · domain       │
│  models (TimerItem/AlarmItem/IntervalEventItem) · GameClockService ·    │
│  media helpers · shared Avalonia styles + views · theme loading ·       │
│  logging bootstrap                                                      │
└────────────────────────────────────────────────────────────────────────┘
```

Any number of PlayerClient instances can connect to one Host at a time (e.g. a
laptop on the table plus a TV in the corner). The connection is one TCP socket
per client; the Host is the only process that ever writes game state.

## Projects

| Project | Type | Role |
|---|---|---|
| `RpgTimeTracker` | Avalonia desktop app (`WinExe`) | The GM/SL-facing control app. Owns the authoritative clock and all timeline items. Runs the TCP server. |
| `RpgTimeTracker.PlayerClient` | Avalonia desktop app (`WinExe`) | Read-only display app for players. Connects to the Host, derives its own clock, renders the shared timeline and any pushed media. |
| `RpgTimeTracker.Shared` | Class library | Everything both apps need identically: the wire protocol, domain models, the derived-clock service, media helpers, shared XAML styles, logging bootstrap. Referenced by both apps — nothing protocol- or model-related is duplicated. |

All three target **.NET 8** and use **Avalonia 12** for UI, **CommunityToolkit.Mvvm**
(source-generated `[ObservableProperty]`/`[RelayCommand]`) for MVVM, and
**LibVLCSharp** (+ `LibVLCSharp.Avalonia`'s `VideoView`) for video playback, and plain
`LibVLCSharp.Shared.MediaPlayer` instances (no `VideoView`, no visual window) for sounds -
see [`design-decisions.md`](design-decisions.md) for why sounds are fully decoupled
from the image/video display slot (no window, unlimited parallel playback).
Logging is **Serilog**, configured once in `RpgTimeTracker.Shared.Logging.AppLogging`
and used via the static `Serilog.Log` accessor throughout (no DI container — the
whole codebase is built around static services and manually-wired ViewModels,
consistent with `SoundService`, `ThemeSettingsService`, etc.).

## Host app (`RpgTimeTracker`)

- **`ViewModels/MainWindowViewModel.cs`** — the one large ViewModel behind the
  main window. Owns the `GameClockService`, the `TcpPlayerServerService`, the
  three timeline-item collections (`Timers`/`Alarms`/`IntervalEvents`), the
  media library, and all the "add new item" staging fields. Every mutation
  that matters to a connected client calls a `Publish*Async` method on the
  server (see [`protocol.md`](protocol.md)).
- **`ViewModels/TimerItemViewModel.cs` / `AlarmItemViewModel.cs` /
  `IntervalEventItemViewModel.cs`** — one ViewModel per timeline-item kind,
  each wrapping a `RpgTimeTracker.Shared.Models` domain object (`TimerItem` /
  `AlarmItem` / `IntervalEventItem`). Each exposes a `StateChanged` event that
  fires on discrete state transitions (started/paused/reset/completed/edited)
  — deliberately **not** on every clock tick — which `MainWindowViewModel`
  uses to know when to push a `timelineItem.upserted` delta.
- **`ViewModels/TimelineDisplayItemViewModel.cs`** — a facade that wraps
  whichever of the three item ViewModels a given row is, so the shared
  "Item List" list and the read-only player list can bind against one
  unified type instead of three.
- **`Network/TcpPlayerServerService.cs`** — the TCP server. Accepts
  connections, sends the granular RPC deltas, streams media in chunks, runs a
  5-second heartbeat, and (since the client-feedback protocol addition) also
  *reads* small inbound RPC frames from clients (playback start/end reports).
- **`Network/PlayerMdnsAnnouncer.cs` / `LanDiscoveryResponder.cs`** — two
  independent, best-effort discovery mechanisms so PlayerClient doesn't
  require manually typing an IP: a minimal hand-rolled mDNS responder
  (`_rpg-time-tracker._tcp.local` on UDP 5353) and a simple UDP broadcast
  probe/response protocol on port 48551. Both failures are non-fatal — manual
  host:port entry always works as a fallback.
- **`Models/TriggerMediaConfig.cs`** — host-only authoring data attached to a
  Timer/Alarm/Interval: an optional image/video/audio to auto-send when that
  item fires, with `Fullscreen`, `PauseClockDuringVideo` (video only), and
  `Loop` flags. Not part of the wire protocol's item DTO — only the resulting
  media send (and the fullscreen/pause side effects) cross the network. Picks
  the file directly from disk (`TriggerMediaFileType` in
  `MainWindow.axaml.cs`) rather than from a library.
- **`ViewModels/SoundLibraryItemViewModel.cs` / `MainWindowViewModel.SoundLibrary`** —
  a library of sounds, deliberately separate from `MediaLibrary`
  (image/video only, see [`design-decisions.md`](design-decisions.md) for
  why). A single click sends a sound to all players (and locally if no
  client is connected); "Test" previews it locally without sending. Each
  entry has its own default `Volume` (0-100), settable in the library and
  overridable live while playing (see `ActiveSoundViewModel`/
  `MainWindowViewModel.ActivePlayingSounds`, the "currently playing sounds"
  panel with per-sound and bulk stop). The Timer/Alarm/Interval "Sound"
  dropdown (`SoundService.SoundOptions`) is fed by this library plus the 3
  built-in notification blips (Pling/Glocke/Digital) — picking a library
  entry there sends it to players exactly like the Sounds tab, picking a
  built-in keeps the old local-only blip behavior (`SoundService.Play`).
- **`Persistence/AppStateDto.cs`** — the JSON schema for the save/load
  feature (File → Save/Load), independent of the network protocol.
- **`Services/ThemeService.cs`** — swaps the active Design's `ResourceDictionary`
  into `Application.Resources.MergedDictionaries`, either a compiled
  `Themes/*.axaml` (built-in Design) or a `ResourceDictionary` built at
  runtime by `RpgTimeTracker.Shared.Theming.ThemeDefinitionLoader` from a
  `theme.json` (SL-authored Design — see
  [`design-decisions.md`](design-decisions.md)). `MainWindowViewModel` loads
  every discoverable `theme.json` (shipped samples under `SampleThemes/` next
  to the exe, plus the SL's own under
  `%AppData%/RpgTimeTracker/Themes/`) at startup and appends them to the same
  `ThemeOptions` dropdown as the 8 built-in Designs.
- **`SampleThemes/*/theme.json`** — the 8 built-in Designs, mechanically
  extracted from their `Themes/*.axaml` counterparts (colors, gradients,
  fonts, corner radii, background/button images), shipped as ready-to-copy
  examples of the JSON schema an SL can author their own Design in.
- **Ambience automation** (`MainWindowViewModel.UpdateAmbience`) — if enabled
  and the active Design is a JSON one with 2+ named `Backgrounds`, swaps
  `WindowBackgroundImageBrush` between the first (day, 06–18) and last
  (night) entry as the game clock crosses those hours. A no-op for any
  Design with only one background — which today is every built-in Design
  except the "Medieval" sample (see
  [`design-decisions.md`](design-decisions.md) for why more variety isn't
  wired in by default).
- **`Views/PlayerWindow.axaml(.cs)`** — the Host's own local player-facing
  window (see [`ShouldShowMediaLocally`](design-decisions.md) for when it
  actually shows media). `MainWindowViewModel.RemoteFullscreen` — toggled
  manually, by an event trigger's `Fullscreen` flag, or cleared when the
  network server stops — drives both the RPC push to remote clients *and*
  this window's `WindowState` via the `LocalFullscreenRequested` event, so
  "Player Fullscreen" fullscreens the Host's own preview together with every
  connected PlayerClient. Locally, `PlayerWindow` also has its own Escape-key
  handler and an "Exit Fullscreen (Esc)" button (visible only while actually
  fullscreen) that exit fullscreen for *this window only*, without touching
  `RemoteFullscreen` or pushing anything — see
  [`design-decisions.md`](design-decisions.md) for why that stays purely
  local.

## PlayerClient app (`RpgTimeTracker.PlayerClient`)

- **`ViewModels/ClientMainWindowViewModel.cs`** — mirrors the Host's
  responsibilities on the receiving end: owns its own `GameClockService`
  instance (ticking locally, corrected by heartbeats and jump events — see
  [`protocol.md`](protocol.md) for why), a `Dictionary<Guid, RemoteTimelineEntry>`
  that reconstructs `TimerItem`/`AlarmItem`/`IntervalEventItem` instances
  locally from received deltas, and the media-window lifecycle.
- **`Network/PlayerTcpClientService.cs`** — the TCP client. Reads the
  frame/RPC stream, writes received media chunks progressively to a temp
  file (so video playback can start before the transfer finishes), runs an
  idle-watchdog (`TcpClient.ReceiveTimeout` does **not** apply to async
  reads, so a silently-dropped connection needs its own timeout logic), and
  can now also *write* two small outbound notifications
  (`media.playbackStarted` / `media.playbackEnded`). Also *receives*
  `media.stopSound`/`media.setVolume` (targeted per-sound stop/live-volume
  from the SL's "currently playing sounds" panel) and raises them as events
  the ViewModel resolves against its own sound-`MediaId` dictionary.
- **`Network/MdnsDiscoveryService.cs`** — the discovery client side: probes
  both mechanisms in parallel-ish sequence and merges results keyed by
  `host:port`, tagging each discovered server with which mechanism(s) found
  it (`DiscoverySource` flags — `Lan`, `Mdns`, or both).
- **`Services/ClientThemeService.cs`** — mirrors the Host's `ThemeService`:
  applies either a compiled `Themes/*.axaml` or, via
  `ThemeDefinitionLoader.BuildResourceDictionary`, a JSON SL-Design. The
  active Design is entirely Host-driven — `ClientMainWindowViewModel.ApplyTheme`
  reacts to `theme.changed`/`session.snapshot.Theme`, resolving a
  `"custom:<Id>"` value against the client's own `SampleThemes/` +
  `%AppData%/RpgTimeTracker/Themes/` (see
  [`design-decisions.md`](design-decisions.md)) — there is no independent
  theme picker on the client side.
- **`Views/MediaWindow.axaml(.cs)`** — a separate, reusable window for
  image/video display. Deliberately created once and only ever `Hide()`/`Show()`n
  afterward (never `Close()`d by app logic) so it keeps its last position and
  size, and its geometry is explicitly synced to the main list window
  whenever new media arrives (see [`design-decisions.md`](design-decisions.md)).
- Both `ClientMainWindow` and `MediaWindow` (whichever is currently
  fullscreen — media takes priority over the timeline list when visible, see
  `OnRemoteFullscreenRequested`) install a local Escape-key handler that
  exits fullscreen for that window only, without touching `MediaFullscreen`
  or anything Host-pushed — the same purely-local escape hatch as the Host's
  `PlayerWindow`, see [`design-decisions.md`](design-decisions.md).

## Shared player UI (`RpgTimeTracker.Shared/Views`)

The Host's `PlayerWindow` and the Client's `ClientMainWindow` show the same
kind of thing — a title/subtitle/clock header, and a card with the scrollable
timeline list — so that markup lives once, in `RpgTimeTracker.Shared/Views`:

- **`PlayerHeaderView.axaml`** — the title/subtitle + current-time/speed
  header block.
- **`PlayerTimelineListView.axaml`** — the "Timeline" card, including the
  per-item template (icon/name/kind/status, primary value/detail, progress
  bar).

Both bind against `RpgTimeTracker.Shared.Visuals.IPlayerDisplayContext`
(`PlayerHeaderTitle`/`PlayerHeaderSubtitle`/`CurrentGameTimeText`/
`SpeedMultiplierDisplay`/`TimelineEntries`), which `MainWindowViewModel` and
`ClientMainWindowViewModel` both implement — they already used identical
property names, so no renaming was needed, just adding the interface. Each
item in `TimelineEntries` implements `IPlayerTimelineEntry`
(`TimelineDisplayItemViewModel` on the Host, `RemoteTimelineItemViewModel` on
the Client) — again, already-matching property names/types made this a
zero-rename extraction. See [`design-decisions.md`](design-decisions.md) for
why the state-modifier styling (`.completed`/`.active`/`.blink`) deliberately
stays *out* of these shared controls.

What's *not* shared, and stays app-specific around these two controls:
- Host's `PlayerWindow` adds its own embedded local media overlay (image/
  video Panel) and the fullscreen exit button/Escape handler.
- Client's `ClientMainWindow` adds its own connection-status bar and the
  expandable "manually connect / discovered servers" section — the one part
  that has no Host equivalent, since the Host doesn't connect to anything.

## Threading model

- **UI thread**: all ViewModel state, all Avalonia bindings, both
  `GameClockService` instances (their `DispatcherTimer` always fires on the
  UI thread by construction).
- **Background threads** (`Task.Run`): the TCP accept loop, the per-connection
  read loop (both sides), the heartbeat loop, and the two UDP discovery
  responders/probes.
- Anywhere a background thread needs to touch UI-bound state (reading
  `MainWindowViewModel` fields to build a snapshot, or writing a received
  value into a ViewModel property), the call is marshalled with
  `Dispatcher.UIThread.Post(...)` or `Dispatcher.UIThread.InvokeAsync(...)`.
  This is applied consistently — grep for `Dispatcher.UIThread` if adding new
  network callbacks.
- Fire-and-forget async calls (`_ = _playerServer.PublishXAsync();`) are used
  pervasively for RPC publishes triggered from synchronous UI event handlers.
  `AppLogging.Initialize` installs `AppDomain.UnhandledException` and
  `TaskScheduler.UnobservedTaskException` handlers specifically because of
  this pattern — otherwise an exception inside one of these would vanish
  silently.

## Data flow summary

1. Host starts the TCP server + both discovery responders
   (`TcpPlayerServerService.Start()`).
2. PlayerClient discovers or is given a `host:port`, connects
   (`PlayerTcpClientService.ConnectAsync`).
3. Host immediately sends a `session.snapshot` (full state) to that one
   client (`SendCatchUpAsync`), plus the last-sent media if any.
4. From then on, the Host sends only **deltas**: clock start/stop/speed/jump,
   header/theme changes, per-item upserts/removals, fullscreen requests, and
   chunked media begin/data.
5. PlayerClient reconstructs local `TimerItem`/`AlarmItem`/`IntervalEventItem`
   instances from each `timelineItem.upserted` and lets its own
   `GameClockService` advance them locally between deltas — it does not need
   a network message for every tick.
6. For video, PlayerClient reports back when playback actually starts (with
   the real, LibVLC-measured duration) and when it ends, so the Host can
   resume a clock it paused for the video and/or auto-close a non-looping
   video — see [`protocol.md`](protocol.md) for the full rationale.

## See also

- [`protocol.md`](protocol.md) — the wire format and JSON-RPC method catalogue.
- [`design-decisions.md`](design-decisions.md) — why the protocol and media
  pipeline are shaped the way they are, including some fixes that reversed
  earlier decisions.
