using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Ein Countdown-Timer, der an der Spielzeit hängt (nicht an der Echtzeit).
///     Farbwechsel-/OnTime-Logik ist bewusst nicht mehr Teil des Timers,
///     sondern liegt in IntervalEventItem.
/// </summary>
public class TimerItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Timer";
    public string Icon { get; set; } = "Bootstrap: Clock";
    public string Sound { get; set; } = "Pling";

    /// <summary>Wie oft der Sound beim Auslösen hintereinander abgespielt wird.</summary>
    public int SoundRepeatCount { get; set; } = 1;

    public string ColorHex { get; set; } = string.Empty;
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; }

    /// <summary>Gesamtdauer des Timers in Spielzeit.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Bereits verstrichene Spielzeit seit dem Start.</summary>
    public TimeSpan Elapsed { get; private set; } = TimeSpan.Zero;

    public bool IsRunning { get; private set; }
    public bool IsCompleted { get; private set; }

    public double Progress => Duration.Ticks <= 0
        ? 0
        : Math.Clamp((double)Elapsed.Ticks / Duration.Ticks, 0d, 1d);

    public TimeSpan Remaining
    {
        get
        {
            var remaining = Duration - Elapsed;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public event Action? Completed;

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

        var wasCompleted = IsCompleted;
        Elapsed += gameDelta;

        if (Elapsed < TimeSpan.Zero) Elapsed = TimeSpan.Zero;

        if (Elapsed >= Duration)
        {
            Elapsed = Duration;
            IsRunning = false;
            IsCompleted = true;
            if (!wasCompleted)
            {
                Completed?.Invoke();
                return true;
            }
        }
        else
        {
            IsCompleted = false;
        }

        return false;
    }

    public void Restore(TimeSpan elapsed, bool isRunning)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed > Duration) elapsed = Duration;

        Elapsed = elapsed;
        IsCompleted = Elapsed >= Duration;
        IsRunning = isRunning && !IsCompleted;
    }
}