namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>
///     Namen aller JSON-RPC-Notifications (kein Request/Response, keine IDs). Der weit überwiegende
///     Teil läuft Server-zu-Client (SL-Host publiziert Zustandsänderungen); die beiden
///     MediaPlayback*-Methoden sind die einzige Ausnahme und laufen Client-zu-Server, damit der
///     Host weiß, wann ein Video beim Spieler tatsächlich zu Ende ist (siehe MediaPlaybackEnded).
/// </summary>
public static class RpcMethods
{
    /// <summary>
    ///     Client-zu-Server: erstes Signal direkt nach dem TCP-Verbindungsaufbau - Protokoll-Version
    ///     und optionaler PIN, bevor der Host mit dem vollen session.snapshot antwortet (siehe
    ///     TcpPlayerServerService.PerformHandshakeAsync). Ohne dieses Signal (Timeout) oder bei
    ///     falschem PIN/inkompatibler Version antwortet der Host stattdessen mit
    ///     session.helloRejected und trennt die Verbindung.
    /// </summary>
    public const string SessionHello = "session.hello";

    /// <summary>
    ///     Server-zu-Client: lehnt die Verbindung ab (falscher PIN, inkompatible Protokoll-Version,
    ///     oder kein session.hello innerhalb des Timeouts). Der Client soll das NICHT automatisch
    ///     erneut versuchen (siehe PlayerTcpClientService._userDisconnectRequested).
    /// </summary>
    public const string SessionHelloRejected = "session.helloRejected";

    /// <summary>Vollständiger Zustand für neu verbindende Clients (einmalig beim Connect).</summary>
    public const string SessionSnapshot = "session.snapshot";

    /// <summary>Reines Lebenszeichen ohne Nutzdaten, damit der Client eine tote Verbindung erkennt.</summary>
    public const string SessionHeartbeat = "session.heartbeat";

    public const string ClockStarted = "clock.started";
    public const string ClockStopped = "clock.stopped";
    public const string ClockSpeedChanged = "clock.speedChanged";

    /// <summary>Absoluter Resync-Punkt: manuelles Setzen der Zeit, Zeitsprung, oder Laden eines Spielstands.</summary>
    public const string ClockTimeJumped = "clock.timeJumped";

    public const string HeaderChanged = "header.changed";
    public const string ThemeChanged = "theme.changed";

    /// <summary>
    ///     Ein Timer/Wecker/Intervall wurde angelegt oder hat sich strukturell geändert
    ///     (Start/Pause/Reset/Fertig/Ausgelöst/Bearbeitet) - NICHT bei reinem Zeitfortschritt.
    /// </summary>
    public const string TimelineItemUpserted = "timelineItem.upserted";

    public const string TimelineItemRemoved = "timelineItem.removed";

    /// <summary>SL fordert den Client auf, sein Anzeigefenster (Liste) zu (ent-)maximieren.</summary>
    public const string DisplayFullscreen = "display.fullscreen";

    /// <summary>Kündigt ein Medium an; die Rohdaten folgen als Binär-Chunks (siehe NetworkFrame.TypeMediaChunk).</summary>
    public const string MediaBegin = "media.begin";

    public const string MediaCleared = "media.cleared";

    /// <summary>
    ///     Client-zu-Server: Wiedergabe eines Videos hat tatsächlich begonnen, inkl. der vom
    ///     Client selbst (LibVLC) ermittelten Dauer.
    /// </summary>
    public const string MediaPlaybackStarted = "media.playbackStarted";

    /// <summary>
    ///     Client-zu-Server: ein nicht-loopendes Video ist zu Ende - der Host nutzt das, um
    ///     eine dafür pausierte Spielzeit fortzusetzen und/oder das Medium bei allen Clients zu schließen.
    /// </summary>
    public const string MediaPlaybackEnded = "media.playbackEnded";

    /// <summary>
    ///     SL beendet einen einzelnen, gerade laufenden Sound gezielt (per MediaId) - unabhängig
    ///     vom Bild/Video-"aktuelles Medium"-Slot, siehe MediaHeaderDto.MediaKindAudio.
    /// </summary>
    public const string MediaStopSound = "media.stopSound";

    /// <summary>SL passt die Lautstärke eines gerade laufenden Sounds live an (per MediaId).</summary>
    public const string MediaSetVolume = "media.setVolume";

    /// <summary>
    ///     Entfernt ein Bild/Video gezielt aus der Galerie (per MediaId) - unabhängig vom
    ///     gerade angezeigten Element, siehe MediaHeaderDto.AddToGallery.
    /// </summary>
    public const string MediaRetract = "media.retract";

    /// <summary>
    ///     SL "hebt hervor": alle Clients springen lokal auf dieses Galerie-Element (per
    ///     MediaId), können sich danach aber weiterhin unabhängig davon wegbewegen.
    /// </summary>
    public const string MediaHighlight = "media.highlight";

    /// <summary>
    ///     Setzt die automatische Weiterschalt-Zeit pro Bild (Sekunden, 0 = manuell). Gilt
    ///     clientseitig nur für Bilder, nie für Videos (siehe design-decisions.md).
    /// </summary>
    public const string MediaSlideshowInterval = "media.slideshowInterval";
}