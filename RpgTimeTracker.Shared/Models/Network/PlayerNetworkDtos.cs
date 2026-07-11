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
    ///     A music track (see RpcMethods.MusicStop/MusicSetVolume/MusicTrackEnded) - deliberately
    ///     a distinct Kind from MediaKindAudio: music plays on its own independent channel/
    ///     transport (a Host-driven playlist sequencer), routed via its own client-side event
    ///     (MusicTrackReceived, not MediaBeginReceived) so it never touches the sound-effect
    ///     ActiveSoundViewModel tracking or the image/video "current medium" gallery slot.
    /// </summary>
    public const string MediaKindMusic = "Music";
    public string MediaId { get; set; } = string.Empty;
    public string Kind { get; set; } = MediaKindImage;
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
}