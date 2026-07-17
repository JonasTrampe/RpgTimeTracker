namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>
///     Names of all JSON-RPC notifications (no request/response, no IDs). The vast majority
///     run server-to-client (the GM host publishes state changes); the two MediaPlayback*
///     methods are the only exception and run client-to-server, so the host knows when a
///     video has actually ended for the player (see MediaPlaybackEnded).
/// </summary>
public static class RpcMethods
{
    /// <summary>
    ///     Client-to-server: first signal right after the TCP connection is established -
    ///     protocol version and optional PIN, before the host replies with the full
    ///     session.snapshot (see TcpPlayerServerService.PerformHandshakeAsync). Without this
    ///     signal (timeout) or with a wrong PIN/incompatible version, the host instead responds
    ///     with session.helloRejected and disconnects.
    /// </summary>
    public const string SessionHello = "session.hello";

    /// <summary>
    ///     Server-to-client: rejects the connection (wrong PIN, incompatible protocol version,
    ///     or no session.hello within the timeout). The client should NOT automatically retry
    ///     this (see PlayerTcpClientService._userDisconnectRequested).
    /// </summary>
    public const string SessionHelloRejected = "session.helloRejected";

    /// <summary>Full state for newly connecting clients (once, on connect).</summary>
    public const string SessionSnapshot = "session.snapshot";

    /// <summary>Pure heartbeat with no payload, so the client detects a dead connection.</summary>
    public const string SessionHeartbeat = "session.heartbeat";

    public const string ClockStarted = "clock.started";
    public const string ClockStopped = "clock.stopped";
    public const string ClockSpeedChanged = "clock.speedChanged";

    /// <summary>Absolute resync point: manual time set, time jump, or loading a save.</summary>
    public const string ClockTimeJumped = "clock.timeJumped";

    public const string HeaderChanged = "header.changed";
    public const string ThemeChanged = "theme.changed";

    /// <summary>
    ///     A timer/alarm/interval was created or changed structurally
    ///     (start/pause/reset/completed/triggered/edited) - NOT on pure time progress.
    /// </summary>
    public const string TimelineItemUpserted = "timelineItem.upserted";

    public const string TimelineItemRemoved = "timelineItem.removed";

    /// <summary>GM requests the client to (un)maximize its display window (list).</summary>
    public const string DisplayFullscreen = "display.fullscreen";

    /// <summary>Announces a medium; the raw data follows as binary chunks (see NetworkFrame.TypeMediaChunk).</summary>
    public const string MediaBegin = "media.begin";

    public const string MediaCleared = "media.cleared";

    /// <summary>
    ///     Client-to-server: playback of a video has actually started, including the duration
    ///     determined by the client itself (LibVLC).
    /// </summary>
    public const string MediaPlaybackStarted = "media.playbackStarted";

    /// <summary>
    ///     Client-to-server: a non-looping video has ended - the host uses this to resume a
    ///     game time paused for it and/or close the medium on all clients.
    /// </summary>
    public const string MediaPlaybackEnded = "media.playbackEnded";

    /// <summary>
    ///     GM stops a single, currently running sound specifically (by MediaId) - independent
    ///     of the image/video "current medium" slot, see MediaHeaderDto.MediaKindAudio.
    /// </summary>
    public const string MediaStopSound = "media.stopSound";

    /// <summary>GM adjusts the volume of a currently running sound live (by MediaId).</summary>
    public const string MediaSetVolume = "media.setVolume";

    /// <summary>
    ///     Removes an image/video specifically from the gallery (by MediaId) - independent of
    ///     the currently displayed item, see MediaHeaderDto.AddToGallery.
    /// </summary>
    public const string MediaRetract = "media.retract";

    /// <summary>
    ///     GM "highlights": all clients jump locally to this gallery item (by MediaId), but can
    ///     then continue to move away from it independently afterward.
    /// </summary>
    public const string MediaHighlight = "media.highlight";

    /// <summary>
    ///     Sets the automatic advance time per image (seconds, 0 = manual). Client-side, applies
    ///     only to images, never to videos (see design-decisions.md).
    /// </summary>
    public const string MediaSlideshowInterval = "media.slideshowInterval";

    /// <summary>
    ///     GM opens a map to players (replaces the gallery display). Floor images are announced/
    ///     streamed beforehand via the existing media.begin + TypeMediaChunk pipeline (one per
    ///     floor, MediaHeaderDto.Kind = MediaKindMapFloor), then this metadata message follows
    ///     once all floor images have started transferring. Sent in full (not incrementally) to
    ///     any client that connects or reconnects while a map is open - see
    ///     TcpPlayerServerService.SendCatchUpAsync.
    /// </summary>
    public const string MapShow = "map.show";

    /// <summary>
    ///     Debounced per-brush-stroke reveal/hide update for one floor of the currently open map.
    /// </summary>
    public const string MapFogUpdate = "map.fogUpdate";

    /// <summary>
    ///     GM resets one floor's live fog back to its starting template - the client already has
    ///     both (from map.show), so this just tells it to copy locally, no fog data resent.
    /// </summary>
    public const string MapFogReset = "map.fogReset";

    /// <summary>GM closes the map; clients return to the previous gallery display.</summary>
    public const string MapHide = "map.hide";

    /// <summary>
    ///     GM changed the player-side fog render style (color/opacity/blur) live - one global
    ///     preference, not per-map (see issue #22). The current style is also sent as part of
    ///     session.snapshot for newly connecting clients, mirroring how ThemeChangedParams/
    ///     SessionSnapshotParams.Theme both carry the theme.
    /// </summary>
    public const string MapRenderStyleChanged = "map.renderStyleChanged";

    /// <summary>
    ///     GM toggled/reconfigured "auto-zoom to the active-turn character" (a player-side
    ///     preference: when a turn starts, every connected view - including the Host's own local
    ///     preview - snaps to the configured zoom level centered on the current-turn token, then
    ///     the player is free to zoom/pan away again on their own until the next turn change).
    ///     Same pattern as MapRenderStyleChanged - also sent as part of session.snapshot for
    ///     newly connecting clients.
    /// </summary>
    public const string MapAutoZoomChanged = "map.autoZoomChanged";

    /// <summary>
    ///     Adds or fully updates one map token (#31/#33) - always the whole resolved snapshot
    ///     (Name/Detail/IconGlyph already filtered by that token's player-visible toggles, position,
    ///     and current floor), never a partial diff: unlike fog cells, a token's payload is tiny, so
    ///     there's no incremental "move-only" variant - a drag, a floor reassignment, a relink, and
    ///     a visibility-toggle change are all just another map.tokenUpsert. Also carries a token that
    ///     just became visible (e.g. HiddenUntilRevealed, its cell was just revealed) for the first
    ///     time - the client has no prior state to diff against either way.
    /// </summary>
    public const string MapTokenUpsert = "map.tokenUpsert";

    /// <summary>
    ///     Removes one map token, or - equally - tells the client a token it knew about is no
    ///     longer visible to players (GmOnly, or HiddenUntilRevealed and its cell became hidden
    ///     again) - the client doesn't distinguish the two cases, both just mean "stop showing this
    ///     token".
    /// </summary>
    public const string MapTokenRemove = "map.tokenRemove";

    /// <summary>
    ///     Client-to-server: a player double-clicked the map, pointing at something for the GM -
    ///     visible only to the GM (the Host's own preview(s)), never rebroadcast to other players.
    ///     Ephemeral - not persisted, not part of map.show's resync for newly-connecting clients.
    /// </summary>
    public const string MapPingFromPlayer = "map.pingFromPlayer";

    /// <summary>
    ///     GM double-clicked their own map - broadcast to every connected player (and shown on
    ///     the Host's own preview locally, no network round-trip needed for that). Same ephemeral
    ///     nature as MapPingFromPlayer, just the other direction.
    /// </summary>
    public const string MapPing = "map.ping";

    /// <summary>
    ///     Client-to-server: a player Shift+left-drew a freehand stroke on their own map view,
    ///     marking something for the GM - same one-way, GM-only visibility as MapPingFromPlayer
    ///     (never rebroadcast to other players, no server-to-client counterpart). Ephemeral - not
    ///     persisted, not part of map.show's resync.
    /// </summary>
    public const string MapAnnotationFromPlayer = "map.annotationFromPlayer";

    /// <summary>
    ///     GM stops the currently playing music track/playlist - independent of the image/video
    ///     "current medium" slot and of sound effects, see MediaHeaderDto.MediaKindMusic.
    /// </summary>
    public const string MusicStop = "music.stop";

    /// <summary>GM adjusts the volume of the currently playing music track live.</summary>
    public const string MusicSetVolume = "music.setVolume";

    /// <summary>
    ///     Client-to-server: the currently playing music track has actually ended - the Host's
    ///     playlist sequencer uses this (alongside its own local-preview playback and a duration-
    ///     estimate fallback timeout, mirroring MediaPlaybackEnded's video-tracking pattern) to
    ///     decide when to advance to the next track.
    /// </summary>
    public const string MusicTrackEnded = "music.trackEnded";

    /// <summary>
    ///     Server-to-client: tells this specific client window whether the GM currently has
    ///     Music/Sound routing enabled for it (see TcpPlayerServerService.SetClientMusicEnabled/
    ///     SetClientSoundEnabled) - sent right after a successful handshake (reflecting any
    ///     restored per-ClientId preference) and again whenever the GM changes it live, so the
    ///     client can show a "muted by GM" indicator instead of silently receiving nothing.
    /// </summary>
    public const string AudioRoutingChanged = "audio.routingChanged";
}