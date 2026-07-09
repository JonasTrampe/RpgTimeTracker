# Design Decisions

Short, dated-in-spirit notes on *why* things are shaped the way they are —
useful context for anyone (human or AI) about to change this code, since
several of these decisions look arbitrary in isolation but exist because an
earlier, more "obvious" approach was tried and broke something specific.

## Delta protocol instead of periodic full-state push

**Decision**: the Host sends granular events (`clock.started`,
`timelineItem.upserted`, …) instead of periodically broadcasting the entire
session state.

**Why**: an earlier version pushed a full `PlayerStateDto` snapshot on every
clock tick. This meant the network/serialization cost scaled with tick rate
× item count regardless of whether anything actually changed, and made the
"only send the game time on an actual jump, not every tick" requirement
impossible to satisfy — the whole point of the client deriving its own clock
locally is that most ticks need no network message at all.

## Client derives its own clock instead of receiving time every tick

**Decision**: `RpgTimeTracker.Shared.Services.GameClockService` runs
identically on both sides. The Host publishes `clock.timeJumped` only from
an explicit jump (`GameClockService.Jumped`, distinct from the internal
per-tick `Tick` event); the client advances its own instance between
messages and self-corrects via the 5s heartbeat.

**Why**: sending the game time every ~200ms tick (the `DispatcherTimer`
interval) to N clients is wasteful and was the original reason the delta
protocol exists at all. The trade-off is that the client's displayed time
can drift very slightly from the Host's between heartbeats — acceptable for
a tabletop display, corrected within 5 seconds regardless.

## Two-tier write-lock timeout (`skipIfBusy` vs. must-deliver)

**Decision**: state RPCs use a non-blocking, skip-if-busy write attempt;
media chunks and the initial catch-up snapshot use a blocking 15s-timeout
write.

**Why** (this reversed an earlier bug, not just a design choice): with a
single shared timeout, a slow media transfer holding the per-connection
write lock would cause a *concurrent, unrelated* state publish to time out
and the client would be wrongly disconnected — even though the connection
was healthy, just busy sending a video. Splitting the semantics (state:
skip and rely on the heartbeat's self-healing; media/catch-up: must
eventually succeed or the connection really is dead) fixed both the false
disconnects and kept slow media from blocking gameplay-critical state
updates indefinitely.

## Idle-connection watchdog with mid-transfer activity tracking

**Decision**: `TcpClient.ReceiveTimeout` is set but explicitly not relied
upon; both sides run their own idle-watchdog task, and `NetworkFrame.ReadAsync`
takes an `onDataReceived` callback invoked on every *partial* read, not just
completed frames.

**Why**: `.NET`'s `TcpClient.ReceiveTimeout` does not apply to
`Stream.ReadAsync` — a silently dead connection (cable pulled, no TCP RST)
would hang forever undetected without a custom watchdog. The
partial-read callback specifically avoids a second bug: a large-but-healthy
media transfer taking longer than the idle timeout would otherwise look
identical to a dead connection to a watchdog that only resets on full-frame
completion.

## Client-reported video playback state (bidirectional protocol exception)

**Decision**: the protocol is one-way (server → client) except for two tiny
client → server notifications, `media.playbackStarted` and
`media.playbackEnded`. This required changing the server's inbound handling
from "drain and discard everything" to actually parsing small inbound RPC
frames.

**Why**: the Host has no way to know what's actually happening on a remote
screen — whether a video even started rendering, its real (LibVLC-measured,
not Host-guessed) duration, or when it finished — without the client
telling it. This is needed for two features: pausing the game clock for the
duration of a triggered video and resuming it accurately, and auto-closing a
non-looping video when it's actually done rather than guessing a timeout. A
duration-based fallback timer still exists for robustness (no client
connected, or an old client that never sends the report), but the real
report wins the race whenever one arrives.

## Media size limit: 1900 MiB, not exactly 2 GiB

**Decision**: `MediaLimits.MaxMediaBytes = 1900L * 1024 * 1024`.

**Why**: media files are read entirely into a `byte[]` (`fileBytes.Length`
is an `int`), and a .NET array can never hold more than `int.MaxValue`
(~2.147 GB) elements. An earlier change set the limit to *exactly*
`2 * 1024 * 1024 * 1024` (2 GiB, one byte **past** `int.MaxValue`) — which
made the `fileBytes.Length > MaxMediaBytes` check permanently unreachable
(the compiler even flags this as CS0652, "comparison useless, constant out
of range"). 1900 MiB stays safely under that hard ceiling. The Host and
client used to each define their own copy of this constant with different
values (200 MB), which meant a client could silently reject media the Host
had already accepted and sent — the limit now lives once, in
`RpgTimeTracker.Shared.Network.MediaLimits`, referenced by both.

## Host shows media locally only when no client would see it

**Decision**: the Host's own player-facing window (`PlayerWindow`) only
decodes/plays a sent image or video itself
(`MainWindowViewModel.ShouldShowMediaLocally`) when `IsPlayerWindowOpen &&
(!IsNetworkServerRunning || ConnectedClientCount == 0)` — i.e. there is
nobody else it would be redundant for.

**Why**: earlier, the Host always displayed media locally in a separate
window regardless of whether a real player client was connected and showing
the same thing, which was both redundant and a second place for the same
bugs to show up. Tying it to "no one else is watching" makes the local
preview specifically a solo-testing/no-network-yet convenience rather than
a permanent second display surface.

## Media window is hidden, not closed, between shows

**Decision**: both `MediaWindow` instances (Host's local overlay is a
same-window `Panel`, not a separate window; the client's `MediaWindow` *is*
a separate window) are created once and thereafter only `Hide()`/`Show()`n
— app logic never calls `Close()` on the client's `MediaWindow` except when
the whole app is shutting down.

**Why**: Avalonia windows keep their `Position`/`Width`/`Height` across
`Hide()`/`Show()` but lose it if recreated from scratch. The client's media
window used to be closed and `new`'d up again on every hide/show cycle,
which meant it reappeared at a default position/size instead of where the
player had left it.

## Client's media window geometry is synced to the main window, not remembered independently

**Decision**: every time the client's media window is about to show new
content, its `Position`/`Width`/`Height`/`WindowState` are explicitly copied
from the main list window (`ClientMainWindow.SyncMediaWindowToMainWindow`),
switching to `Normal` first if necessary before applying geometry (many
window managers ignore explicit geometry changes while a window is still
maximized/fullscreen).

**Why**: "remember its own last position/size" (the previous behavior) is
not the same as "appear where the list currently is" — if the player has
the list window maximized or moved since the media window was last shown,
a media window that only remembers *its own* history reappears in the wrong
place, disconnected from what the player is actually looking at. The
client's local "always fullscreen" media preference (a persisted setting)
still overrides this afterward if the player has it checked.

## Video playback starts *after* the window is shown, not before

**Decision**: `ClientMainWindowViewModel.OnMediaBegin` raises
`MediaWindowShouldShow` (which creates/sizes/shows the window) **before**
calling `StartVideo` (which calls `MediaPlayer.Play`).

**Why**: `VideoView` embeds a native child window handle (via
`NativeControlHost`), not a pure Avalonia-rendered surface. Starting
playback into a `MediaPlayer` before its `VideoView` is attached to a
window of its final size means the native rendering surface gets resized
*during* active playback once the window geometry sync runs — for
high-resolution video specifically, this could leave the window at a wrong
size and/or the video rendering behind the rest of the window instead of
in it. Realizing the window (and syncing its final geometry) first, then
starting playback, avoids resizing a live native surface altogether. Sounds
don't go through this path at all (see below), so this only concerns Video.

## Sounds are fully decoupled from the Image/Video "current media" slot

**Decision**: Audio media (`MediaKind.Audio`) never touches
`CurrentMediaKind`/`CurrentMediaLocalPath`/the shared `MediaPlayer` field on
either Host (`MainWindowViewModel`) or Client
(`ClientMainWindowViewModel`), and never opens/shows the media window
(`PlayerWindow`/`MediaWindow`). Instead, every sound gets its own
short-lived `MediaPlayer` instance
(`PlayLocalSoundIfNeeded`/`OnLocalSoundEnded` on the Host,
`PlaySound`/`OnSoundEndReached` on the Client), tracked in a
`Dictionary<mediaId, MediaPlayer>` so any number of sounds can play at once.
`BeginVideoTracking` (Host) and the media-window-showing branch in
`OnMediaBegin` (Client) only ever match `MediaHeaderDto.MediaKindVideo`.

**Why**: an early version routed Audio through the same single-slot
Video/Image pipeline (reusing `CurrentMediaKind` and
`BeginVideoTracking`'s pending-media tracking). That caused three bugs
directly reported by the user: (1) a sound showed up in the player window
just like an image/video, even though a sound has nothing to visually
show; (2) only one sound could play at a time, because starting a second
one reused/replaced the first sound's `MediaPlayer`; (3) worse, a sound's
own end-of-playback resolution (`ResolvePendingVideo`) unconditionally
called `ClearMediaCommand`, which closed/replaced whatever image or video
happened to be showing at the time the sound finished - a completely
unrelated video could get cut off just because a background sound ended.
Fully decoupling sounds from that slot avoids all three: no window, no
single shared player, and no shared "current media" to accidentally clear.

**Update**: the original version of this decision accepted a trade-off — no
way to stop a looping sound once sent. That gap is now closed (see
"Sounds get their own stop/volume protocol" below): every sound, looping or
not, is tracked in `MainWindowViewModel.ActivePlayingSounds` and stoppable
individually or in bulk, at the Host and every connected client.

## The Sound Library is a separate collection from the Image/Video Library

**Decision**: `MainWindowViewModel.SoundLibrary` (backed by
`SoundLibraryItemViewModel`, its own storage directory
`ThemeSettingsService.SoundLibraryDirectory`, and its own `settings.json`
section) is a completely separate collection from `MediaLibrary`
(image/video only, `MediaLibraryItemViewModel`). They get their own tab
("Sounds" vs. "Library") each with their own add/export/import buttons.
`MediaTypeHelper.TryGetKind` returning `MediaKind.Audio` is now actively
*rejected* by `AddMediaToLibraryFromPathAsync` (and vice versa: non-audio is
rejected by `AddSoundToLibraryFromPathAsync`).

**Why**: sounds and images/video are conceptually different things sent
through different UI (single-click-to-send for sounds vs. double-click for
media, per-sound volume vs. no volume concept for images/video, a "Test"
preview button that only makes sense for sounds) and different runtime
behavior (fully parallel vs. single-slot, see above). Keeping one mixed
collection with a `Kind` discriminator (the pre-existing design) meant every
UI element needed `IsVisible` gates keyed on `Kind`, and it invited exactly
the kind of accidental cross-talk the previous decision above had to fix.
Splitting the collection outright removes the discriminator entirely instead
of gating on it everywhere.

**Trigger media is the one exception**: `TriggerMediaConfig` (the optional
image/video/audio a Timer/Alarm/Interval can auto-send on fire) still picks
a file directly from disk via `TriggerMediaFileType`, not from either
library — it has no "library" concept of its own, so there was nothing to
split there.

## Sounds get their own stop/volume protocol, decoupled from playback-ended tracking

**Decision**: two new Host→Client RPC methods, `media.stopSound` and
`media.setVolume` (both keyed by `mediaId`, see `protocol.md`), let the Host
end or adjust one specific, already-in-flight sound anywhere it's playing
(a connected client, or the Host's own local preview). Unlike `media.begin`/
`media.cleared`, these never touch the image/video slot and were added
*only* for sounds.

**Why**: once sounds could run in unlimited parallel (see above), the GM had
no way to see what was still playing, let alone stop a specific one (e.g. a
looping ambience track) without waiting for it to end naturally. The
"Currently Playing Sounds" panel (`MainWindowViewModel.ActivePlayingSounds`)
is the visible half of this; the RPC pair is what makes "Stop" and the live
volume slider actually reach wherever the sound is really playing, rather
than only affecting Host-side bookkeeping.

**Consequence for the item-triggered "Sound" field**: Timer/Alarm/Interval's
existing per-item `Sound` dropdown (previously always a local-only blip via
`SoundService.Play`) now branches on whether the chosen name resolves to a
Sound Library entry (`SoundService.TryGetLibrarySoundPath`). A library
entry is sent to players exactly like any other sound (tracked in
`ActivePlayingSounds`, stoppable); the 3 built-in tones (Pling/Glocke/
Digital) keep the original local-only behavior unchanged. `SoundRepeatCount
== 0` (infinite, see the dedicated decision below) maps directly to
`MediaHeaderDto.Loop = true` for a library sound — the existing "stop this
element's sound when its Reset fires" wiring
(`MainWindowViewModel.StopInfiniteSoundLoop`) was extended to also stop the
associated network sound via `_itemSoundMediaIds`, so the behavior stays
consistent regardless of which kind of sound is configured. A fixed repeat
count > 1 intentionally sends the library sound only *once* (`Loop = false`)
rather than reproducing the built-in tones' repeat-with-gap behavior — that
mechanic exists for near-instant blips, and doesn't translate well to
potentially much longer library sounds.

## Removing a sound never deletes the underlying settings-file entry silently

**Decision**: the old "+ Add Sound" Settings-tab flow
(`SoundService.AddCustomSound`/`RegisterCustomSounds`, backed by
`ThemeSettingsService.CustomSounds`) was removed outright in favor of the
Sound Library. Existing entries are migrated into `SoundLibrary` exactly
once at startup (`MainWindowViewModel` constructor, gated by
`ThemeSettingsService.ThemeSettingsDto.LegacyCustomSoundsMigrated`) so
nobody silently loses a previously-added sound; `CustomSounds` itself is
kept in the DTO purely so old `settings.json` files still deserialize.

**Why**: two independent sound-adding flows (one for network-sent
Library sounds, one for local notification blips) would have meant two
places to manage largely the same kind of data, and no way for a
notification sound to also be sendable to players. Merging them into one
Sound Library — with the built-in tones staying as the only remaining
"local-only" option — was simpler for the user and for the code.

## Concurrent media sends are serialized on the wire, but playback is not

**Decision**: `TcpPlayerServerService.PublishMediaAsync` wraps the entire
header+chunks transfer to all clients in a `SemaphoreSlim(1,1)`
(`_mediaSendLock`), so two full media transfers (e.g. two sounds sent in
quick succession) can never interleave their frames on the same TCP
connection.

**Why**: `NetworkFrame.TypeMediaChunk` carries no `MediaId` - chunks are
matched to a transfer purely by arrival order, on the assumption that only
one media streams at a time per connection (see the comment on
`NetworkFrame.TypeMediaChunk`). Supporting truly parallel sound *playback*
(the actual user-visible goal) doesn't require parallel *transfer*: sound
files are small and transfer in a fraction of a second, so queuing full
transfers one after another is imperceptible, while already-fully-received
sounds keep playing independently in their own `MediaPlayer` regardless of
what's currently being transferred. Tagging chunks with a MediaId to allow
genuinely interleaved transfers was considered and rejected as unnecessary
complexity for the actual requirement.

## Fullscreen state is never touched by clearing/resetting media

**Decision**: `MainWindowViewModel.ClearMedia()` does not modify
`RemoteFullscreen` or push `display.fullscreen` in any way.

**Why**: this reverses an earlier "fix". A prior bug report ("PlayerWindow
doesn't return to the list view after showing an image") was diagnosed —
incorrectly, it turned out — as fullscreen getting stuck on, and `ClearMedia`
was changed to auto-revert fullscreen whenever it had been auto-set by an
event trigger. In practice this meant resetting a timer (which closes its
own triggered media via `StopMediaIfTriggeredBy`) or simply clicking
"Remove" would *also* silently drop a fullscreen view the GM wanted to
keep (e.g. "show the player list fullscreen for the rest of the session").
Fullscreen is now purely opt-in/opt-out via the explicit toggle
(`ToggleRemoteFullscreenCommand`) or an event trigger's `Fullscreen` flag
turning it *on* — clearing media never turns it back off as a side effect.

## Discovery: LAN broadcast and mDNS results are merged, not overwritten

**Decision**: `MdnsDiscoveryService.DiscoverAsync` keys results by
`host:port` across *both* discovery mechanisms; if the same server answers
via both, the entry's `DiscoverySource` flags are OR'd together
(`Lan | Mdns`) instead of the second mechanism's result replacing the
first's. A second, coarser pass then merges any *remaining* duplicates by
port alone (`MergeByPort`), and the UI shows the combined source
(`SourceLabel`: "LAN", "mDNS", or "LAN + mDNS") as a small hint next to each
entry.

**Why**: a server on the same LAN commonly answers both probes, and the
goal is exactly one list entry per physical server, not one per discovery
mechanism. Keying by `host:port` alone turned out to be insufficient on its
own: `PlayerMdnsAnnouncer` (mDNS) and `LanDiscoveryResponder` (LAN
broadcast) used to resolve "our own local IP" two different ways — a naive
"first Up, non-loopback interface" enumeration for mDNS vs. a
socket-routing lookup (`Connect()` a UDP socket toward the actual asker,
then read back the OS-chosen local endpoint) for LAN. On any machine with
virtual adapters present and "Up" (Hyper-V, WSL, Docker, VPN — common on
Windows dev machines), the naive enumeration could pick a different,
non-LAN-facing adapter than the routing-based lookup, so the *same* Host
process reported two different IPs depending on which mechanism answered —
producing two `host:port` keys, and two entries, for one server.
`PlayerMdnsAnnouncer.GetBestLocalIPv4` now uses the same routing-based
technique as the LAN responder (connecting toward a fixed external-ish
target instead of a specific asker, since mDNS replies are multicast, not
addressed to one client) so both mechanisms report the same IP in the
common case. The port-based second pass exists as a belt-and-suspenders
fallback for the rare remaining case where the two routing lookups still
diverge (e.g. genuinely different routes to different targets on a
multi-homed machine) — safe here specifically because this app's real-world
usage is one Host per LAN, so "same port" is a reasonable proxy for "same
server" even without a shared instance identifier in the wire format.

## Timer trigger media is stopped by its own timer's reset — nothing else's

**Decision**: `TimerItemViewModel.Reset()` fires a `MediaResetRequested`
event; `MainWindowViewModel` tracks which `TriggerMediaConfig` (if any)
currently-active media was triggered by (`_activeTriggerMediaSource`,
compared by reference) and only clears media if the resetting timer is that
same source.

**Why**: without tracking *which* item triggered the current media,
resetting an unrelated timer could not distinguish "this timer's own
triggered image" from "some other media the GM sent separately" — either
resetting would need to never touch media (leaving a stale triggered image
on screen after its timer resets) or it would risk clearing media it didn't
cause. Reference-equality tracking against the config object solves this
without needing IDs or timestamps.

## "Player Fullscreen" drives the Host's own PlayerWindow too, not just remote clients

**Decision**: every place that changes `MainWindowViewModel.RemoteFullscreen`
(the manual `ToggleRemoteFullscreenCommand`, stopping the network server, and
an event trigger's `Fullscreen` flag) now goes through a single
`SetRemoteFullscreen(bool)` helper that both pushes `display.fullscreen` to
remote clients *and* raises `LocalFullscreenRequested`, which
`MainWindow.axaml.cs` uses to set the Host's own `PlayerWindow.WindowState`.

**Why**: "Player Fullscreen" previously only meant "fullscreen for connected
network clients" — the Host's own `PlayerWindow` (used for solo preview or
when running the display on a second local monitor without a separate
PlayerClient instance) had no button that affected it at all, so a GM
running the display locally rather than over the network had no fullscreen
control. Routing every fullscreen change through one helper keeps
`RemoteFullscreen` (the single source of truth shown in the UI label) and
the local window's actual `WindowState` from ever drifting apart, the same
way a previous fix already keeps `ClearMedia()` from silently touching
fullscreen (see above).

## Escape (and, for the Host, a dedicated button) exits fullscreen locally only, never touching shared state

**Decision**: `PlayerWindow` (Host), `ClientMainWindow`, and `MediaWindow`
(both PlayerClient windows) each install a local `KeyDown` handler that, if
`WindowState == FullScreen` and the key is `Escape`, sets
`WindowState = Normal` on that window only. `PlayerWindow` additionally shows
a small "Exit Fullscreen (Esc)" button, visible only while it is actually
fullscreen (tracked via overriding `OnPropertyChanged` for
`WindowStateProperty`, matching the existing pattern in
`Views/Controls/DateTimeInput.axaml.cs`). Neither Escape nor the button
touches `RemoteFullscreen`, `MediaFullscreen`, or sends any RPC.

**Why**: fullscreen on either side is driven by shared, persisted/broadcast
state (`RemoteFullscreen` on the Host, `MediaFullscreen`/a Host-pushed
`display.fullscreen` on the client) — but a *local* recovery path is still
needed for the case where that state gets out of sync with reality (a stuck
toggle, a window that fullscreened on the wrong monitor, or simply wanting a
quick local peek without changing what every other screen shows). Making the
escape purely local and non-destructive to the shared flag means the next
legitimate fullscreen push (a new trigger-media event, the GM's toggle
button, or the next medium the client receives) still fullscreens correctly
afterward — Escape is a momentary local override, not a persistent
opt-out, mirroring the "fullscreen is never silently turned off as a side
effect" rule above.

## GM-authored Designs are JSON files loaded alongside the built-in compiled ones, not a replacement for them

**Decision**: `RpgTimeTracker.Shared.Theming.ThemeDefinitionDto` describes a
Design (colors, gradient brushes, fonts, corner radii, named background
images, button images) as data. `ThemeDefinitionLoader` scans two
directories for `<id>/theme.json` folders — `SampleThemes/` shipped next to
the exe, and `%AppData%/RpgTimeTracker/Themes/` for the GM's own — and
builds an Avalonia `ResourceDictionary` from each at runtime, using the
*exact same resource keys* (`AccentBrush`, `WindowChromeBrush`,
`WindowBackgroundImageBrush`, …) as the hand-written `Themes/*.axaml` files.
`ThemeService.ApplyCustom` swaps this dictionary in exactly like
`ThemeService.Apply` does for a built-in `AppTheme`. The 8 existing
`Themes/*.axaml` files were **not** migrated to this format — they stay as
fast, compiled, hand-tuned XAML, and the JSON loader is purely additive.

**Why**: rewriting 8 already-polished, hand-tuned Designs to a
data-driven format risks visually regressing all of them for the sake of a
feature only some GMs will use. Keeping both systems side by side — compiled
XAML for the built-ins, JSON+loose-image-files for anything GM-authored —
means zero risk to the existing Designs while still giving the GM a fully
equivalent, editable format. The 8 `SampleThemes/*/theme.json` files are a
mechanical extraction of the built-in Designs (see
`extract_themes.py`-style parsing of the `.axaml` XML) and exist specifically
so a GM can copy one, rename its `Id`, and edit colors/images without
starting from a blank schema.

**Update**: custom Designs are now pushed to clients too. The wire value for
`theme.changed` and `session.snapshot.Theme` is either a plain `AppTheme`
enum name or `"custom:<Id>"` (`MainWindowViewModel.GetCurrentThemeWireValue`).
`ClientMainWindowViewModel.ApplyTheme` recognizes the `custom:` prefix and
resolves it via the same `ThemeDefinitionLoader.LoadAll()` the Host uses,
applying it through the client's own `ClientThemeService.ApplyCustom`. This
only works if the client has the same `theme.json` available locally — the
file itself is still never transferred over the wire, only its `Id`. Both
apps ship the same 8 `SampleThemes/*/theme.json` (duplicated per-project as
Content, since each app is a separate published output), so any built-in
sample Design syncs correctly out of the box. A GM's own custom Design
(under `%AppData%/RpgTimeTracker/Themes/`) only syncs automatically to a
PlayerClient running on the *same machine or a synced profile* — for a
Design used across multiple physical player machines, the GM currently has
to copy the `theme.json` (and its images) to each one manually. If the
client doesn't have the referenced Design at all, it logs a warning and
keeps showing whatever Design it had before, rather than silently falling
back to a Design the GM didn't choose.

## Time-of-day ambience automation only swaps backgrounds within the active Design, and only if it has more than one

**Decision**: `MainWindowViewModel.UpdateAmbience()` (called every clock
tick) checks whether the active Design is a JSON one with 2+ entries in
`Backgrounds`; if so, it sets `WindowBackgroundImageBrush` to the first
entry during 06:00–18:00 game time and the last entry otherwise. Nothing
else about the Design changes, and built-in Designs (always exactly one
background) are untouched by this logic.

**Why**: the original ask was "each Design has several background images
that automatically cycle by time of day, independent of Design choice."
Investigating the existing `Assets/Backgrounds/*.png` files turned up extra
variants per Design (`*-scene-bg`, `*-markant-bg`, plain `*-bg`) that looked
like they might already be day/night alternates — but opening them showed
most are unfinished draft art or unrelated debug/mockup screenshots (e.g.
`shadowrun-markant-bg.png` is a UI mockup with text labels like "RUN TIMER"),
not usable background scenes. Only the Medieval sample has a second
legitimately finished image (`medieval-scene-bg.png`, a night castle) to
pair with its existing day scene. Rather than wiring in broken-looking art
or blocking the whole feature on new art that doesn't exist yet, the
automation is built against the general case (any Design's `Backgrounds`
list, however long) and ships wired up for the one Design that already has
real day/night art — every other Design works exactly as it did before
until someone (the GM) adds a second named background to its `theme.json`.

## Local-only video pause resolves via the same MediaPlayer.EndReached race as the remote report

**Decision**: `MainWindowViewModel.CreateLocalMediaPlayer` hooks
`MediaPlayer.EndReached` to call `ResolvePendingVideo(_pendingVideoMediaId)`,
the same method the client's `media.playbackEnded` RPC calls.

**Why**: `BeginVideoTracking`'s clock-pause-until-video-ends feature
previously only resolved accurately when a network client was connected and
reporting back — a solo/local-only session (Host's own `PlayerWindow`, no
PlayerClient attached) fell back to a generic LibVLC-probed-duration timeout,
which is an estimate, not the real end of playback. Hooking the Host's own
`MediaPlayer.EndReached` gives the exact same "whichever source resolves
first wins" behavior locally that already existed for remote clients,
without duplicating any pause/resume logic.

## Sound repeat count is a per-item setting, not a global one

**Decision**: `TimerItem`/`AlarmItem`/`IntervalEventItem` each gained a
`SoundRepeatCount` (default 1), and `SoundService.Play` loops that many times
with a fixed gap. `SoundService`'s external-process playback path
(`TryRunPlayer`, used for non-WAV formats) now waits for the player process
to exit instead of firing-and-forgetting it.

**Why**: repeats need to be sequential, not simultaneous, or a repeat count
> 1 would just spawn N overlapping players at once for non-WAV sounds. This
was previously fine because nothing ever played the same sound twice in a
row; adding repeats made the fire-and-forget assumption incorrect, so
waiting for process exit became necessary specifically for this feature.

## SoundRepeatCount == 0 means infinite, cancelled only by the triggering item's own Reset

**Decision**: `SoundService.Play` gained an optional `CancellationToken`; a
`repeatCount <= 0` loops until that token is cancelled instead of a fixed
number of times. `MainWindowViewModel` keeps a `Dictionary<Guid,
CancellationTokenSource>` (`_infiniteSoundLoops`) keyed by the triggering
Timer/Alarm/IntervalEvent's `Id`. `PlaySound` always cancels any existing
entry for that Id first (so a fresh trigger — e.g. a repeating IntervalEvent
re-entering its active window — replaces rather than stacks loops), then
starts a new infinite one if `repeatCount == 0`. Each of the three
ViewModels gained a `ResetRequested`/`MediaResetRequested` event fired from
their Reset/Dismiss command, which `MainWindowViewModel` subscribes to
specifically to call `StopInfiniteSoundLoop(itemId)` — deleting the item
(`RemoveTimer`/`RemoveAlarm`/`RemoveIntervalEvent`) also stops it, since
there would otherwise be no way to silence a loop for an item that no
longer exists.

**Why**: an unbounded repeat count needs an explicit, reliable way to stop,
and the user specified exactly one: resetting the item that triggered it.
Keying by the item's `Id` (rather than, say, a single "currently looping"
field) means multiple items can each have their own independent infinite
loop running at once without stepping on each other, and looking it up by
Id is the natural key already used everywhere else in this ViewModel
(`PublishTimelineItemRemovedAsync`, `StopMediaIfTriggeredBy`'s sibling
concept for media). Clamping logic that used to force any value `<= 0` up
to `1` (in each ViewModel's constructor, `OnSoundRepeatCountChanged`, and
`FromDto`) had to change to only reject *negative* values, since `0` is now
a meaningful, distinct setting rather than an input error.

## Time-jump undo is a per-session LIFO stack of raw deltas, not a full state snapshot

**Decision**: `MainWindowViewModel.JumpBy` pushes the applied `TimeSpan`
delta onto `_jumpUndoStack`; `UndoJumpCommand` pops it and calls
`_clock.Jump(-delta)`.

**Why**: every jump path (quick-jump buttons, the free-jump textbox in
either direction, "jump to next event", jump markers) already funnels
through the single `JumpBy` method, so capturing undo there covers all of
them for free. Storing the delta (not a time snapshot) means undo is just
"apply the opposite delta," which reuses the exact same `GameClockService.Jump`
path — and therefore the exact same `Jumped` event — that a normal jump
uses, so connected clients follow an undo exactly like any other jump with
no extra protocol work.

## The GM-local heads-up warning and session log never cross the network, by construction

**Decision**: `HeadsUpMessage` (a transient, auto-clearing banner shown a
configurable lead time before a Timer/Alarm/IntervalEvent fires) and
`SessionLogEntries` (an opt-in, in-memory list of game-time-stamped events,
exported only via an explicit file-save dialog) are both plain
`MainWindowViewModel` state with no `Publish*Async` call anywhere near them.

**Why**: both features are explicitly GM-facing conveniences — a spoiler
warning and a private recap — that must never leak to the player-facing
side of the app. Not wiring them to `TcpPlayerServerService` at all (rather
than wiring them and gating on a flag) makes "never sent to players" a
structural guarantee instead of a behavior that depends on a check staying
correct. The session log is additionally opt-in (`RecordSessionLog`) so nothing
is retained in memory at all unless the GM turns it on, and export always
asks for a destination file rather than writing to a fixed path — mirroring
the existing Save/Load file-dialog pattern instead of introducing a
new persistence convention.

## Host/Client player UI is shared as markup via an interface, not by merging the two ViewModels

**Decision**: `RpgTimeTracker.Shared.Visuals.IPlayerDisplayContext` and
`IPlayerTimelineEntry` are implemented by `MainWindowViewModel`/
`TimelineDisplayItemViewModel` (Host) and `ClientMainWindowViewModel`/
`RemoteTimelineItemViewModel` (Client) respectively. Two Avalonia
UserControls in `RpgTimeTracker.Shared/Views`
(`PlayerHeaderView`/`PlayerTimelineListView`) bind against those interfaces
and are embedded in both `PlayerWindow.axaml` and `ClientMainWindow.axaml`.
`TimelineEntries` is typed as plain `System.Collections.IEnumerable` (not
`IEnumerable<IPlayerTimelineEntry>`) specifically so each side can return its
own `ObservableCollection<T>` directly — a generic return type would need a
`.Cast<>()`-style wrapper, which doesn't implement `INotifyCollectionChanged`
and would silently stop the shared list from live-updating.

**Why**: the two ViewModels already had identical property names and types
for every field these controls need (`PlayerHeaderTitle`, `CurrentGameTimeText`,
`Name`, `KindLabel`, `Progress`, …) — purely a historical accident of one
being modeled after the other, not a plan — so extracting an interface
needed zero renames on either side, only adding `: IPlayerDisplayContext`/
`: IPlayerTimelineEntry` to four existing classes. Merging the two window
ViewModels into one shared type was considered and rejected: they diverge in
real, load-bearing ways beyond display (the Host owns the authoritative
clock and the TCP server; the Client derives its clock locally and owns the
connection/discovery state), and forcing them into one class would have
meant threading a "which side am I" branch through methods that have no
business knowing.

## The two shared player-UI controls don't own item state styling, only structure

**Decision**: `PlayerTimelineListView`'s item template sets
`Classes.active`/`Classes.completed`/`Classes.blink`, but defines no style
rules for what those classes actually look like. Both `PlayerWindow` and
`ClientMainWindow` keep their own local `<Window.Styles>` overrides for
`Border.item.active`/`.completed`/`.blink` (Host's PlayerWindow previously
named its "active" state `.pulse` — renamed to `.active` for this
extraction, no visual change, just aligning the class name with the
already-shared markup).

**Why**: an earlier, pre-extraction decision deliberately gave the Host's
own windows a filled-background item style and the Client a border-color-only
style, for readability reasons specific to each context. Sharing the actual
`Border`/`Grid` structure (the goal of this extraction) is orthogonal to
that — Avalonia style selectors cascade from a `Window`'s own `Styles` down
through embedded `UserControl`s regardless of which XAML file the elements
live in, so keeping the state-class *rules* local while sharing the
state-class *usage* preserves the old visual distinction with none of the
old markup duplication.

## The "new item" basics (icon/name/sound/color/blink/visible) are a ViewModel, not three copies of the same bindings

**Decision**: `RpgTimeTracker.ViewModels.NewItemBasicsViewModel` holds
`Icon`/`Name`/`Sound`/`SoundRepeatCount`/`ColorHex`/`Blink`/`IsPlayerVisible`.
`MainWindowViewModel` exposes one instance each —
`NewTimerBasics`/`NewAlarmBasics`/`NewIntervalBasics` — replacing 21 separate
`NewTimerXxx`/`NewAlarmXxx`/`NewIntervalXxx` properties. A single shared
`Views/Controls/NewItemBasicsEditor.axaml` binds against this type and is
placed once in each of the three "+ Timer"/"+ Wecker"/"+ OnTime" tabs
(`DataContext="{Binding NewTimerBasics}"` etc.), each opening its own
`IconPickerWindow` directly from its own code-behind rather than dispatching
through a `MainWindowViewModel.SetAddBlockIcon(target, icon)` string switch
(removed).

**Why**: the three "add new element" tabs had identical markup for this
block, differing only in which flat property each field bound to — the kind
of duplication that only exists because there was no shared type to bind
against. Introducing `NewItemBasicsViewModel` gives the control something
real to bind to, and lets `ResetToDefaults()` replace what used to be 5-8
individual property resets after each `AddTimer`/`AddAlarm`/`AddIntervalEvent`
call. The per-item-kind wording differences that existed before ("Blink
on expiry" vs. "on trigger" vs. "while active") were dropped in favor of
one generic "⚡ Blinken" label — a deliberate small wording simplification to
keep the control genuinely reusable rather than parameterizing every string.

## Settings live in their own tab, not the always-visible sidebar

**Decision**: Design/Ambience, Player-window title,
heads-up-warning config, session-log config, and network/connected-clients
moved from the permanently-visible left "Chronometer-Steuerung" sidebar into
a fourth tab ("Einstellungen") alongside "Element Anlegen"/"Elementliste"/
"Bibliothek" (later joined by a fifth, "Sounds" — see the Sound Library
decisions above). The clock display, start/pause, speed, jump controls, and
jump markers stay in the sidebar.

**Why**: this block kept growing as features were added in this session
(heads-up warning, session log, ambience automation, connected-clients list)
until it was pushing the actually-time-critical clock controls further down
an always-scrolled sidebar. None of these are things a GM touches
mid-encounter the way they touch the clock/jump controls, so they belong
behind a tab click, not permanently in view.

## Library export/import is a folder + manifest, not a single archive file

**Decision**: `MainWindowViewModel.Export{Media,Sound}LibraryAsync`/
`Import{Media,Sound}LibraryAsync` copy each library's referenced files
as-is into/from a user-picked folder, alongside one `bibliothek.json`
manifest (name, relative filename, MIME type, `Kind` for media, `Loop`,
`Volume` for sounds). Import re-copies each file into the app's own managed
directory under a fresh GUID name (never reads directly from the picked
folder afterward) and appends to the existing library rather than
replacing it.

**Why**: this exists specifically so a library can be backed up or handed to
another machine independently of the automatic `settings.json` persistence
under `%AppData%`, per an explicit user request — a plain folder (not a
zip/archive) keeps the implementation to "copy files + write/read one JSON
file", avoiding a compression/archive dependency for what's fundamentally a
rare, manual, low-volume operation. Re-copying on import (rather than
referencing the picked folder in place) keeps the same invariant every
other library entry already has: the app's managed directory is the only
thing that has to stay alive for playback to keep working, regardless of
whether the user later moves or deletes the folder they imported from.

## Transient/changing state moved to a fixed-height status bar, not inline tab content

**Decision**: a persistent bar was added at the very bottom of the Host
window (`MainWindow.axaml`, 4th row of the outer `Grid`, sibling to the
sidebar+`TabControl` row — so it's visible regardless of which tab is
active), showing: server status (running/stopped, LAN address:port,
connected-client count — `MainWindowViewModel.ServerStatusSummary`,
`NetworkServerAddress` sourced from `PlayerMdnsAnnouncer.GetBestLocalIPv4`,
made `public` for this), the current image/video with a close button
(`HasMedia`/`CurrentMediaFileName`/`ClearMediaCommand` — unchanged bindings,
just relocated), and a sound-count button whose `Flyout` reveals the full
"currently playing" list with per-sound/bulk stop. The inline "aktuelles
Medium" row that used to live in the Bibliothek tab, and the whole second
column of the Sounds tab that used to hold this list, were both removed.

**Why**: both of those inline spots grew/shrank the surrounding `StackPanel`/
tab content as media started/stopped or sounds began/ended — exactly the
"layout shifts while I'm trying to do something else" complaint this was
built to fix. A fixed-height bar outside the tab content can't do that: its
own row is `Auto`-sized once (whatever its widest possible content needs)
and individual chips inside it only appear/disappear within their own
column, which doesn't reflow anything to either side. Putting the detailed,
variable-length sound list behind a `Flyout` (Avalonia's floating overlay
control — the first use of `Flyout`/`Popup` in this codebase) keeps that
same guarantee: a `Flyout` renders above the content instead of participating
in its layout, so opening/closing it or the list growing while open never
moves anything underneath.

## Per-library-item settings live behind a "⚙" Flyout, not inline on the tile

**Decision**: `MediaLibraryItemViewModel` tiles now show a static Name label
(only a small "⚙" button opens a `Flyout` with the rename `TextBox`) instead
of an always-editable inline `TextBox`. `SoundLibraryItemViewModel` tiles
went further: Volume/Loop/RepeatCount/trim-start/trim-end/"SL-Test" all
moved off the tile into that same kind of `Flyout`, leaving the tile itself
down to icon + name + delete + gear.

**Why**: both libraries render their items in a `WrapPanel` — every tile's
size directly determines where every *other* tile ends up. An inline editor
that grows a tile when you start editing it (which is what the earlier,
denser Sound tile design was heading toward once Loop/RepeatCount/trim
needed a place to live) would reflow every tile after it, the same
layout-shift problem the status bar decision above addresses, just for
library tiles instead of transient state. A `Flyout` anchored to the gear
button sidesteps it identically: it floats over the grid without occupying
space in it, so a tile's on-screen size — and therefore every other tile's
position — never changes based on what's being edited or how much settings
UI a given library type happens to have.

## Sounds support a finite repeat count and start/end trim, not just infinite loop

**Decision**: `MediaHeaderDto` gained `RepeatCount` (default 1, only
meaningful when `Loop = false`) and `TrimStartMs`/`TrimEndMs` (0 = no trim
at that end). Both Host (`PlayLocalSoundIfNeeded`/`OnLocalSoundEnded`) and
Client (`PlaySound`/`OnSoundEndReached`) now track *remaining* repeats per
`mediaId` (`-1` sentinel for infinite/Loop, otherwise counts down to 0
before the sound is allowed to really end) instead of a plain bool, and
apply trim via `VlcMediaService.ApplySoundTrim` — two LibVLC media options,
`start-time`/`stop-time`, added once per `Media` object before `Play()`.

**Why**: `RepeatCount` is a small, natural generalization of the existing
"Loop = play forever" flag — some sounds want to play exactly N times
(a knock, a short sting) rather than either once or forever, and the
mechanism (stop, replay the same `Media` in place) is identical to Loop
minus the "forever" part, so it shares the EndReached handler rather than
needing a separate code path. Trim is the ability to reuse a longer sound
file (a music track, an ambience recording) for just one segment of it,
without needing a separate audio-editing step outside the app — done via
LibVLC's own start/stop-time options rather than pre-trimming the file on
disk, so the *original* file is always what's stored/exported, and the trim
points can be changed later without re-encoding anything.
`VlcMediaService.ApplySoundTrim` formats the seconds value with
`CultureInfo.InvariantCulture` deliberately: VLC parses these options like
a command line, and a locale that uses `,` as its decimal separator (e.g.
German) would otherwise silently produce a nonsense value.

## Image/video gallery: append instead of replace, navigated locally per client

**Decision**: `MainWindowViewModel.SentMediaItems` accumulates every ad-hoc/
library image or video actually sent (`MediaHeaderDto.AddToGallery = true`)
instead of each new send replacing/deleting the previous one. Each connected
`ClientMainWindowViewModel` (and the Host's own `PlayerWindow` preview)
keeps its *own* local copy of whatever it has received and navigates that
list independently — Left/Right arrow keys or a click in the media window
(`MediaWindow`/`PlayerWindow`'s panel) move through it with **no round-trip
to the Host**. The Host can "highlight" one item
(`media.highlight`/`HighlightSentMediaItem`), which is a *suggestion* — every
client jumps to it but can immediately navigate away again locally
afterward. `media.retract` removes one item everywhere; nothing is deleted
from a client's local cache until that arrives.

**Why**: this exists so the GM can flip back to something shown five
minutes ago without re-sending it, and so a player who got distracted can
scroll back through what's already been shown independently, without
waiting on or disrupting anyone else. Local, no-round-trip navigation was a
deliberate choice over a server-authoritative "current index" — the
alternative (Host tracks one global position, players request next/previous
from it) would mean one player's Back button could yank the image away from
everyone else's screen. Retaining received files as long as they're in the
list, and only deleting on explicit retract, is what makes "navigate back"
free of a fresh transfer — see `StartVideo`/`OnMediaCompleted`'s `_gallery`
checks before deleting a temp file, and `AddOrUpdateGalleryEntry` reusing an
existing slot by `MediaId` when the same item is shown again instead of
recording a duplicate.

**Event-media priority**: `TriggerMediaConfig`-sourced sends never set
`AddToGallery` and are therefore never part of this list. On the client,
`_isShowingEventMedia` (Host: `_activeTriggerMediaSource` unchanged from
before) blocks gallery navigation while one is on screen — consistent with
"images and videos from events take priority" — and per explicit user
decision, the gallery does **not** auto-resume the instant the event media
naturally ends; it only resumes when the *triggering* Timer/Alarm/Interval
is explicitly reset (`StopMediaIfTriggeredBy` → `ResumeGalleryAfterEventMedia`),
matching the pre-existing trigger-media lifecycle rather than adding a new one.

**Auto-advance timer**: `SlideshowSecondsPerImage` (0 = manual) is
broadcast via `media.slideshowInterval` and drives an *independent*
`DispatcherTimer` on the Host's own preview and on every client — not a
single shared clock — so a slow/stalled client doesn't hold up anyone
else's advancement. It only ever calls the same local Next as a manual
arrow-key press would, and is skipped for a whole tick if the current item
is a video (a video has its own natural duration; forcibly cutting away
from a still-playing video was explicitly ruled out).

**Known limitation**: a client that connects after several images/videos
have already been sent only gets whatever the Host's existing single-item
catch-up cache (`_lastMedia`) holds — not the full gallery history. Backing
the catch-up snapshot with the whole `SentMediaItems` list was out of scope
for this change; worth revisiting if late-joining players need the same
scrollback as those connected from the start.

## Bibliothek and Sounds tabs split into two columns: authoring vs. live state

**Decision**: both the "Bibliothek" and "Sounds" tabs in `MainWindow.axaml`
use a `Grid ColumnDefinitions="*,*"` instead of one stacked `StackPanel`.
Bibliothek: left column keeps the ad-hoc send section and the Medienbibliothek
(add/export/import/tile list); right column holds the "Aktuell gesendete
Bilder/Videos (Galerie)" section (slideshow-seconds input + gallery tiles).
Sounds: left column keeps the ad-hoc "Sound an Spieler senden" section; right
column holds the Sound-Bibliothek (add/export/import/tile list).

**Why**: each tab was mixing two different kinds of content in one vertical
stack — a one-time "author/configure" section and a list whose *size* changes
constantly at runtime (gallery grows/shrinks as items are sent/retracted;
sound library grows as entries are added). Stacked vertically, the
lower section's position kept moving whenever the section above it changed
size. Splitting into columns gives each its own independent vertical extent,
matching the two-column pattern already established for the Einstellungen
tab — same `Grid ColumnDefinitions="*,*"` / `Margin="0,0,10,0"` +
`Margin="10,0,0,0"` structure, reused rather than inventing a new layout.

## About screen: shared view, license text loaded from one file, not retyped

**Decision**: both apps gained an "ℹ" entry point (Host: Chronometer-
Steuerung header row, `MainWindow.axaml`; Client: connection-status bar,
`ClientMainWindow.axaml`) opening a small `AboutWindow`. The window itself is
just a thin per-app wrapper (`Views/AboutWindow.axaml` in each project) around
one shared `RpgTimeTracker.Shared.Views.AboutView` UserControl, following the
same "shared View, per-app host Window" pattern already used for
`PlayerHeaderView`/`PlayerTimelineListView`. `AboutViewModel` (Shared) takes
app name/description as constructor arguments from each `AboutWindow` (they
legitimately differ per app) but derives the two things that must **not** be
duplicated from actual data instead of hand-typed strings:
- **Version**: `assembly.GetName().Version`, read via reflection from
  whichever app's own assembly is passed in — sourced from the single
  `<Version>` property in the new repo-root `Directory.Build.props` (inherited
  by all three `.csproj`), not copy-pasted into each project file.
- **License text**: the pre-existing `THIRD-PARTY-NOTICES.txt` at the repo
  root (already written for exactly this purpose — see its own header
  comment) is read from `AppContext.BaseDirectory` at runtime. It's linked
  (not copied) into `RpgTimeTracker.Shared.csproj` as `Content` with
  `CopyToOutputDirectory`; the .NET SDK's Content-propagation-through-
  `ProjectReference` then copies it into both Host's and Client's own output
  directories automatically, with the physical file staying at the repo root
  as the one and only copy.

## Client auto-reconnect: backoff loop, only after an unexpected drop

**Decision**: `PlayerTcpClientService` now remembers the last connected
host/port and, if the connection ends WITHOUT an explicit `Disconnect()`
call, automatically retries connecting to the same host/port with a
growing backoff (2s → 20s, ×1.5 per failed attempt), reporting progress via
the existing `StatusChanged` event ("Verbindung verloren - erneuter
Versuch in Xs ..."). A `_userDisconnectRequested` flag (set by `Disconnect()`,
cleared at the start of every `ConnectAsync`) distinguishes "connection
died, please recover" from "the user meant to disconnect" - only the
former schedules a reconnect. No new state-sync plumbing was needed for
"reconnected clients get a complete update": the Host already sends a full
`session.snapshot` to every newly-accepted TCP connection
(`TcpPlayerServerService.SendCatchUpAsync`), and a reconnect *is* a new TCP
connection from the server's point of view, so this falls out for free.

**Why**: the existing idle-watchdog (15s) and connect-timeout (5s) already
detected a dead connection reliably, but detection without recovery still
left the player staring at a frozen client until someone manually clicked
"Verbinden" again - easy to miss mid-session. Backoff instead of a fixed
retry interval avoids hammering a genuinely offline host every 2 seconds
indefinitely, while still recovering a brief WLAN hiccup or Host restart
within a few seconds. Reconnect only reuses the *existing* connect path
(`ConnectAsync`) rather than a separate code path, so it gets the same
5s connect-timeout and idle-watchdog behavior as any other connection
attempt for free.

## Client prompts to delete temp media files on close

**Decision**: `ClientMainWindow.OnClosing` checks
`ClientMainWindowViewModel.HasTempFilesToClean` (gallery non-empty or an
ad-hoc video temp file still referenced); if true, it cancels the close,
shows a minimal Yes/No `ConfirmCleanupWindow` (first modal dialog in this
codebase - kept deliberately small rather than introducing a general
dialog framework nothing else needs yet), and only re-closes afterward.
Answering "yes" calls `DeleteAllTempFiles()`, which reuses the existing
`ClearGallery()` plus a defensive sweep of `Path.GetTempPath()` for any
`rpgtimetracker-media-*` files left over from a prior ungraceful exit
(crash, task-kill) that never got to clean up after itself.

**Why**: sound temp files were already deleted immediately after playback,
and the gallery was already cleared on every disconnect - so by the time
the app is closing there's usually little left, but "little" isn't "none"
(a currently-open image/video, or a gallery accumulated during a still-open
session). Asking rather than always deleting matches the explicit request
("only on request") and leaves room for a player who wants to keep a received
image after closing. The orphan-sweep goes a step further than the literal
ask because leftover temp files from a crash would otherwise never get
cleaned up at all (nothing else in the app ever revisits them), which is
exactly the "clutter" scenario being guarded against.

## Server name broadcast in the LAN/mDNS announcement

**Decision**: `LanDiscoveryResponder`'s `Name` field (previously hardcoded
to `"RpgTimeTracker"`) and a new `name=<value>` entry in
`PlayerMdnsAnnouncer`'s existing TXT record are now both driven by a
`ServerName` setting (Host, Einstellungen tab, persisted via
`ThemeSettingsService`, applied when the server is (re)started). The
Client's `MdnsDiscoveryService` already consumed `LanDiscoveryResponse.Name`
for the LAN path; the mDNS path previously only derived a name from the
fixed DNS instance string (always literally "RpgTimeTracker"), so TXT-record
parsing (type 16) was added to `TryParseResponse`, preferring the `name=`
value when present.

**Why**: the LAN responder already had a `Name` field ready to use - it
just wasn't wired to anything configurable, and the mDNS side's TXT record
already existed (app/mode/version) but was never read by the client at
all. Reusing both existing-but-unused pieces of plumbing was simpler and
lower-risk than inventing a new discovery field. The DNS instance name and
hostname themselves were deliberately left untouched (still fixed strings)
to avoid touching the already-working PTR/SRV matching logic
(`IsQueryForOurService`) for a feature that's purely cosmetic (a friendly
label in the client's server list, useful once more than one Host runs on
the same LAN).

## In-game calendar: hybrid month grid + entry list, shared toggle, event-media reuse

**Decision**: a "Kalender" main tab on the Host lets the GM create entries
(`CalendarEntryDefinition`: title, description, icon, color, start date/time,
recurrence [None/Daily/Weekly/Monthly/Yearly], optional end date, optional
image/video/sound trigger, per-entry player-visible flag). The shared
`PlayerTimelineListView` (used by both Host's `PlayerWindow` and the Client's
`ClientMainWindow`) gained a small segmented "Zeitliste | Kalender" toggle in
its header, visible only when `HasPlayerVisibleCalendarEntries` is true - a
month grid + entries-for-selected-day panel replaces the timeline when
toggled. Calendar entries sync via the existing full-resync mechanism
(`SessionSnapshotParams.CalendarEntries`, sent on every client connect and
after any debounced edit) and persist both in the normal save-state
(`AppStateDto.CalendarEntries`) and via separate JSON export/import. Firing
a calendar entry's trigger reuses the same `TriggerEventMedia`/
`SendSoundFromPathAsync`/`PlaySoundLibraryItem` paths Timer/Alarm/Interval
already use, so it automatically gets the same event-media-takes-priority
behavior as everything else.

**Why**: this was largely built already before this decision was recorded;
the only actual defect was `IPlayerDisplayContext.CalendarMonthDays`/
`CalendarEntries` being declared as `ObservableCollection<CalendarEntryViewModel>`
(the Host's *editable* entry type) when both implementations actually
backed them with the separate read-only `PlayerCalendarDayViewModel`/
`PlayerCalendarEntryViewModel` collections - explicit interface
implementations must match the interface's declared type exactly, so this
was a hard compile error in both `MainWindowViewModel` and
`ClientMainWindowViewModel`. Fixed by typing both interface members as
plain `IEnumerable`, mirroring the already-established `TimelineEntries`
pattern (which exists for the same reason: Host and Client don't share a
single concrete item type for the timeline, only for the calendar day/entry
cells). Reusing the full-resync/save-state/event-media machinery instead of
building calendar-specific equivalents was the right call already made -
new plumbing here would have duplicated behavior (sync-on-change,
new-client catch-up, media-priority) that already exists and is already
exercised by three other feature areas.

**Why color was added after the fact**: Timer/Alarm/Interval already carry
a per-item `ColorHex` (rendered as an accent border strip in
`PlayerTimelineListView`); calendar entries initially had only an icon.
Added the same `ColorHex`/`ColorBrush` pair to `CalendarEntryDefinition`,
`CalendarEntryViewModel`, and `PlayerCalendarEntryViewModel`, rendered as
the same 6px accent strip pattern in both the Host's entry-for-selected-day
list and the shared player-facing entry list - kept off the month-grid day
cells themselves, since a day can contain multiple differently-colored
entries and tinting the whole cell would be misleading with more than one.
The Start date/time field was also switched from a free-text
`"yyyy-MM-dd HH:mm:ss"` `TextBox` to the existing `DateTimeInput` control
(already used for Wecker) - the control's `Text` property already produces/
consumes exactly that format, so no `CalendarEntryViewModel` changes were
needed, only the `TextBox` element swapped in `MainWindow.axaml`.

**Why**: the request was specifically to avoid license data existing in two
places. Re-typing a curated package/license list into a new view would have
recreated exactly that problem (and risked drifting from the legally-precise
text already in `THIRD-PARTY-NOTICES.txt`), so the About screen reads that
file verbatim into a read-only, monospaced, scrollable `TextBox` rather than
reformatting it into some new structured representation. Reusing the
Directory.Build.props version property the same way avoids the same
duplication problem for the version number across three csproj files.

## Tabs restyled as an underline strip, not individually boxed buttons

**Decision**: `TabItem` (shared `AppStyles.axaml`) no longer has its own
per-tab border/background/corner-radius at rest; it's flat and muted until
`:pointerover`/`:selected`, where the selected tab gets a bold label, a
rounded-top "card" background (`SelectedTabBrush`) and an accent-colored
bottom underline. `TabControl WrapPanel` background stays transparent.

**Why**: with every `TabItem` boxed identically regardless of state, the
strip read as a row of independent buttons with no visual hint that they're
mutually-exclusive views of the same panel. The underline-plus-lifted-card
pattern (browser tabs, VS Code tabs) makes the active/inactive relationship
obvious at a glance without introducing new theme resources - it reuses
`MutedTextBrush`/`TextBrush`/`TabBrush`/`SelectedTabBrush`/`AccentBrush`,
which every existing theme JSON already defines.

## Library tiles get a shared ViewModel base + a shared panel View

**Decision**: `MediaLibraryItemViewModel` and `SoundLibraryItemViewModel`
now derive from `LibraryItemViewModelBase<TSelf>` (CRTP), which owns
`Name`/`LocalPath`/`MimeType`/`DeleteCommand` and an `_onChanged` callback
invoked via a protected `NotifyChanged()` the derived classes call from
their own property-changed partial methods. Separately, the "toolbar chrome"
around each library (title, description, add button, export/import,
`WrapPanel` list, empty-state text, add/export/import file-dialog logic) is
now one shared `RpgTimeTracker.Views.Controls.LibraryPanelView` UserControl,
used twice (Bibliothek tab for Media, Sounds tab for Sound) with only the
tile `DataTemplate` and a handful of per-library settings (dialog title,
file-type filter, three `Func<string, Task>` delegates wired in
`MainWindow.axaml.cs`'s `OnDataContextChanged`) differing between the two.

**Why**: earlier this session the two libraries' XAML chrome was near-
identical but fully duplicated, and a follow-up question about extracting a
base was deliberately left unresolved pending this decision. `TSelf`-generic
CRTP (rather than a non-generic `Action<LibraryItemViewModelBase>`) was
chosen specifically so the *existing*, concretely-typed callback methods in
`MainWindowViewModel` (e.g. `RemoveMediaLibraryItem(MediaLibraryItemViewModel)`)
keep compiling unchanged via method-group conversion - no call site needed to
change type. `LibraryPanelView` exposes its bindable surface
(`Title`/`Description`/`ItemsSource`/`ItemTemplate`/`HasNoItems`/
`EmptyStateText`/`ErrorMessage`) as `StyledProperty`s and its own internal
XAML binds to them via `$parent[controls:LibraryPanelView].Property` -
required because the control's *inherited* `DataContext` stays the outer
`MainWindowViewModel` (so `ItemsSource="{Binding MediaLibrary}"` still works
from the call site), so the panel's own XAML can't just bind to its
`DataContext` for its own configuration. File-dialog code (`OpenFilePickerAsync`/
`OpenFolderPickerAsync`) moved into the control's own code-behind, using
`TopLevel.GetTopLevel(this)` (which correctly resolves to the owning Window
from a nested `UserControl`), so it no longer needs to live in
`MainWindow.axaml.cs` at all - Export/Import additionally collapsed into a
single "⋮" overflow `MenuFlyout` per library instead of two always-visible
buttons, decluttering the primary "Add" action.

## Host-side fullscreen toggles merged into the status bar

**Decision**: the local "toggle own GM-preview fullscreen" icon (previously
in the Chronometer-Steuerung header) and the remote "push fullscreen to
clients" button (previously a full-width button in the Bibliothek tab's
ad-hoc send section) both moved into the persistent status bar as a pair of
fixed-size icon buttons. The remote one gets `Classes.active="{Binding
RemoteFullscreen}"` (new `Button.iconsquare.active` style, accent-colored
background) instead of changing size/label to show its on/off state.

**Why**: both were fullscreen-related "current live display state" controls
that had ended up in unrelated locations (one in a generic header action
row, one hanging off an unrelated send form) - exactly the kind of control
the status bar already exists to collect (see the earlier "moved to a
fixed-height status bar" decision). Using an accent-colored fill for the
active state rather than swapping label text keeps the button's footprint
constant, preserving the status bar's hard "never resizes" constraint.

## Media windows show a persistent clock chip and a custom minimize button

**Decision**: `PlayerWindow`'s local media overlay (Host) and the Client's
standalone `MediaWindow` both gained (a) a small dark semi-transparent chip
in the top-left bound to `CurrentGameTimeText`, and (b) a "−" minimize
button next to the existing "✕" close button, top-right.

**Why**: both windows can run fullscreen/borderless (no OS title bar), and
the local media overlay in particular fully occludes the header clock
underneath it while a picture/video is showing - so "what time is it in the
game right now" became unanswerable exactly while a scene was up. A custom
minimize button exists for the same reason the close button already does:
OS-native minimize isn't reachable without a title bar.

## About screen's own content is Markdown, rendered via ClassIsland.Markdown.Avalonia

**Decision**: each app's title/description text for the About screen lives
in its own `About/AboutContent.md` (embedded `AvaloniaResource`, not
`Content`/`CopyToOutputDirectory` - unlike `SampleThemes`, this isn't meant
to be user-editable post-install), loaded via `AssetLoader.Open(avares://...)`
in that app's `AboutWindow.axaml.cs` and passed into `AboutViewModel` as a
plain markdown string (`ContentMarkdown`). `AboutView` renders it with
`Markdown.Avalonia`'s `MarkdownScrollViewer`, exactly like the very first
attempt - but the package behind that namespace is now
`ClassIsland.Markdown.Avalonia` instead of the original
`whistyun/Markdown.Avalonia`. `AboutViewModel.AppName`/`AppDescription` were
removed earlier - the markdown's own `# Heading` serves as the title.

**Why (this decision has now flip-flopped twice)**: the original choice was
`whistyun/Markdown.Avalonia` 11.0.3, accepting a larger dependency footprint
for full Markdown support. That crashed at runtime with a
`MissingMethodException` inside its own precompiled internal styles the
first time the About window was opened: it only declares `Avalonia >=
11.0.0` with no upper bound, so NuGet happily resolved it against this
project's actual `Avalonia 12.0.5` - but Avalonia's markup-XAML compiler ABI
changed between those major versions, so its precompiled XAML called an API
that no longer exists. The only Markdown.Avalonia build that even claims
Avalonia 12 support is an unlisted `12.0.0-a3` alpha with no stable
successor. First fix was to drop the dependency entirely for a small
hand-rolled `MiniMarkdownRenderer` (plain `TextBlock`/`Run`, understanding
only the tiny subset `AboutContent.md` actually uses) - correct and safe,
but unsatisfying as a long-term answer for a screen that's supposed to
render Markdown. Searched NuGet again and found
**`ClassIsland.Markdown.Avalonia`**: a community fork of the exact same
library (same original authors credited, same MIT license, same
`Markdown.Avalonia` assembly/namespace - literally a drop-in replacement,
same `xmlns:mdxaml`/`<mdxaml:MarkdownScrollViewer>` usage as the very first
attempt), except its sub-packages explicitly declare `Avalonia >= 12.0.0`
- meaning it was actually built and shipped against the same Avalonia major
version this project uses, unlike the original's unbounded dependency
range. Removed `MiniMarkdownRenderer` (dead code once the real library
worked) and switched to the fork. Its transitive tree is similar to the
original attempt's (ColorTextBlock/ColorDocument.Avalonia,
Markdown.Avalonia.Html/Svg/SyntaxHigh/Tight, HtmlAgilityPack, ExCSS,
Avalonia.AvaloniaEdit, Svg.Controls.Avalonia/Custom/Model/SceneGraph,
ShimSkiaSharp), all reflected in `THIRD-PARTY-NOTICES.txt` (MIT except
Svg.Custom, which is MS-PL).

## About screen: one scrollable region, description and licenses in one Markdown document

**Decision**: `AboutView` no longer has a separate, height-capped `TextBox`
for `THIRD-PARTY-NOTICES.txt` alongside its own `MarkdownScrollViewer` for
the app description. `AboutViewModel.CombinedMarkdown` concatenates
`ContentMarkdown` + a `---` rule + a `## Bibliotheken & Lizenzen` heading +
`ThirdPartyNoticesText` into one string, rendered by a single
`MarkdownScrollViewer` that fills the whole window below the small
"Version X.Y.Z" line at the top.

**Why**: the previous `DockPanel` had the licenses `ScrollViewer` and a
`Separator` both docked `Bottom` while the description `MarkdownScrollViewer`
was the (undocked) fill child - `DockPanel` processes docked children in
declaration order, so those `Bottom`-docked elements claimed space starting
from the bottom of whatever the `Top`-docked elements had already left,
squeezing the licenses box down to a sliver whenever the description content
was tall enough to need most of the remaining space. A single Markdown
document sidesteps the whole problem: one scroll region, no independent
height budgets fighting each other. `THIRD-PARTY-NOTICES.txt` isn't
"real" Markdown, but it was checked for characters that would misrender
(no underscores anywhere in the file, and its single stray backtick is
unpaired so CommonMark renders it literally rather than opening a code
span) - and its existing Setext-style section headings (a title line
followed immediately by a row of `-` or `=`) are exactly Markdown's syntax
for h1/h2 headings, so they render as real headings instead of plain text.
The long, deeply-indented legal clauses inside the full Apache/LGPL/MS-PL
texts may render slightly differently than plain monospace (Markdown's
list-continuation/indentation rules aren't identical to the original
plain-text layout), which is an accepted tradeoff for gaining actual
heading structure and a single unified scroll experience.

## Combined session.hello handshake for PIN + protocol version

**Decision**: a new connection doesn't get added to the Host's `_clients`
list (and therefore isn't visible anywhere, isn't a broadcast target, and
doesn't get the `session.snapshot` catch-up) until it has completed a
`session.hello`/`session.helloRejected` handshake. The client sends
`session.hello` (its `ProtocolInfo.Version` + its configured PIN, empty
string if unset) immediately after the TCP connection is established, before
`ReadLoopAsync` processes anything else. `TcpPlayerServerService.
PerformHandshakeAsync` waits up to 5s (`HandshakeTimeout`) for that message
via a `TaskCompletionSource<SessionHelloParams>` on `ClientConnection`
(`HelloTcs`, resolved from `HandleClientRpcFrame`'s `SessionHello` case),
then checks the protocol version against `ProtocolInfo.Version` and the PIN
against the Host's `ConnectionPin` setting (skipped entirely if that setting
is empty). Any failure — timeout, version mismatch, wrong PIN — sends
`session.helloRejected` with a human-readable reason and disposes the
connection without ever touching `_clients`.

**Why**: PIN/passcode and protocol-version checking were originally two
separate TODO items, but both need to happen at the exact same point (before
a client is treated as "real") and both produce the same shape of outcome
(accept or reject-with-reason), so one handshake message covers both instead
of two independent pre-checks. The PIN defaults to empty/disabled so existing
setups keep working without any action — this is a LAN-trust convenience
feature, not real authentication (the PIN travels in cleartext JSON over a
plain TCP socket), intended only to stop a random device on the same Wi-Fi
from accidentally joining. A protocol-version mismatch is a hard rejection
rather than a warning, because a wire-format change that an older client
can't parse would otherwise fail unpredictably deep inside RPC deserialization
instead of with one clear message. On the client, receiving
`session.helloRejected` calls the existing `Disconnect()` (which sets
`_userDisconnectRequested = true`) specifically to suppress the auto-reconnect
loop — retrying immediately with the same wrong PIN or the same incompatible
build would just loop forever otherwise.

## About screen: own license and third-party notices in collapsed Expanders

**Decision**: `AboutView` no longer runs the app description and both license
texts through a single combined Markdown document (`CombinedMarkdown` was
removed). Instead `AboutViewModel` exposes `ContentMarkdown`, `LicenseText`
(the project's own `LICENSE`, newly loaded the same way
`ThirdPartyNoticesText` already was) and `ThirdPartyNoticesText` as three
separate strings. The view wraps the whole page in one outer `ScrollViewer`
containing a `StackPanel`: the description `MarkdownScrollViewer` is always
visible, followed by two `Expander`s (default collapsed) - "Eigene Lizenz
(MIT)" and "Bibliotheken & Lizenzen (Drittanbieter)" - each containing its
own `MarkdownScrollViewer`.

**Why**: once the project's own `LICENSE` was added alongside the existing
third-party notices, the combined document became three walls of legal text
stacked in front of (or after) the actual app description, all forced open at
once - exactly the "didn't always want to scroll through everything" problem the
single-scroll-region fix from the previous section was meant to solve for a
*different* squeeze bug, but which reappeared here in a new form once there
was more content. Expanders let both license texts default to closed, so the
screen opens on just the description; nesting the per-section
`MarkdownScrollViewer`s inside the outer `ScrollViewer`'s infinite-height
`StackPanel` means each one sizes to its own content instead of claiming a
fixed height budget, so there's no repeat of the old `DockPanel` space-fight
- the outer `ScrollViewer` is the only thing that actually scrolls.

## Time-jump undo/redo as a delta stack, not a state snapshot list

**Decision**: `MainWindowViewModel` tracks time-jump history as two
`Stack<TimeSpan>` fields (`_jumpUndoStack`/`_jumpRedoStack`) holding the
*deltas* that were applied, not the resulting `DateTime`s or any broader
state snapshot. `UndoJump`/`RedoJump` just apply `-delta`/`delta` again via
`GameClockService.Jump` and move the popped value to the other stack.
`ApplyManualDateTime` (the free-text time entry) now computes its own delta
against the current time and pushes it through the same `PushJump` helper
used by every jump button and jump marker, instead of calling
`_clock.SetTime` directly and bypassing the history - so setting the clock
by hand is undoable exactly like every other jump. A fresh jump (`JumpBy`)
clears the redo stack; loading a save file clears both stacks entirely.

**Why**: a delta is enough to reconstruct/undo a jump because
`GameClockService` has no other time-jump side effects worth snapshotting -
timers/alarms/intervals derive their displayed state from the current game
time on every tick rather than storing "time when jump happened," so
replaying `Jump(-delta)`/`Jump(delta)` correctly un-triggers/re-triggers
whatever needs it for free. Storing full `DateTime` snapshots would also
work, but the delta form is what the original single-step undo already used
(`_jumpUndoStack` was already a `Stack<TimeSpan>` capable of holding more
than one entry, so repeated clicks on "undo" already unwound several jumps
in a row - the actual gaps were that there was no way back (no redo stack),
manual time-setting silently bypassed the stack entirely, and neither stack
was ever cleared on file load, so a stale undo could in theory jump back
into a `DateTime` from before the currently loaded timers/alarms/jump
markers existed).

## Release prep: untrack .idea, clean informational version, thin CI

**Decision**: for the `1.0.0-alpha` GitHub release, the tracked
`.idea/.idea.RpgTimeTracker/.idea/*` files (JetBrains Rider project
metadata) were removed from git and `.idea/` added to `.gitignore` instead.
`Directory.Build.props` now sets `<IncludeSourceRevisionInInformationalVersion>
false</IncludeSourceRevisionInInformationalVersion>` so the About screen's
version text reads a clean `1.0.0-alpha` instead of the SDK's default
`1.0.0-alpha+<git-sha>`. `.github/workflows/build.yml` is a deliberately
minimal `dotnet restore` + `dotnet build -c Release` matrix over
`windows-latest`/`ubuntu-latest` - a compile check, not a publish/release
pipeline.

**Why**: IDE project files are per-developer noise in a public repo (they
regenerate locally and differ across Rider installs/OSes), not project
state worth reviewing in diffs or forcing on contributors who use a
different editor. The commit-SHA suffix on the informational version is
useful for internal build traceability but is not something an About
dialog aimed at GM/players needs to show - "1.0.0-alpha" answers "is this
the version I think it is" without exposing implementation detail. The CI
was scoped to "does it still build on both OSes" rather than a full
publish/artifact pipeline because that's the actual failure mode worth
catching automatically (a change that silently breaks the Linux build,
say, by referencing a Windows-only API) - producing signed, versioned
release binaries is a separate, explicit action better done deliberately
per release than on every push.

## Themes: all designs go through the JSON loader, no more compiled AXAML themes

**Decision**: the 16 `Themes/*.axaml` files (8 per app, Host + PlayerClient)
and the `AppTheme` enum are gone. `ThemeService`/`ClientThemeService` now
have a single `Apply(ThemeDefinitionDto, folderPath)` method - there is no
`Apply(AppTheme)` anymore. The 8 designs that used to be compiled-in now
ship purely as their existing `SampleThemes/*/theme.json` files (which
were already byte-for-byte equivalents, kept in sync as JSON "documentation
copies" of the AXAML - see the older "GM-authored Designs" README section).
`ThemeDefinitionDto` gained an optional `PickerLabel` field so the style
picker can keep showing a short neutral name ("Shadowrun / Cyberpunk")
instead of each design's flashy in-fiction `DisplayName` ("SHADOWRUN //
HOST-LAYER CHRONO", used for the in-app hero title) - custom GM designs
without a `PickerLabel` just fall back to `DisplayName`, unchanged from
before. `ThemeDefinitionLoader.Resolve(rawId)` is a new single entry point
that every consumer (Host/Client startup, `MainWindowViewModel`,
`ClientMainWindowViewModel`) uses to turn a stored/received theme value
into a design, understanding older values (a bare PascalCase enum name
like `"PostApocalyptic"`, or the even older `"custom:<id>"` prefix) so
existing `settings.json`/save files keep resolving to the same visual
design without any migration step.

**Why**: two parallel theme systems (compiled AXAML + JSON loader) existed
only because the JSON loader was added later, alongside the original 8
designs, rather than replacing them - by the time both were confirmed
functionally identical, keeping both was pure duplication (every visual
tweak to a built-in design had to be made twice, once in AXAML XML and once
in the JSON copy, or they'd silently drift - which is exactly what nearly
happened here: the Shadowrun/Alien "PulseBrush" color-toning-down change
below was applied to the JSON samples only, since that's now the only copy
that exists). Collapsing to one mechanism removes that drift risk entirely
and shrinks the Host/Client `ThemeService` classes to one method each.

## Shadowrun/Alien active-timer color: pastel instead of saturated neon

**Decision**: `PulseBrush`/`PulseBorderBrush` (the active-timer highlight,
`Classes.pulse` in `MainWindow.axaml`/`PlayerWindow.axaml`) changed from a
fully saturated neon teal (Shadowrun, `#E020F5C8`) / neon mint
(Alien, `#C056FFB0`) to a desaturated pastel version in the same hue
(`#C078C9BE` / `#C08ED9B4`), with `PulseTextBrush` darkened slightly to
keep contrast. Only these two `SampleThemes/*/theme.json` files changed
(both projects, kept identical) - the other 6 designs' pulse colors were
left as-is.

**Why**: the original neon values read as visually "loud"/alarming for a
highlight that's shown continuously on every running timer, not just at
the moment it fires - pastel keeps the same hue identity (still reads as
"Shadowrun cyan" / "Alien green") while being calmer to look at for
extended periods. Scoped to just these two designs since they were the
ones flagged as too aggressive; the same technique (reduce saturation,
raise lightness, keep hue) is the template to follow if another design's
accent ever needs the same treatment.

## File layout: role-based folders with domain subfolders, not domain-only folders

**Decision**: within `RpgTimeTracker.Shared` and `RpgTimeTracker`, folders
now group by role first (`Models/`, `Services/`, `ViewModels/`, `Views/`)
and by subject second, as a subfolder (`Models/Rpc/`, `Services/Rpc/`,
`Models/Network/`, `Services/Network/`, `Models/Theming/`,
`Services/Theming/`, `Services/Logging/`, `Services/Visuals/`,
`Models/Persistence/`, `Views/Converters/`), rather than one flat
subject-only folder (`Rpc/`, `Network/`, `Theming/`, `Logging/`, `Visuals/`,
`Persistence/`, `Converters/`) mixing data and behavior together. Concretely:
`RpcMethods`/`RpcParams`/`ProtocolInfo` (pure data/constants) moved to
`Models/Rpc/`, while `RpcMessage`/`RpcNotification<T>` (the actual
serialize/parse behavior, still one file, `RpcEnvelope.cs`) moved to
`Services/Rpc/` - same split for `Network` (`MediaLimits`/`PlayerNetworkDtos`
→ `Models/Network/`, `NetworkFrame` → `Services/Network/`) and `Theming`
(`ThemeDefinitionDto` → `Models/Theming/`, `ThemeDefinitionLoader` →
`Services/Theming/`). `IPlayerDisplayContext`/`IPlayerTimelineEntry` (MVVM
display contracts, not visual/rendering code) moved out of the old
`Visuals/` folder into `ViewModels/`, since that's what they actually are;
`VisualItemHelper` (genuinely visual: icon geometry lookup/caching) moved
to `Services/Visuals/`. `CalendarEntryDefinition` (a plain data class with
no MVVM behavior, used directly as a persisted DTO member of
`AppStateDto`) moved out of `ViewModels/` into `Models/`, where it belongs.

**Why**: a handful of files were genuinely misplaced by role (most
notably `CalendarEntryDefinition` sitting in `ViewModels/` despite being
pure data) - fixing only those and leaving `Rpc/`/`Network/`/`Theming/` as
mixed-role folders would still leave data and behavior interleaved in the
same folder, which is the actual thing "sort by role" is meant to prevent.
Every consumer file's `using` list grew by a line or two (some now need
both `Models.X` and `Services.X` where they used to need only `X`) - a
real cost, but a small and mechanical one, and it's compiler-checked: a
misplaced or missing `using` fails the build immediately rather than
compiling into a subtly wrong reference. `Rpc`/`Network`/`Theming`/`Visuals`
were kept as subfolder names (not flattened away) because they're
meaningful domain groupings worth preserving - `Models/Rpc/RpcParams.cs`
still tells you at a glance "this is protocol data," which a flat
`Models/RpcParams.cs` next to fifteen unrelated DTOs would not.

## SampleThemes: one copy in Shared, not one per app

**Decision**: `SampleThemes/` (the 8 shipped JSON designs) now lives only
in `RpgTimeTracker.Shared/SampleThemes/`, with a single `Content Include`
in `RpgTimeTracker.Shared.csproj`. The Host and PlayerClient `.csproj`
files no longer have their own `SampleThemes\**\*.*` content entries -
MSBuild's content propagation across `ProjectReference` (already used for
`THIRD-PARTY-NOTICES.txt`/`LICENSE.txt`) copies the whole nested folder
structure into both apps' output directories automatically, verified by
building each app separately and checking `SampleThemes/shadowrun/theme.json`
exists at the same relative path in both outputs.

**Why**: the two copies were physically identical files that had to be
hand-kept-in-sync on every edit (this already happened once, when the
`PickerLabel` field and the Shadowrun/Alien pulse-color tweak were added
in the same session - each change had to be applied twice and diffed to
confirm the folders still matched). Since `ThemeDefinitionLoader.SampleThemesDirectory`
already resolves relative to `AppContext.BaseDirectory` (i.e., wherever
the running app's own files are), a single source copied to both outputs
works identically to two maintained copies, with no runtime difference -
purely removes a manual-sync burden that had already caused near-misses.

## Event-Medium (image/video) must come from the Media Library

**Decision**: Timer/Alarm/IntervalEvent "Event-Medium" image/video
selection no longer opens a disk file picker - it opens a new
`MediaLibraryPickerWindow` (modeled on the existing `IconPickerWindow`:
a modal list, double-click to choose) listing `MediaLibrary` items only.
Audio-as-event-medium is unaffected and still uses a disk file picker
(`ChooseTriggerAudioAsync`), now scoped to audio extensions only instead
of the old combined "image, video, or audio" filter. Each of the four
"Event-Medium" UI blocks (New Timer, New Alarm, New Interval, and the
existing-item edit flyout) got a second small button ("Audio…") alongside
the renamed "Aus Bibliothek…" button, since one `TriggerMediaConfig` can
hold either kind but they now come from two different sources.

**Why**: this closes the gap the delete-warning feature (see below) exists
to guard - if event-media images/videos could still point at arbitrary
disk files outside the library, "is this library item currently in use"
would only catch coincidental path matches, not the general case, and a
user could still lose event-media files silently by deleting/moving them
outside the app entirely. Restricting to the library also matches what
Calendar entries already did (`SelectedCalendarTriggerMediaItem`, a
`MediaLibrary`-bound `ComboBox` added earlier) - Timer/Alarm/Interval were
the remaining inconsistent case. A modal picker window (not an inline
`ComboBox` like Calendar's) was chosen specifically because the
existing-item edit flyout is driven from code-behind Click handlers
operating on whichever `TimelineDisplayItemViewModel` is in that flyout's
DataContext - there's no single `SelectedXxx` property to bind a `ComboBox`
to for "whichever item's flyout happens to be open," so a callback-based
modal (`Action<MediaLibraryItemViewModel>`, exactly like `IconPickerWindow`'s
`Action<string>` constructor) is the pattern that already fits all four
call sites uniformly.

## Deleting an in-use library image/video: warn, then choose keep-file vs. delete-everywhere

**Decision**: `MainWindowViewModel.RemoveMediaLibraryItemAsync` now scans
Timers/Alarms/IntervalEvents/CalendarEntries for a `TriggerMedia.Path`
matching the library item's `LocalPath` (`FindTriggerMediaUsageLabels` -
matched by path, explicitly never by the item's display `Name`, which is
freely user-editable and has no connection to file identity) before
deleting. If nothing references it, deletion is unchanged. If something
does, a new `ConfirmTriggerMediaDeleteWindow` (three buttons: Cancel, "Datei
behalten", "Aus Elementen entfernen + löschen") blocks the deletion until
the user picks:
- **Aus Elementen entfernen + löschen**: clears `TriggerMedia` on every
  referencing item first (`ClearTriggerMediaReferences`, same effect as
  manually clicking each item's existing "✕ Event-Medium entfernen"
  button), then removes the library entry and deletes the file as before.
- **Datei behalten**: removes only the library list entry/manifest row -
  the file itself is left on disk, so the referencing items keep working
  exactly as before, just no longer manageable from the library UI (an
  intentionally orphaned file).

Since the ViewModel can't open a `Window` itself, the confirmation is
wired the same way `LibraryPanelView`'s `AddItemAsync`/`ExportAsync` funcs
already are: `MainWindowViewModel.ConfirmTriggerMediaDeleteAsync` is a
settable `Func<...>` property, assigned from `MainWindow.axaml.cs`'s
`OnDataContextChanged` to a method that shows the dialog and returns the
chosen `TriggerMediaDeleteChoice`. Without a handler assigned (e.g. a
future unit test constructing the ViewModel directly), the default is
`Cancel` - an in-use item can never be silently deleted just because
nothing happened to be listening.

**Why**: `TriggerMediaConfig` stores a live filesystem path, not its own
copy of the file (confirmed by reading `SendTriggerMediaAsync`, which does
`File.ReadAllBytesAsync(path)` fresh every time a trigger fires) - so
before this change, deleting a library image that an item's Event-Medium
happened to point at (which, after the library-only restriction above, is
now the *only* way Event-Medium can be set) would silently break that
item the next time it fired, with no error, no log message, just a no-op
(`TriggerEventMedia`'s `!File.Exists(config.Path)` early-return). "Keep
the file" is the option that makes an orphaned-but-working file a
deliberate, informed choice instead of an invisible side effect the user
never agreed to.

## Resource-based English/German localization

**Decision**: the entire codebase (UI strings, code comments, log
messages, documentation) is standardized on English, with a proper
switchable localization system layered on top for the UI - not a one-way
hardcoded replacement. English is the default/fallback language; German is
a selectable alternative, matching the `todo.md` Lokalisierung TODO's
original design intent ("Host und PlayerClient müssen nicht dieselbe
Sprache verwenden").

The architecture deliberately mirrors the existing JSON-based theme system
almost exactly, since both solve the same shape of problem (swap a set of
named values into the UI at runtime, per-installation, without a rebuild):
`LocalizationService` (`RpgTimeTracker.Shared/Services/Localization/`) is
a static class that loads a flat `Dictionary<string,string>` from
`en.json`/`de.json` (`RpgTimeTracker.Shared/Localization/`, copied to both
apps' output directories via the same `<Content Include>` MSBuild pattern
already used for `SampleThemes/`), builds an Avalonia `ResourceDictionary`
keyed by dotted identifiers (e.g. `"MainWindow.Clock.SectionTitle"`), and
merges/swaps it into `Application.Current.Resources.MergedDictionaries` on
`Apply(string? language)` - so XAML binds via `{DynamicResource Some.Key}`
and updates instantly on a language switch, exactly like
`ThemeService.Apply`. A `Get(string key)` static method covers the
non-XAML-bound case: C# code that assigns a string directly to a
ViewModel property (error messages, status/log text shown via
`ShowActionStatus`/`RecordSessionEvent`, default item names like "New
Timer"/"Neuer Timer"). English is always loaded as a fallback merge for
any key missing from the active non-English language, so a partially
translated language file degrades gracefully instead of showing raw keys.

Interpolated strings (e.g. `$"Timer expired: {timer.Name}"`) can't be
looked up directly through a flat key→string dictionary, since the
dictionary only stores static text. These became format-string resource
values (`"Timer expired: {0}"` / `"Timer abgelaufen: {0}"`) invoked via
`string.Format(LocalizationService.Get("Key"), value)` - a pattern applied
consistently across every ViewModel with dynamic status/error text.

Host and PlayerClient each persist their own `Language` setting
independently in their existing settings DTOs (`ThemeSettingsDto`/
`ClientSettingsDto`) - no network sync of language preference, unlike
Theme which the Host actively pushes to connected Clients. This is
intentional: the GM's own authored names (timer/alarm names, jump
markers, calendar entries) are freely typed by the GM and sent over the
protocol as plain strings regardless of either side's UI language, so
Host and PlayerClient genuinely don't need to agree on a language.

A discovered XAML limitation along the way: `MultiBinding`/plain
`Binding` with `StringFormat="{DynamicResource ...}"` does **not** work
as a way to localize a format string - `StringFormat` is a plain CLR
property in Avalonia (not itself bindable/dynamic-resource-aware),
mirroring the same limitation in WPF. The fix was to split a composite
label into two separate `TextBlock`s (a `{DynamicResource}`-bound static
label + a `{Binding}`-bound dynamic value) instead of ever attempting a
localized composite format string in XAML - used first for the About
screen's version line, then for the header labels/values across
`ClientMainWindow.axaml` and `MainWindow.axaml`.

Migrating the ~330 remaining hardcoded UI strings (beyond the About-screen
proof-of-concept) was mechanical enough to parallelize: each area (a
view + its ViewModel) was assigned to its own pass, choosing a
`<ViewName>.<Area>.<Descriptor>` key naming convention (e.g.
`MainWindow.Clock.SectionTitle`, `ClientMainWindowViewModel.Alarm.DetailFormat`)
and reporting the new key→text pairs back rather than editing the shared
`en.json`/`de.json` directly, so the two JSON files could be assembled and
reconciled in one consistent pass afterward instead of risking merge
conflicts between concurrent edits to the same file. A handful of
genuinely shared concepts (the media-window minimize/close/gallery-nav
tooltips, the alarm "now"/"once" labels, the interval status labels) were
deliberately given one shared key each and reused across Host and
PlayerClient rather than duplicated per-file, matching the project's
existing preference for shared code over near-duplicate copies.
