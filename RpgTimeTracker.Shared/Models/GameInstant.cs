using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Game time's calendar-agnostic representation: elapsed seconds since an arbitrary epoch
///     (instant zero), replacing DateTime so game time isn't locked to the Gregorian calendar's
///     rules/range. Actual clock speed/tick math (GameClockService) stays entirely in real SI
///     seconds via TimeSpan deltas - only the stored "current point in time" changes type. A
///     CalendarDefinition converts a GameInstant to/from a human calendar date for display and
///     calendar-entry matching; GameInstant itself knows nothing about months/years/calendars.
/// </summary>
public readonly struct GameInstant : IEquatable<GameInstant>, IComparable<GameInstant>
{
    public static readonly GameInstant Zero = new(0);

    public GameInstant(long totalSeconds)
    {
        TotalSeconds = totalSeconds;
    }

    public long TotalSeconds { get; }

    public GameInstant Add(TimeSpan delta)
    {
        return new GameInstant(TotalSeconds + (long)delta.TotalSeconds);
    }

    public TimeSpan Subtract(GameInstant other)
    {
        return TimeSpan.FromSeconds(TotalSeconds - other.TotalSeconds);
    }

    public int CompareTo(GameInstant other)
    {
        return TotalSeconds.CompareTo(other.TotalSeconds);
    }

    public bool Equals(GameInstant other)
    {
        return TotalSeconds == other.TotalSeconds;
    }

    public override bool Equals(object? obj)
    {
        return obj is GameInstant other && Equals(other);
    }

    public override int GetHashCode()
    {
        return TotalSeconds.GetHashCode();
    }

    public static GameInstant operator +(GameInstant instant, TimeSpan delta)
    {
        return instant.Add(delta);
    }

    public static TimeSpan operator -(GameInstant a, GameInstant b)
    {
        return a.Subtract(b);
    }

    public static bool operator ==(GameInstant a, GameInstant b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(GameInstant a, GameInstant b)
    {
        return !a.Equals(b);
    }

    public static bool operator <(GameInstant a, GameInstant b)
    {
        return a.TotalSeconds < b.TotalSeconds;
    }

    public static bool operator <=(GameInstant a, GameInstant b)
    {
        return a.TotalSeconds <= b.TotalSeconds;
    }

    public static bool operator >(GameInstant a, GameInstant b)
    {
        return a.TotalSeconds > b.TotalSeconds;
    }

    public static bool operator >=(GameInstant a, GameInstant b)
    {
        return a.TotalSeconds >= b.TotalSeconds;
    }
}