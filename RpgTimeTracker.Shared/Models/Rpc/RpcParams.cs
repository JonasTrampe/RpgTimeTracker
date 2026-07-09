using System;
using System.Collections.Generic;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Models.Rpc;

/// <summary>Client-zu-Server: erstes Signal nach dem Verbindungsaufbau (siehe RpcMethods.SessionHello).</summary>
public sealed class SessionHelloParams
{
    public int ProtocolVersion { get; set; }
    public string Pin { get; set; } = string.Empty;
}

/// <summary>Server-zu-Client: Verbindung abgelehnt (siehe RpcMethods.SessionHelloRejected).</summary>
public sealed class SessionHelloRejectedParams
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class SessionSnapshotParams
{
    public DateTime CurrentGameTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public bool IsClockRunning { get; set; }
    public string PlayerHeaderTitle { get; set; } = "Spieleranzeige";
    public string PlayerHeaderSubtitle { get; set; } = string.Empty;
    public string Theme { get; set; } = "Shadowrun";
    public List<TimelineItemSnapshotDto> Items { get; set; } = new();
    public List<CalendarEntryDefinition> CalendarEntries { get; set; } = new();
}

public sealed class ClockSpeedChangedParams
{
    public double SpeedMultiplier { get; set; } = 1.0;
}

/// <summary>
///     Begleitet den periodischen Heartbeat mit dem aktuellen Uhrzustand, damit die lokal
///     abgeleitete Client-Uhr regelmäßig gegen Drift korrigiert wird - und damit sich der Client
///     auch von einem Delta-Event erholt, das wegen skipIfBusy (Medienübertragung lief gerade)
///     verworfen wurde, ohne auf den nächsten echten Sprung warten zu müssen.
/// </summary>
public sealed class ClockHeartbeatParams
{
    public DateTime CurrentGameTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public bool IsClockRunning { get; set; }
}

/// <summary>Absoluter Resync-Punkt (manuelles Setzen, Zeitsprung, Laden eines Spielstands).</summary>
public sealed class ClockTimeJumpedParams
{
    public DateTime NewGameTime { get; set; }
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

/// <summary>Client-zu-Server: Video-Wiedergabe hat begonnen, inkl. der vom Client selbst ermittelten Dauer.</summary>
public sealed class MediaPlaybackStartedParams
{
    public string MediaId { get; set; } = string.Empty;
    public long DurationMs { get; set; }
}

/// <summary>Client-zu-Server: ein nicht-loopendes Video ist beim Client zu Ende.</summary>
public sealed class MediaPlaybackEndedParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-zu-Client: beendet einen einzelnen, gerade laufenden Sound (per MediaId).</summary>
public sealed class MediaStopSoundParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-zu-Client: passt die Lautstärke eines gerade laufenden Sounds live an (0-100).</summary>
public sealed class MediaSetVolumeParams
{
    public string MediaId { get; set; } = string.Empty;
    public int Volume { get; set; } = 100;
}

/// <summary>Server-zu-Client: entfernt ein Bild/Video aus der Galerie (per MediaId).</summary>
public sealed class MediaRetractParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-zu-Client: springt (lokal, nicht sperrend) auf dieses Galerie-Element.</summary>
public sealed class MediaHighlightParams
{
    public string MediaId { get; set; } = string.Empty;
}

/// <summary>Server-zu-Client: automatische Weiterschalt-Zeit pro Bild (Sekunden, 0 = manuell).</summary>
public sealed class MediaSlideshowIntervalParams
{
    public double Seconds { get; set; }
}

/// <summary>
///     Voller Zustand eines Timers/Weckers/Intervalls für "upsert" (angelegt oder strukturell
///     geändert - Start/Pause/Reset/Fertig/Ausgelöst/bearbeitet). Wird NICHT bei reinem
///     Zeitfortschritt gesendet: Restzeit/Fortschritt leitet der Client lokal aus diesen Feldern
///     + seiner eigenen, per clock.*-Events synchronisierten Uhr ab.
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
    public DateTime TriggerAt { get; set; }
    public long? RepeatIntervalTicks { get; set; }
    public bool IsTriggered { get; set; }

    // Interval
    public long IntervalTicks { get; set; }
    public long ActiveDurationTicks { get; set; }
    public int? MaxRepeats { get; set; }
}