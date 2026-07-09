using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Separates OnTime-/Intervall-Objekt: alle X Spielzeit wird für Y Spielzeit
///     ein aktiver Zustand ausgelöst. Unterstützt Sound, Farbe, Icon und Blinken.
/// </summary>
public class IntervalEventItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Intervall";
    public string Icon { get; set; } = "Bootstrap: Lightning";
    public string Sound { get; set; } = "Pling";

    /// <summary>Wie oft der Sound beim Auslösen hintereinander abgespielt wird.</summary>
    public int SoundRepeatCount { get; set; } = 1;

    public string ColorHex { get; set; } = "#FFD45A";
    public bool Blink { get; set; } = true;
    public bool IsPlayerVisible { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ActiveDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>0/null = unbegrenzt.</summary>
    public int? MaxRepeats { get; set; }

    public TimeSpan Elapsed { get; private set; } = TimeSpan.Zero;
    public bool IsRunning { get; private set; }
    public bool IsCompleted { get; private set; }

    public bool IsActive
    {
        get
        {
            if (Interval <= TimeSpan.Zero || ActiveDuration <= TimeSpan.Zero || Elapsed < Interval) return false;

            var repeat = CurrentRepeatNumber;
            if (repeat < 1 || IsBeyondMaxRepeats(repeat)) return false;

            var activeStart = TimeSpan.FromTicks(repeat * Interval.Ticks);
            var sinceStart = Elapsed - activeStart;
            return sinceStart >= TimeSpan.Zero && sinceStart < ActiveDuration;
        }
    }

    public int CurrentRepeatNumber
    {
        get
        {
            if (Interval <= TimeSpan.Zero || Elapsed < Interval) return 0;
            var count = (int)(Elapsed.Ticks / Interval.Ticks);
            if (MaxRepeats.HasValue && MaxRepeats.Value > 0) count = Math.Min(count, MaxRepeats.Value);
            return count;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (IsCompleted) return TimeSpan.Zero;

            if (IsActive)
            {
                var activeStart = TimeSpan.FromTicks(CurrentRepeatNumber * Interval.Ticks);
                var remainingActive = ActiveDuration - (Elapsed - activeStart);
                return remainingActive < TimeSpan.Zero ? TimeSpan.Zero : remainingActive;
            }

            var nextRepeat = Math.Max(1, (int)(Elapsed.Ticks / Interval.Ticks) + 1);
            if (MaxRepeats.HasValue && MaxRepeats.Value > 0 && nextRepeat > MaxRepeats.Value) return TimeSpan.Zero;

            var nextAt = TimeSpan.FromTicks(nextRepeat * Interval.Ticks);
            var remaining = nextAt - Elapsed;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public double Progress
    {
        get
        {
            if (Interval <= TimeSpan.Zero) return 0;
            var phase = Elapsed.Ticks % Interval.Ticks;
            return Math.Clamp((double)phase / Interval.Ticks, 0d, 1d);
        }
    }

    public void Start()
    {
        if (IsCompleted) return;
        IsRunning = true;
    }

    public void Pause()
    {
        IsRunning = false;
    }

    public void Reset()
    {
        Elapsed = TimeSpan.Zero;
        IsRunning = false;
        IsCompleted = false;
    }

    public bool Advance(TimeSpan gameDelta)
    {
        if (!IsRunning || gameDelta == TimeSpan.Zero) return false;

        var beforeRepeat = RawRepeatNumber(Elapsed);
        Elapsed += gameDelta;
        if (Elapsed < TimeSpan.Zero) Elapsed = TimeSpan.Zero;

        var afterRepeat = RawRepeatNumber(Elapsed);
        var triggeredNow = afterRepeat > beforeRepeat && afterRepeat > 0 && !IsBeyondMaxRepeats(afterRepeat);

        if (MaxRepeats.HasValue && MaxRepeats.Value > 0)
        {
            var endAt = TimeSpan.FromTicks(MaxRepeats.Value * Interval.Ticks) + ActiveDuration;
            if (Elapsed >= endAt)
            {
                IsCompleted = true;
                IsRunning = false;
            }
        }

        return triggeredNow;
    }

    public void Restore(TimeSpan elapsed, bool isRunning, bool isCompleted)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        Elapsed = elapsed;
        IsCompleted = isCompleted;
        IsRunning = isRunning && !IsCompleted;
    }

    private int RawRepeatNumber(TimeSpan elapsed)
    {
        if (Interval <= TimeSpan.Zero || elapsed < Interval) return 0;
        return (int)(elapsed.Ticks / Interval.Ticks);
    }

    private bool IsBeyondMaxRepeats(int repeatNumber)
    {
        return MaxRepeats.HasValue && MaxRepeats.Value > 0 && repeatNumber > MaxRepeats.Value;
    }
}