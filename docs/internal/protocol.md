# Network Protocol

One TCP connection per PlayerClient, port **48550** by default
(`TcpPlayerServerService.DefaultPort`). The Host is the server; PlayerClient
instances are clients. The protocol is mostly one-way (Host → Client
notifications), with a narrow client → Host exception for video-playback
feedback.

## Protocol versioning

`ProtocolInfo.Version` (`RpgTimeTracker.Shared/Models/Rpc/ProtocolInfo.cs`) is
exchanged in `session.hello`/`session.helloRejected` - a host rejects a
connection from a client with a differing version instead of silently
accepting it, giving a clear error instead of undefined behavior once
multiple client/host builds are in circulation. The
`RPC Protocol Version` CI check (`.github/workflows/protocol-version.yml`)
enforces this on every PR: if `RpcMethods.cs`/`RpcParams.cs` change by more
than whitespace, `ProtocolInfo.Version` must increase, or the check fails.

## Frame format

Every message on the wire — RPC notification or raw media chunk — is wrapped
in the same length-prefixed binary frame
(`RpgTimeTracker.Shared.Network.NetworkFrame`):

```
┌──────────┬───────────────────────┬─────────────┐
│  1 byte  │  4 bytes (big-endian) │   payload    │
│  type    │  payload length       │              │
└──────────┴───────────────────────┴─────────────┘
```

| Type | Value | Payload |
|---|---|---|
| `TypeRpc` | 1 | UTF-8 JSON of a JSON-RPC-shaped notification (see below) |
| `TypeMediaChunk` | 2 | Raw bytes of the file currently being streamed — no header, since only one media transfer is ever in flight per connection at a time |

`NetworkFrame.ReadAsync`/`WriteAsync` are shared by both the server's and the
client's read/write loops. `ReadAsync` takes an optional `onDataReceived`
callback invoked after every partial read (not just on frame completion) —
this is what lets the client's idle-watchdog track "any bytes arrived
recently" instead of "a whole frame arrived recently", so a large, slow-but-
healthy media transfer doesn't get mistaken for a dead connection.

## JSON-RPC envelope

Payloads inside `TypeRpc` frames follow a JSON-RPC 2.0-*shaped* envelope, but
it is **notification-only** — no request IDs, no responses:

```json
{ "jsonrpc": "2.0", "method": "clock.speedChanged", "params": { "speedMultiplier": 4.0 } }
```

- `RpcMessage.Serialize<TParams>(method, params)` — outgoing.
- `RpcMessage.TryParseRaw(bytes)` — incoming, returns `null` (never throws)
  on malformed JSON, since the sender is a live network peer that shouldn't
  be able to crash the receiver.
- `RpcNotificationRaw.GetParams<TParams>()` — deserializes `params` lazily
  once the `method` string has told the receiver which type to expect.

## Methods (Host → Client)

| Method | Params | Fired when |
|---|---|---|
| `session.snapshot` | `SessionSnapshotParams` (full state incl. all timeline items, plus the current fog render style: `fogColorHex`/`fogOpacityPercent`/`fogBlurRadius`, and the auto-zoom-to-active-character preference: `autoZoomEnabled`/`autoZoomLevel`) | Once per new connection; also on demand for a full resync (e.g. after loading a save file) |
| `session.heartbeat` | `ClockHeartbeatParams` (current game time, speed, running) | Every 5s (`TcpPlayerServerService.HeartbeatInterval`), always — doubles as the client's dead-connection signal *and* periodic clock-drift correction |
| `clock.started` / `clock.stopped` | *(none)* | Clock paused/resumed |
| `clock.speedChanged` | `{ speedMultiplier }` | Speed slider changed |
| `clock.timeJumped` | `{ newGameTime }` | An explicit jump (manual set, jump buttons, load) — **not** sent for routine per-tick advancement, see [`design-decisions.md`](design-decisions.md) |
| `header.changed` | `{ title, subtitle }` | Player-window header text edited |
| `theme.changed` | `{ theme }` | Theme switched |
| `timelineItem.upserted` | `TimelineItemSnapshotDto` | A timer/alarm/interval was created or had a discrete state change (started/paused/reset/completed/triggered/edited) — **not** sent every tick |
| `timelineItem.removed` | `{ id }` | Item deleted |
| `display.fullscreen` | `{ fullscreen }` | SL toggled "Player Fullscreen" |
| `media.begin` | `MediaHeaderDto` (id, kind, filename, mime, total length, loop, volume, repeatCount, trimStartMs, trimEndMs, addToGallery) | Announces an incoming image/video/sound, immediately followed by N `TypeMediaChunk` frames. `Volume`/`RepeatCount`/`TrimStartMs`/`TrimEndMs` are only meaningful for `Kind = Audio` — `RepeatCount` only applies when `Loop = false` (1 = play once, no repeat). `AddToGallery` is only meaningful for `Kind = Image`/`Video`: true for ad-hoc/library sends (joins the client's local gallery), false for event-trigger media (one-shot, takes display priority, never joins the gallery) |
| `media.cleared` | *(none)* | SL removed the current image/video, or a non-looping video finished and auto-closed. **Never** affects sounds, which live entirely outside this slot — see [`design-decisions.md`](design-decisions.md) |
| `media.stopSound` | `{ mediaId }` | SL stopped one specific, currently-playing sound (per-item "Stop" or the bulk "Stop All" in the Sounds tab) |
| `media.setVolume` | `{ mediaId, volume }` | SL adjusted a currently-playing sound's volume live (the "currently playing sounds" panel's slider) |
| `media.retract` | `{ mediaId }` | SL removed one image/video from the gallery everywhere - each client drops it from its local list and deletes its cached copy |
| `media.highlight` | `{ mediaId }` | SL (or the auto-resume after an event medium closes) suggests jumping to this gallery item - each client jumps its own local pointer there but can navigate away again immediately afterward |
| `media.slideshowInterval` | `{ seconds }` | SL set/changed the per-image auto-advance duration (0 = manual). Each client runs its own independent timer off this value; videos are never auto-advanced away from |
| `handout.show` | `HandoutShowParams` (`title`, `markdown`) | SL pushes a Markdown-authored text handout, shown in its own overlay window - independent of the image/video "current medium" slot and the map, filtered by the same `ImageEnabled` per-client routing flag a plain image push uses. Cached (`TcpPlayerServerService._lastHandout`) and resent to a newly connecting/reconnecting client the same way the current image/video medium is (`SendCatchUpAsync`), not via `session.snapshot` |
| `handout.hide` | *(none)* | SL closes the currently shown handout for all clients |
| `map.show` | `MapShowParams` (mapId, mapName, per-floor: floorId, floorName, imageMediaId, cellSizePx, gridWidth/height, startingFogBase64, currentFogBase64; tokens: `MapTokenSnapshotDto[]`) | SL opens a map to players (replaces the gallery display). Each floor's image is streamed beforehand via `media.begin`/`TypeMediaChunk` with `Kind = MapFloor` (a distinct Kind from Image/Video/Audio - see `MediaHeaderDto.MediaKindMapFloor` - so it never touches the gallery/current-medium logic), keyed by `ImageMediaId = FloorId`. Sent in full (not incrementally) to any client that connects or reconnects while a map is open, so fog/token state can never end up partially stale - see `TcpPlayerServerService._openMap`/`SendCatchUpAsync` |
| `map.fogUpdate` | `{ floorId, cells: [{ x, y, revealed }] }` | Debounced per-brush-stroke reveal/hide update for one floor of the currently open map |
| `map.fogReset` | `{ floorId }` | SL resets one floor's live fog back to its starting template - the client already has both fog masks from `map.show`, so no fog data is resent |
| `map.hide` | *(none)* | SL closes the map; clients return to the previous gallery display |
| `map.renderStyleChanged` | `{ colorHex, opacityPercent, blurRadius }` | SL changed the fog render style live - a map can override color/opacity/blur/blur-enabled individually (Edit/Prepare window), falling back to the global Settings-tab default for whatever it leaves unset (`MainWindowViewModel.GetEffectiveFogStyle`); this always carries the *effective* (already-resolved) style for whichever map is currently open, never the raw per-map override or raw global value separately. Also sent as part of `session.snapshot` for newly connecting/reconnecting clients |
| `map.autoZoomChanged` | `MapAutoZoomChangedParams` (`enabled`, `zoomLevel`) | SL toggled/reconfigured "auto-zoom to the active-turn character" (Settings tab or the Map/Live window) - a client-side preference, not a server-enforced view: the client stores it on `MapDisplayViewModel`, which then decides on each `map.tokenUpsert` whether a token's `IsCurrentTurn` flipping to `true` should trigger a zoom+pan. Also sent as part of `session.snapshot` for newly connecting clients |
| `map.tokenUpsert` | `MapTokenSnapshotDto` (id, floorId, x, y, name?, detail?, playerInfo?, iconGlyph?, isCurrentTurn, facingDegrees?) | SL adds/updates one map token - the whole resolved snapshot, always, never a partial diff (a token's payload is tiny, unlike fog cells - a drag, a floor reassignment, a relink, or a visibility-toggle change are all just another `map.tokenUpsert`). Name/Detail/PlayerInfo/IconGlyph are resolved server-side from the linked Character+Variant or Point of Interest (or the token's own freeform fields) and are `null` when that field's player-visible toggle is off - the client applies no visibility logic of its own. `PlayerInfo` (Markdown-authored, rendered as plain text in the token tooltip) resolves from `NpcVariantViewModel.PlayerInfo` for a Character-linked token or `PointOfInterestLibraryItemViewModel.PlayerInfo` for a Point of Interest-linked token; freeform tokens have no such field and always send `null`. Also covers a token becoming newly visible (e.g. `HiddenUntilRevealed` and its cell was just painted revealed). `isCurrentTurn` is set by the Initiative tracker (#70) - true for the one token whose turn it currently is; `facingDegrees` (0 = north, clockwise) is only sent for a Character-linked token (`null` for freeform/Point of Interest, which have no facing) so the client's arrow overlay gates on that instead of needing the token's link kind over the wire |
| `map.tokenRemove` | `{ tokenId }` | SL removed a token, or it's no longer visible to players (`GmOnly`, or `HiddenUntilRevealed` and its cell became hidden again) - the client doesn't distinguish the two cases |
| `map.ping` | `MapPingParams` (`floorId`, `x`, `y`) | SL pinged the map, either by double-clicking their own canvas with the Ping tool selected, or forwarding a player's `map.pingFromPlayer` (see below). Broadcast to every `MapEnabled` client. Ephemeral - not cached, not resent as part of `map.show`'s catch-up for a newly connecting client (a ping that already faded before they joined has no meaning to replay) |
| `audio.routingChanged` | `DataRoutingChangedParams` (`musicEnabled`, `soundEnabled`, `imageEnabled`, `videoEnabled`, `mapEnabled`) | SL toggled one client's per-kind routing (Connected Clients panel) - kept its old name despite now covering Image/Video/Map too, to avoid wire-protocol churn (see `design-decisions.md`). Sent to exactly one client (not broadcast) right after handshake and again on every live toggle, purely so that client can show a "muted by GM" indicator - the actual enforcement is server-side: `PublishMediaAsync`/`PublishMapShowAsync`/etc. simply skip a client whose corresponding flag is off, and `SendCatchUpAsync` withholds cached image/video/map catch-up the same way |

## Methods (Client → Host)

| Method | Params | Fired when |
|---|---|---|
| `media.playbackStarted` | `{ mediaId, durationMs }` | The client's LibVLC player actually began playing a video, reporting the *real* duration it measured (not whatever the Host guessed) |
| `media.playbackEnded` | `{ mediaId }` | A **non-looping** video reached its end on the client |
| `map.pingFromPlayer` | `MapPingParams` (`floorId`, `x`, `y`) | A player double-clicked their own map view - visible only to the GM (rendered locally, never re-broadcast to other players), not a persisted or resyncable event |

These two exist because the Host cannot otherwise know what actually happens
on a remote screen — see [`design-decisions.md`](design-decisions.md) for why
this was added and how it's used (loop-vs-close, clock pause-for-video).

The server side of this required a real change: `ClientConnection` used to
just drain and discard anything a client sent (`DrainAndCloseOnInputAsync`),
since the protocol was designed one-way. It now runs a small `ReadLoopAsync`
that parses inbound `TypeRpc` frames (capped at 4 KB —
`MaxInboundRpcPayloadBytes` — since clients are only ever expected to send
a handful of tiny messages) and dispatches known methods; anything else is
logged and ignored rather than disconnecting the client.

## Media streaming

1. Host reads the whole file into memory (`byte[]`), capped at
   `RpgTimeTracker.Shared.Network.MediaLimits.MaxMediaBytes` (1900 MiB — kept
   just under the ~2 GiB ceiling a single .NET array can ever reach, since an
   exact 2 GiB constant would make the size check permanently unreachable —
   see [`design-decisions.md`](design-decisions.md)).
2. Host sends one `media.begin` RPC frame, then the file in 64 KB
   `TypeMediaChunk` frames (`MediaChunkSize`) — small enough that a
   `skipIfBusy` state broadcast in between doesn't have to wait long for the
   per-connection write lock.
3. Client creates a temp file on `media.begin` and fires `MediaBeginReceived`
   **immediately** (before any bytes have arrived) — for video, this lets
   playback start progressively into the still-growing file (LibVLC can
   follow a file that's being appended to); for images, the caller waits for
   `MediaCompleted` since a partial image can't be decoded.
4. Client writes each chunk and flushes; once the byte count reaches the
   announced `TotalLength`, it fires `MediaCompleted`.
5. The most recently sent media is cached server-side (`_lastMedia`) and
   replayed to any client that connects after it was sent, alongside the
   `session.snapshot` catch-up.

`Loop` (carried in `MediaHeaderDto`) determines client behavior on end:
- **Loop = true**: the client restarts the video locally (`Stop()`/`Play()`
  on the same `MediaPlayer`) and never reports `media.playbackEnded` — the
  Host has nothing to track for a looping video.
- **Loop = false**: the client reports `media.playbackEnded`, and the Host's
  `TcpPlayerServerService.ClientReportedPlaybackEnded` handler resolves
  whatever it was tracking for that `mediaId` (see next section).

## Video-end tracking (Host side)

Applies to **every** video sent, regardless of path (ad-hoc send, library,
or an event trigger) — `MainWindowViewModel.BeginVideoTracking`:

- Skipped entirely if the video loops (nothing to wait for).
- Otherwise the Host remembers the `mediaId` and (if requested via
  `TriggerMediaConfig.PauseClockDuringVideo`) pauses the game clock.
- A **fallback timer** runs in parallel: it asks LibVLC to locally parse the
  video's duration and waits that long plus a 15s buffer. This exists purely
  as a safety net for when no client ever reports back (e.g. an older
  client, or zero clients connected) — the real client report is expected to
  win the race in the normal case, since it also accounts for network/buffer
  time the local duration estimate can't see.
- Whichever resolves first — a real `media.playbackEnded` report or the
  fallback timeout — calls `ResolvePendingVideo`, which resumes the clock if
  it was paused for this video and always clears/closes the media
  (`ClearMediaCommand`).
- Clearing media does **not** touch remote/local fullscreen state. That used
  to auto-revert fullscreen on every clear, which turned out to also kill a
  deliberately, manually-enabled "keep the player fullscreen" preference —
  see [`design-decisions.md`](design-decisions.md).

## Sound lifecycle (Host side)

Sounds (`Kind = Audio`) are transferred exactly like images/video (`media.begin`
+ chunks), but everything downstream is deliberately separate from the
image/video pipeline above — see [`design-decisions.md`](design-decisions.md)
for the bug this decoupling fixes. Per sound sent:

- `MainWindowViewModel.SendSoundAsync` sends it, plays it locally if eligible
  (`PlayLocalSoundIfNeeded` — same "no client connected" condition as
  `ShouldShowMediaLocally`, just without any UI window), and adds an entry to
  `ActivePlayingSounds` (the "currently playing sounds" panel).
- The client (or the Host's own local preview) reports
  `media.playbackStarted`/`media.playbackEnded` exactly like video, but the
  Host no longer *acts* on these to auto-close anything — a sound only ever
  leaves `ActivePlayingSounds` via its own natural end, an explicit
  `media.stopSound`, or the last client disconnecting (see below).
- `media.stopSound`/`media.setVolume` are the only Host→Client(s) commands
  that target one already-in-flight sound by `mediaId`, rather than
  announcing a new one.
- If the last connected client disconnects (or the server stops),
  `ClearRemoteOnlyActiveSounds` drops every panel entry that isn't also
  playing locally — the client independently stops all of its own sounds on
  disconnect (`ClientMainWindowViewModel.StopAllSounds`), so this just keeps
  the Host's panel truthful rather than sending anything further.

## Clock synchronization design

The client never receives a network message per tick. Instead:

1. On `session.snapshot` or `session.heartbeat`, the client sets its local
   `GameClockService`'s time, speed, and running state directly.
2. Between those messages, the client's own `GameClockService` instance
   ticks forward locally using the same `DispatcherTimer`-based
   advancement logic as the Host's (both use the identical
   `RpgTimeTracker.Shared.Services.GameClockService`).
3. `clock.timeJumped` is fired only from `GameClockService.Jump()` — an
   explicit jump — never from the internal per-tick advancement
   (`GameClockService.Jumped` is a separate event from `GameClockService.Tick`
   specifically so the Host can tell them apart).
4. The 5-second heartbeat additionally carries the current clock state as a
   drift-correction/self-healing mechanism: because state RPCs are sent with
   `skipIfBusy: true` (skipped rather than queued if a media transfer is
   mid-flight on that connection, see below), a `clock.started`/`stopped`
   event could in principle be dropped for a busy client. The heartbeat
   guarantees eventual consistency regardless.

## Write-lock semantics (server → client)

Each `ClientConnection` serializes its own writes behind a `SemaphoreSlim`.
Two different acquisition modes are used depending on the payload:

- **State RPCs** (`BroadcastRpcAsync`, i.e. everything except media and the
  initial per-connection catch-up snapshot): `skipIfBusy: true` — a
  non-blocking `WaitAsync(0)`. If the lock is currently held (a media
  transfer is in progress on that connection), the state broadcast is
  silently skipped for that client rather than blocking or disconnecting it.
  This is safe specifically because of the heartbeat's self-healing
  property above.
- **Media chunks and the catch-up snapshot**: must-deliver, with a 15s
  timeout (`MustDeliverWriteTimeout`) before the connection is considered
  dead and dropped.

This two-tier approach exists because a single shared timeout used to cause
false disconnects: a slow media send would hold the lock long enough that a
concurrent state publish would time out and the client would get dropped —
even though the connection itself was perfectly healthy, just busy.
