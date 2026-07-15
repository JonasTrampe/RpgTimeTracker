using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Tests;

public class IntervalEventItemTests
{
    private static IntervalEventItem CreateRunning(TimeSpan interval, TimeSpan activeDuration, int? maxRepeats = null)
    {
        var item = new IntervalEventItem
        {
            Interval = interval,
            ActiveDuration = activeDuration,
            MaxRepeats = maxRepeats
        };
        item.Start();
        return item;
    }

    [Fact]
    public void Advance_while_not_running_does_nothing()
    {
        var item = new IntervalEventItem
            { Interval = TimeSpan.FromMinutes(10), ActiveDuration = TimeSpan.FromMinutes(1) };

        var triggered = item.Advance(TimeSpan.FromMinutes(10));

        Assert.False(triggered);
        Assert.Equal(TimeSpan.Zero, item.Elapsed);
    }

    [Fact]
    public void IsActive_is_false_before_first_interval_elapses()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        item.Advance(TimeSpan.FromMinutes(9));

        Assert.False(item.IsActive);
        Assert.Equal(0, item.CurrentRepeatNumber);
    }

    [Fact]
    public void IsActive_becomes_true_exactly_at_the_interval_boundary_and_lasts_ActiveDuration()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        item.Advance(TimeSpan.FromMinutes(10));
        Assert.True(item.IsActive);
        Assert.Equal(1, item.CurrentRepeatNumber);

        item.Advance(TimeSpan.FromSeconds(59));
        Assert.True(item.IsActive);

        item.Advance(TimeSpan.FromSeconds(2));
        Assert.False(item.IsActive);
    }

    [Fact]
    public void Advance_reports_triggered_only_on_the_tick_that_crosses_into_a_new_repeat()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        var firstHalf = item.Advance(TimeSpan.FromMinutes(5));
        var crossing = item.Advance(TimeSpan.FromMinutes(5));
        var withinSameRepeat = item.Advance(TimeSpan.FromSeconds(10));

        Assert.False(firstHalf);
        Assert.True(crossing);
        Assert.False(withinSameRepeat);
    }

    [Fact]
    public void A_single_large_jump_across_multiple_repeats_still_reports_triggered_once()
    {
        // Mirrors AlarmItem's single-step behavior: a big time jump that skips several
        // whole repeats must not require one Advance call per repeat to detect the crossing.
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        var triggered = item.Advance(TimeSpan.FromMinutes(35));

        Assert.True(triggered);
        Assert.Equal(3, item.CurrentRepeatNumber);
    }

    [Fact]
    public void MaxRepeats_completes_the_item_once_the_final_active_window_ends()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), 2);

        item.Advance(TimeSpan.FromMinutes(21));

        Assert.True(item.IsCompleted);
        Assert.False(item.IsRunning);
    }

    [Fact]
    public void MaxRepeats_prevents_IsActive_beyond_the_final_repeat()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), 1);

        item.Advance(TimeSpan.FromMinutes(20));

        Assert.False(item.IsActive);
    }

    [Fact]
    public void Progress_is_the_fractional_position_within_the_current_interval()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        item.Advance(TimeSpan.FromMinutes(2.5));

        Assert.Equal(0.25, item.Progress, 3);
    }

    [Fact]
    public void Reset_clears_elapsed_running_and_completed_state()
    {
        var item = CreateRunning(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), 1);
        item.Advance(TimeSpan.FromMinutes(11));
        Assert.True(item.IsCompleted);

        item.Reset();

        Assert.Equal(TimeSpan.Zero, item.Elapsed);
        Assert.False(item.IsRunning);
        Assert.False(item.IsCompleted);
    }

    [Fact]
    public void Restore_clamps_negative_elapsed_to_zero_and_respects_completed_flag()
    {
        var item = new IntervalEventItem
            { Interval = TimeSpan.FromMinutes(10), ActiveDuration = TimeSpan.FromMinutes(1) };

        item.Restore(-TimeSpan.FromMinutes(1), true, true);

        Assert.Equal(TimeSpan.Zero, item.Elapsed);
        Assert.True(item.IsCompleted);
        Assert.False(item.IsRunning);
    }
}