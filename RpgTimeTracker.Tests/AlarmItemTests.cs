using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Tests;

public class AlarmItemTests
{
    private static readonly GameInstant TriggerAt = new(1_700_000_000);

    [Fact]
    public void TimeRemaining_before_trigger_counts_down()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };

        var remaining = alarm.TimeRemaining(TriggerAt.Add(-TimeSpan.FromHours(2)));

        Assert.Equal(TimeSpan.FromHours(2), remaining);
    }

    [Fact]
    public void TimeRemaining_after_trigger_clamps_to_zero()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };

        var remaining = alarm.TimeRemaining(TriggerAt.Add(TimeSpan.FromHours(1)));

        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void IsOverdue_is_true_only_once_time_reached_and_not_yet_triggered()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };

        Assert.False(alarm.IsOverdue(TriggerAt.Add(-TimeSpan.FromMinutes(1))));
        Assert.True(alarm.IsOverdue(TriggerAt));

        alarm.CheckTrigger(TriggerAt);

        Assert.False(alarm.IsOverdue(TriggerAt));
    }

    [Fact]
    public void CheckTrigger_fires_once_when_time_reaches_TriggerAt()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };
        var triggerCount = 0;
        alarm.Triggered += () => triggerCount++;

        var triggeredNow = alarm.CheckTrigger(TriggerAt);

        Assert.True(triggeredNow);
        Assert.True(alarm.IsTriggered);
        Assert.Equal(1, triggerCount);

        // A later check at the same or later time must not re-fire a non-repeating alarm.
        var triggeredAgain = alarm.CheckTrigger(TriggerAt.Add(TimeSpan.FromMinutes(5)));
        Assert.False(triggeredAgain);
        Assert.Equal(1, triggerCount);
    }

    [Fact]
    public void SyncToTime_disarms_an_already_triggered_alarm_when_rewinding_before_TriggerAt()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };
        alarm.CheckTrigger(TriggerAt);
        Assert.True(alarm.IsTriggered);

        // Jumping backward past the trigger time "disarms" it again (see README time-jump behavior).
        alarm.SyncToTime(TriggerAt.Add(-TimeSpan.FromHours(1)));

        Assert.False(alarm.IsTriggered);
    }

    [Fact]
    public void SyncToTime_re_triggers_after_being_disarmed_by_a_rewind()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };
        var triggerCount = 0;
        alarm.Triggered += () => triggerCount++;

        alarm.SyncToTime(TriggerAt);
        alarm.SyncToTime(TriggerAt.Add(-TimeSpan.FromHours(1)));
        var triggeredAgain = alarm.SyncToTime(TriggerAt);

        Assert.True(triggeredAgain);
        Assert.Equal(2, triggerCount);
    }

    [Fact]
    public void Repeating_alarm_advances_TriggerAt_by_one_interval_and_is_ready_to_trigger_again()
    {
        var interval = TimeSpan.FromHours(24);
        var alarm = new AlarmItem { TriggerAt = TriggerAt, RepeatInterval = interval };

        alarm.CheckTrigger(TriggerAt);

        Assert.False(alarm.IsTriggered);
        Assert.Equal(TriggerAt.Add(interval), alarm.TriggerAt);
    }

    [Fact]
    public void Repeating_alarm_survives_a_huge_forward_jump_in_a_single_step()
    {
        // A large time jump (e.g. "+30 days") with a short repeat interval must not require
        // one loop iteration per missed occurrence - AdvancePastDue does this in one step.
        var interval = TimeSpan.FromMinutes(1);
        var alarm = new AlarmItem { TriggerAt = TriggerAt, RepeatInterval = interval };

        var farFuture = TriggerAt.Add(TimeSpan.FromDays(30));
        var triggeredNow = alarm.CheckTrigger(farFuture);

        Assert.True(triggeredNow);
        Assert.True(alarm.TriggerAt > farFuture);
        Assert.True(alarm.TriggerAt - farFuture <= interval);
    }

    [Fact]
    public void Restore_sets_triggered_state_directly()
    {
        var alarm = new AlarmItem { TriggerAt = TriggerAt };

        alarm.Restore(isTriggered: true);

        Assert.True(alarm.IsTriggered);
    }
}
