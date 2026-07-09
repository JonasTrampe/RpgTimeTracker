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
}