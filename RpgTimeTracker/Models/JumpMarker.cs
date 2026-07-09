using System;

namespace RpgTimeTracker.Models;

/// <summary>
///     A configurable jump marker for a specific time of day
///     (e.g. "dawn" = 06:00). When activated, the game time jumps
///     to the next or previous occurrence of this time.
/// </summary>
public class JumpMarker
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = "Marke";

    /// <summary>Time of day for the marker, e.g. new TimeSpan(6, 0, 0) for 06:00.</summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>Next occurrence of this time of day from the given point in time (exclusive if exactly now).</summary>
    public DateTime NextOccurrence(DateTime from)
    {
        var target = from.Date + TimeOfDay;
        if (target <= from) target = target.AddDays(1);
        return target;
    }

    /// <summary>Previous occurrence of this time of day before the given point in time (exclusive if exactly now).</summary>
    public DateTime PreviousOccurrence(DateTime from)
    {
        var target = from.Date + TimeOfDay;
        if (target >= from) target = target.AddDays(-1);
        return target;
    }
}