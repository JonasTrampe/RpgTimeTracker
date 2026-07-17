using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>Client-to-server: first signal after the connection is established (see RpcMethods.SessionHello).</summary>
public sealed class SessionHelloParams
{
    public int ProtocolVersion { get; set; }
    public string Pin { get; set; } = string.Empty;

    /// <summary>
    ///     Stable per-installation identifier (see ClientSettingsService.GetOrCreateClientId),
    ///     generated once and persisted locally - lets the Host remember this window's Music/Sound
    ///     routing preference across reconnects instead of resetting it to enabled every time.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>Server-to-client: connection rejected (see RpcMethods.SessionHelloRejected).</summary>
public sealed class SessionHelloRejectedParams
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class SessionSnapshotParams
{
    /// <summary>
    ///     Elapsed seconds of the calendar-agnostic GameInstant (see GameClockService) -
    ///     not a wall-clock DateTime, since game time isn't locked to the Gregorian calendar.
    /// </summary>
    public long CurrentGameTimeSeconds { get; set; }

    public double SpeedMultiplier { get; set; } = 1.0;
    public bool IsClockRunning { get; set; }
    public string PlayerHeaderTitle { get; set; } = "Spieleranzeige";
    public string PlayerHeaderSubtitle { get; set; } = string.Empty;
    public string Theme { get; set; } = "Shadowrun";
    public List<TimelineItemSnapshotDto> Items { get; set; } = new();
    public List<CalendarEntryDefinition> CalendarEntries { get; set; } = new();

    /// <summary>
    ///     The campaign's active calendar, sent once at connect (not re-sent per tick) so
    ///     PlayerClient can format dates without duplicating calendar-selection logic - see
    ///     CalendarService.Active.
    /// </summary>
    public CalendarDefinition ActiveCalendar { get; set; } = CalendarDefinition.CreateGregorian();

    /// <summary>Player-side fog render style, current at connect time - see MapRenderStyleChangedParams.</summary>
    public string FogColorHex { get; set; } = "#0C0C0C";

    public int FogOpacityPercent { get; set; } = 100;
    public double FogBlurRadius { get; set; }
    public bool FogBlurEnabled { get; set; } = true;

    /// <summary>Player-side auto-zoom-to-active-character preference, current at connect time - see MapAutoZoomChangedParams.</summary>
    public bool AutoZoomEnabled { get; set; }

    public double AutoZoomLevel { get; set; } = 2.0;
}

public sealed class ClockSpeedChangedParams
{
    public double SpeedMultiplier { get; set; } = 1.0;
}

/// <summary>
///     Accompanies the periodic heartbeat with the current clock state so the locally
///     derived client clock is regularly corrected against drift - and so the client
///     can also recover from a delta event that was discarded because of skipIfBusy (media
///     transfer was in progress), without having to wait for the next real jump.
/// </summary>
public sealed class ClockHeartbeatParams
{
    public long CurrentGameTimeSeconds { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public bool IsClockRunning { get; set; }
}

/// <summary>Absolute resync point (manual setting, time jump, loading a save).</summary>
public sealed class ClockTimeJumpedParams
{
    public long NewGameTimeSeconds { get; set; }
}

public sealed class HeaderChangedParams
{
    public string Title { get; set; } = "Spieleranzeige";
    public string Subtitle { get; set; } = string.Empty;
}

public sealed class ThemeChangedParams
{
    public string Theme { get; set; } = "Shadowrun";
}

public sealed class TimelineItemRemovedParams
{
    public string Id { get; set; } = string.Empty;
}

public sealed class DisplayFullscreenParams
{
    public bool Fullscreen { get; set; }
}

/// <summary>Client-to-server: video playback has started, including the duration determined by the client itself.</summary>
public sealed class MediaPlaybackStartedParams
{
    public string MediaId { get; set; } = string.Empty;
    public long DurationMs { get; set; }
}

/// <summary>Client-to-server: a non-looping video has ended on the client.</summary>
public sealed class MediaPlaybackEndedParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-to-client: stops a single, currently playing sound (by MediaId).</summary>
public sealed class MediaStopSoundParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-to-client: adjusts the volume of a currently playing sound live (0-100).</summary>
public sealed class MediaSetVolumeParams
{
    public string MediaId { get; set; } = string.Empty;
    public int Volume { get; set; } = 100;
}

/// <summary>Server-to-client: removes an image/video from the gallery (by MediaId).</summary>
public sealed class MediaRetractParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-to-client: jumps (locally, non-blocking) to this gallery item.</summary>
public sealed class MediaHighlightParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-to-client: automatic advance time per image (seconds, 0 = manual).</summary>
public sealed class MediaSlideshowIntervalParams
{
    public double Seconds { get; set; }
}

/// <summary>Server-to-client: opens a map (see RpcMethods.MapShow).</summary>
public sealed class MapShowParams
{
    public Guid MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public List<MapFloorShowDto> Floors { get; set; } = [];

    /// <summary>
    ///     Every token currently visible to players, across all floors (each carries its own
    ///     FloorId - see MapTokenSnapshotDto) - full resync, same "always full state on connect/
    ///     reconnect" rule as fog.
    /// </summary>
    public List<MapTokenSnapshotDto> Tokens { get; set; } = [];
}

/// <summary>
///     One floor's metadata for map.show - the image itself is transferred separately via the
///     existing media.begin + TypeMediaChunk pipeline, keyed by ImageMediaId (= FloorId).
/// </summary>
public sealed class MapFloorShowDto
{
    public Guid FloorId { get; set; }
    public string FloorName { get; set; } = string.Empty;
    public string ImageMediaId { get; set; } = string.Empty;
    public int CellSizePx { get; set; }
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>
    ///     Binary FogMask format (see FogMaskSerializer), base64-encoded - already a
    ///     "whole grid" transfer conceptually identical to the at-rest format.
    /// </summary>
    public string StartingFogBase64 { get; set; } = string.Empty;

    public string CurrentFogBase64 { get; set; } = string.Empty;
}

/// <summary>Server-to-client: debounced reveal/hide update for one floor (see RpcMethods.MapFogUpdate).</summary>
public sealed class MapFogUpdateParams
{
    public Guid FloorId { get; set; }
    public List<FogCellDto> Cells { get; set; } = [];
}

public sealed class FogCellDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public bool Revealed { get; set; }
}

/// <summary>Server-to-client: reset one floor's live fog back to its starting template (see RpcMethods.MapFogReset).</summary>
public sealed class MapFogResetParams
{
    public Guid FloorId { get; set; }
}

/// <summary>Server-to-client: live change of the player-side fog render style (see RpcMethods.MapRenderStyleChanged).</summary>
public sealed class MapRenderStyleChangedParams
{
    public string ColorHex { get; set; } = "#0C0C0C";
    public int OpacityPercent { get; set; } = 100;
    public double BlurRadius { get; set; }
    public bool BlurEnabled { get; set; } = true;
}

/// <summary>Server-to-client: live change of the auto-zoom-to-active-character preference (see RpcMethods.MapAutoZoomChanged).</summary>
public sealed class MapAutoZoomChangedParams
{
    public bool Enabled { get; set; }
    public double ZoomLevel { get; set; } = 2.0;
}

/// <summary>
///     A map token, already fully resolved and filtered by the Host before it's ever put on the
///     wire (see RpcMethods.MapTokenUpsert) - Name/Detail/IconGlyph are null when that token's
///     corresponding PlayerVisibleName/Detail/Portrait toggle is off, so the client never even
///     receives a field it isn't allowed to show; it has no extra visibility logic of its own to
///     apply. Portrait image transfer (the actual picture, not just a fallback icon glyph) is
///     deferred to a follow-up - this first pass only sends IconGlyph.
/// </summary>
public sealed class MapTokenSnapshotDto
{
    public Guid Id { get; set; }
    public Guid FloorId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string? Name { get; set; }
    public string? Detail { get; set; }
    public string? IconGlyph { get; set; }

    /// <summary>
    ///     The Character's player-facing Markdown bio/description (NpcVariantViewModel.PlayerInfo),
    ///     only populated when the token's PlayerVisiblePlayerInfo toggle is on - null for
    ///     freeform/Point of Interest tokens, which have no such field.
    /// </summary>
    public string? PlayerInfo { get; set; }

    /// <summary>Set by the Initiative tracker (#70) - true for the one token whose turn it currently is.</summary>
    public bool IsCurrentTurn { get; set; }

    /// <summary>
    ///     Facing direction in degrees, 0 = up/north, clockwise (#70) - only set for a
    ///     Character-linked token (null for freeform/Point of Interest tokens, which have no
    ///     facing), so the client's arrow overlay can gate on this being non-null instead of
    ///     needing the token's LinkKind sent over the wire.
    /// </summary>
    public double? FacingDegrees { get; set; }
}

/// <summary>Server-to-client: a token is no longer visible to players (see RpcMethods.MapTokenRemove).</summary>
public sealed class MapTokenRemoveParams
{
    public Guid TokenId { get; set; }
}

/// <summary>
///     Shared shape for both map.pingFromPlayer (client-to-server) and map.ping (server-to-
///     client) - image-space coordinates on one floor, meaningful only relative to whichever
///     floor the receiving side currently has displayed (see RpcMethods.MapPing's doc comment).
/// </summary>
public sealed class MapPingParams
{
    public Guid FloorId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>One point of a freehand annotation stroke, in image-space coordinates - see MapAnnotationParams.</summary>
public sealed class AnnotationPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
///     Shape for map.annotationFromPlayer (client-to-server only - see RpcMethods.
///     MapAnnotationFromPlayer's doc comment for why there's no server-to-client counterpart).
///     Points are in image-space coordinates on the given floor, sent as one message per
///     completed stroke rather than per pointer-move.
/// </summary>
public sealed class MapAnnotationParams
{
    public Guid FloorId { get; set; }
    public List<AnnotationPoint> Points { get; set; } = [];
}

/// <summary>Server-to-client: adjusts the volume of the currently playing music track live (0-100).</summary>
public sealed class MusicSetVolumeParams
{
    public int Volume { get; set; } = 100;
}

/// <summary>
///     Client-to-server: the currently playing music track (identified by MediaId, matching
///     the MediaHeaderDto.MediaId it was sent with) has actually ended.
/// </summary>
public sealed class MusicTrackEndedParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>
///     Server-to-client: this window's current Music/Sound/Image/Video/Map routing state (see
///     RpcMethods.AudioRoutingChanged - kept its name despite now carrying more flags, to avoid
///     unnecessary wire-protocol churn).
/// </summary>
public sealed class DataRoutingChangedParams
{
    public bool MusicEnabled { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public bool ImageEnabled { get; set; } = true;
    public bool VideoEnabled { get; set; } = true;
    public bool MapEnabled { get; set; } = true;
}

/// <summary>
///     Full state of a timer/alarm/interval for "upsert" (created or structurally
///     changed - start/pause/reset/completed/triggered/edited). NOT sent for pure
///     time progression: the client derives remaining time/progress locally from these fields
///     + its own clock, synchronized via clock.* events.
/// </summary>
public sealed class TimelineItemSnapshotDto
{
    public const string KindTimer = "Timer";
    public const string KindAlarm = "Alarm";
    public const string KindInterval = "Interval";

    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = KindTimer;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Sound { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; } = true;

    // Timer
    public long DurationTicks { get; set; }
    public long ElapsedTicks { get; set; }
    public bool IsRunning { get; set; }
    public bool IsCompleted { get; set; }

    // Alarm
    public long TriggerAtSeconds { get; set; }
    public long? RepeatIntervalTicks { get; set; }
    public bool IsTriggered { get; set; }

    // Interval
    public long IntervalTicks { get; set; }
    public long ActiveDurationTicks { get; set; }
    public int? MaxRepeats { get; set; }
}