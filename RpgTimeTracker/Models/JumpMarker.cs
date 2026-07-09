using System;

namespace RpgTimeTracker.Models;

/// <summary>
///     Eine konfigurierbare Sprungmarke für eine bestimmte Tageszeit
///     (z.B. "Morgengrauen" = 06:00). Beim Aktivieren springt die Spielzeit
///     zum nächsten bzw. vorherigen Auftreten dieser Uhrzeit.
/// </summary>
public class JumpMarker
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = "Marke";

    /// <summary>Tageszeit der Marke, z.B. new TimeSpan(6, 0, 0) für 06:00 Uhr.</summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>Nächstes Auftreten dieser Tageszeit ab dem gegebenen Zeitpunkt (exklusiv, falls genau jetzt).</summary>
    public DateTime NextOccurrence(DateTime from)
    {
        var target = from.Date + TimeOfDay;
        if (target <= from) target = target.AddDays(1);
        return target;
    }

    /// <summary>Vorheriges Auftreten dieser Tageszeit vor dem gegebenen Zeitpunkt (exklusiv, falls genau jetzt).</summary>
    public DateTime PreviousOccurrence(DateTime from)
    {
        var target = from.Date + TimeOfDay;
        if (target >= from) target = target.AddDays(-1);
        return target;
    }
}