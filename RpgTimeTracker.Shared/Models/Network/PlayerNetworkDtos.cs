namespace RpgTimeTracker.Shared.Models.Network;

/// <summary>Announces a medium (RpcMethods.MediaBegin), followed by N raw NetworkFrame.TypeMediaChunk frames.</summary>
public sealed class MediaHeaderDto
{
    public const string MediaKindImage = "Image";
    public const string MediaKindVideo = "Video";
    public const string MediaKindAudio = "Audio";

    /// <summary>
    ///     A map floor image (see RpcMethods.MapShow) - deliberately a distinct Kind rather than
    ///     reusing MediaKindImage + AddToGallery=false: floor images are cached long-term (kept
    ///     alive for the whole time the map is open, not deleted after a single display like
    ///     event-trigger media) and never touch the single-slot "current medium"/gallery logic.
    /// </summary>
    public const string MediaKindMapFloor = "MapFloor";

    /// <summary>
    ///     A playing music track (see RpcMethods.MusicStop/MusicSetVolume/MusicTrackEnded) - see
    ///     Layer. Kind stays MediaKindAudio for music too (both flow through the same media.begin
    ///     + chunk transfer/routing); Layer is what the client-side transport (PlayerTcpClientService)
    ///     and the Host's routing filter (TcpPlayerServerService.PublishMediaAsync) key off to
    ///     decide Music-only-one-at-a-time-buffered-fully-first vs Sound-many-concurrent-streamed-
    ///     while-downloading-may-loop behavior, instead of a second, largely-duplicate Kind value.
    /// </summary>
    public const string LayerMusic = "Music";

    /// <summary>
    ///     A sound effect - see Layer. Also the default (empty Layer is treated the same as
    ///     LayerSound) for back-compat with any header that predates the Layer field.
    /// </summary>
    public const string LayerSound = "Sound";

    public string MediaId { get; set; } = string.Empty;
    public string Kind { get; set; } = MediaKindImage;

    /// <summary>Only meaningful when Kind == MediaKindAudio - see LayerMusic/LayerSound.</summary>
    public string Layer { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Total size in bytes; the actual data follows as NetworkFrame.TypeMediaChunk frames.</summary>
    public long TotalLength { get; set; }

    /// <summary>
    ///     Whether the video should automatically restart from the beginning after ending
    ///     (client-side via LibVLC), instead of reporting media.playbackEnded and being closed
    ///     by the host.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    ///     Only relevant for sounds (0-100): starting volume, as stored in the sound library
    ///     entry. Live changes during playback run separately via media.setVolume.
    /// </summary>
    public int Volume { get; set; } = 100;

    /// <summary>
    ///     Only relevant for sounds and only effective when Loop=false: total number of
    ///     playbacks (1 = once, no repeat). Irrelevant when Loop=true (endless).
    /// </summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>
    ///     Only relevant for sounds: trimming via LibVLC start-time/end-time options.
    ///     0 = no trim at this point (start at 0 or up to the end of the file, respectively).
    /// </summary>
    public long TrimStartMs { get; set; }

    public long TrimEndMs { get; set; }

    /// <summary>
    ///     Only relevant for images/video: whether this medium stays in the session's own
    ///     gallery (ad-hoc/library, navigable/browsable) instead of just being shown once
    ///     (event-trigger media - take precedence, interrupt the gallery display, but are not
    ///     themselves part of it). When false, the client deletes the file after display as before.
    /// </summary>
    public bool AddToGallery { get; set; }

    /// <summary>
    ///     An estimate, in milliseconds, of how far into playback a client should seek right after
    ///     starting this medium, instead of starting from 0 - used for music catch-up (a client
    ///     connecting/reconnecting/re-enabling Music routing mid-track, see
    ///     TcpPlayerServerService._currentMusicTrack) and for sound resend (re-enabling Sound
    ///     routing for a client while a long-running sound/ambience is already active, see
    ///     MainWindowViewModel.ResendActiveSoundsToClient - only applied above a configurable
    ///     duration threshold, since seeking a short one-off effect isn't meaningful). Based on
    ///     wall-clock elapsed time since playback started, not a frame-accurate sync. 0 = no seek.
    /// </summary>
    public long SeekToMs { get; set; }

    /// <summary>
    ///     Shallow copy with a different SeekToMs - used when resending an in-progress
    ///     track/sound to one specific client (music catch-up/re-enable, sound re-enable) without
    ///     mutating the shared cached original, so each resend gets its own accurate estimate.
    /// </summary>
    public MediaHeaderDto CloneWithSeek(long seekToMs)
    {
        return new MediaHeaderDto
        {
            MediaId = MediaId,
            Kind = Kind,
            Layer = Layer,
            FileName = FileName,
            MimeType = MimeType,
            TotalLength = TotalLength,
            Loop = Loop,
            Volume = Volume,
            RepeatCount = RepeatCount,
            TrimStartMs = TrimStartMs,
            TrimEndMs = TrimEndMs,
            AddToGallery = AddToGallery,
            SeekToMs = seekToMs
        };
    }
}