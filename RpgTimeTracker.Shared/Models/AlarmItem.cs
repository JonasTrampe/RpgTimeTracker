using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Ein Wecker, der bei einem bestimmten Spielzeit-Datum/Zeitpunkt auslöst.
///     Kann optional wiederkehrend sein.
/// </summary>
public class AlarmItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Wecker";
    public string Icon { get; set; } = "Bootstrap: Alarm";
    public string Sound { get; set; } = "Pling";

    /// <summary>Wie oft der Sound beim Auslösen hintereinander abgespielt wird.</summary>
    public int SoundRepeatCount { get; set; } = 1;

    public string ColorHex { get; set; } = string.Empty;
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; } = true;

    public DateTime TriggerAt { get; set; }
    public TimeSpan? RepeatInterval { get; set; }

    public bool IsTriggered { get; private set; }

    public event Action? Triggered;

    public TimeSpan TimeRemaining(DateTime currentGameTime)
    {
        var remaining = TriggerAt - currentGameTime;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public bool IsOverdue(DateTime currentGameTime)
    {
        return currentGameTime >= TriggerAt && !IsTriggered;
    }

    public bool CheckTrigger(DateTime currentGameTime)
    {
        if (IsTriggered || currentGameTime < TriggerAt) return false;

        IsTriggered = true;
        Triggered?.Invoke();

        if (RepeatInterval.HasValue && RepeatInterval.Value > TimeSpan.Zero)
        {
            TriggerAt = AdvancePastDue(TriggerAt, currentGameTime, RepeatInterval.Value);
            IsTriggered = false;
        }

        return true;
    }

    public bool SyncToTime(DateTime currentGameTime)
    {
        if (IsTriggered && currentGameTime < TriggerAt) IsTriggered = false;

        return CheckTrigger(currentGameTime);
    }

    public void Reset(DateTime currentGameTime)
    {
        IsTriggered = false;
        if (TriggerAt <= currentGameTime && RepeatInterval.HasValue && RepeatInterval.Value > TimeSpan.Zero)
            TriggerAt = AdvancePastDue(TriggerAt, currentGameTime, RepeatInterval.Value);
    }

    public void Restore(bool isTriggered)
    {
        IsTriggered = isTriggered;
    }

    /// <summary>
    ///     Rückt einen wiederkehrenden Termin in einem Schritt (statt einer Schleife) auf den
    ///     nächsten Zeitpunkt nach currentGameTime vor. Wichtig bei großen Zeitsprüngen mit
    ///     kurzem RepeatInterval, wo eine Schleife Millionen Iterationen bräuchte.
    /// </summary>
    private static DateTime AdvancePastDue(DateTime triggerAt, DateTime currentGameTime, TimeSpan repeatInterval)
    {
        if (triggerAt > currentGameTime) return triggerAt;

        var steps = (currentGameTime - triggerAt).Ticks / repeatInterval.Ticks + 1;
        return triggerAt + TimeSpan.FromTicks(steps * repeatInterval.Ticks);
    }
}