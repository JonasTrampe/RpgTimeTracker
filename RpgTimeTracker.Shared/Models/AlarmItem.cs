using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     An alarm that triggers at a specific game-time date/time.
///     Can optionally be recurring.
/// </summary>
public class AlarmItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Wecker";
    public string Icon { get; set; } = "Bootstrap: Alarm";
    public string Sound { get; set; } = "Pling";

    /// <summary>How many times the sound is played in a row when triggered.</summary>
    public int SoundRepeatCount { get; set; } = 1;

    public string ColorHex { get; set; } = string.Empty;
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; } = true;

    public GameInstant TriggerAt { get; set; }
    public TimeSpan? RepeatInterval { get; set; }

    public bool IsTriggered { get; private set; }

    public event Action? Triggered;

    public TimeSpan TimeRemaining(GameInstant currentGameTime)
    {
        var remaining = TriggerAt - currentGameTime;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public bool IsOverdue(GameInstant currentGameTime)
    {
        return currentGameTime >= TriggerAt && !IsTriggered;
    }

    public bool CheckTrigger(GameInstant currentGameTime)
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

    public bool SyncToTime(GameInstant currentGameTime)
    {
        if (IsTriggered && currentGameTime < TriggerAt) IsTriggered = false;

        return CheckTrigger(currentGameTime);
    }

    public void Reset(GameInstant currentGameTime)
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
    ///     Advances a recurring appointment in one step (instead of a loop) to the
    ///     next point in time after currentGameTime. Important for large time jumps with
    ///     a short RepeatInterval, where a loop would need millions of iterations.
    /// </summary>
    private static GameInstant AdvancePastDue(GameInstant triggerAt, GameInstant currentGameTime,
        TimeSpan repeatInterval)
    {
        if (triggerAt > currentGameTime) return triggerAt;

        var steps = (currentGameTime - triggerAt).Ticks / repeatInterval.Ticks + 1;
        return triggerAt + TimeSpan.FromTicks(steps * repeatInterval.Ticks);
    }
}