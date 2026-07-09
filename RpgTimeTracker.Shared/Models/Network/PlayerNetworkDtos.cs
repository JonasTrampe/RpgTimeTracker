namespace RpgTimeTracker.Shared.Models.Network;

/// <summary>Kündigt ein Medium an (RpcMethods.MediaBegin), gefolgt von N rohen NetworkFrame.TypeMediaChunk-Frames.</summary>
public sealed class MediaHeaderDto
{
    public const string MediaKindImage = "Image";
    public const string MediaKindVideo = "Video";
    public const string MediaKindAudio = "Audio";
    public string MediaId { get; set; } = string.Empty;
    public string Kind { get; set; } = MediaKindImage;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Gesamtgröße in Bytes; die eigentlichen Daten folgen als NetworkFrame.TypeMediaChunk-Frames.</summary>
    public long TotalLength { get; set; }

    /// <summary>
    ///     Ob das Video nach dem Ende automatisch von vorn beginnen soll (Client-seitig via LibVLC),
    ///     statt media.playbackEnded zu melden und vom Host geschlossen zu werden.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    ///     Nur für Sounds relevant (0-100): Start-Lautstärke, wie im Sound-Bibliothekseintrag
    ///     hinterlegt. Live-Änderungen während der Wiedergabe laufen separat über media.setVolume.
    /// </summary>
    public int Volume { get; set; } = 100;

    /// <summary>
    ///     Nur für Sounds relevant und nur wirksam, wenn Loop=false: Gesamtanzahl der
    ///     Wiedergaben (1 = einmal, kein Wiederholen). Bei Loop=true irrelevant (endlos).
    /// </summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>
    ///     Nur für Sounds relevant: Zurechtschneiden über LibVLC-Startzeit/-Endzeit-Optionen.
    ///     0 = kein Trim an dieser Stelle (Start bei 0 bzw. bis zum Dateiende).
    /// </summary>
    public long TrimStartMs { get; set; }

    public long TrimEndMs { get; set; }

    /// <summary>
    ///     Nur für Bild/Video relevant: ob dieses Medium in der session-eigenen Galerie
    ///     bleibt (Ad-hoc/Bibliothek, navigierbar/rückblätterbar) statt nur einmalig gezeigt zu werden
    ///     (Event-Trigger-Medien - haben Vorrang, unterbrechen die Galerie-Anzeige, sind aber selbst
    ///     nicht Teil davon). Bei false löscht der Client die Datei wie bisher nach der Anzeige.
    /// </summary>
    public bool AddToGallery { get; set; }
}