using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Tests;

public class TimerItemTests
{
    private static TimerItem CreateRunning(TimeSpan duration)
    {
        var timer = new TimerItem { Duration = duration };
        timer.Start();
        return timer;
    }

    [Fact]
    public void Advance_while_not_running_does_nothing()
    {
        var timer = new TimerItem { Duration = TimeSpan.FromMinutes(10) };

        var completed = timer.Advance(TimeSpan.FromMinutes(5));

        Assert.False(completed);
        Assert.Equal(TimeSpan.Zero, timer.Elapsed);
    }

    [Fact]
    public void Advance_partial_updates_elapsed_and_progress_without_completing()
    {
        var timer = CreateRunning(TimeSpan.FromMinutes(10));

        var completed = timer.Advance(TimeSpan.FromMinutes(4));

        Assert.False(completed);
        Assert.False(timer.IsCompleted);
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(4), timer.Elapsed);
        Assert.Equal(0.4, timer.Progress, 3);
        Assert.Equal(TimeSpan.FromMinutes(6), timer.Remaining);
    }

    [Fact]
    public void Advance_past_duration_completes_exactly_once_and_clamps_elapsed()
    {
        var timer = CreateRunning(TimeSpan.FromMinutes(10));
        var completedEventCount = 0;
        timer.Completed += () => completedEventCount++;

        var completedNow = timer.Advance(TimeSpan.FromMinutes(15));

        Assert.True(completedNow);
        Assert.True(timer.IsCompleted);
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Elapsed);
        Assert.Equal(1, completedEventCount);

        // Further advances (e.g. from a later time jump) must not re-fire Completed.
        var completedAgain = timer.Advance(TimeSpan.FromMinutes(5));
        Assert.False(completedAgain);
        Assert.Equal(1, completedEventCount);
    }

    [Fact]
    public void Advance_backward_past_start_clamps_elapsed_to_zero_and_uncompletes()
    {
        var timer = CreateRunning(TimeSpan.FromMinutes(10));
        timer.Advance(TimeSpan.FromMinutes(10));
        Assert.True(timer.IsCompleted);

        // Timers only react to rewinds while running (see IsRunning guard) - a completed
        // timer is not running anymore, so re-arm it first, matching how a GM would restart it.
        timer.Reset();
        timer.Start();
        timer.Advance(TimeSpan.FromMinutes(4));
        timer.Advance(-TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.Zero, timer.Elapsed);
        Assert.False(timer.IsCompleted);
    }

    [Fact]
    public void Reset_clears_elapsed_and_completion_state()
    {
        var timer = CreateRunning(TimeSpan.FromMinutes(10));
        timer.Advance(TimeSpan.FromMinutes(10));

        timer.Reset();

        Assert.Equal(TimeSpan.Zero, timer.Elapsed);
        Assert.False(timer.IsRunning);
        Assert.False(timer.IsCompleted);
    }

    [Fact]
    public void Restore_clamps_elapsed_to_duration_and_marks_completed()
    {
        var timer = new TimerItem { Duration = TimeSpan.FromMinutes(10) };

        timer.Restore(TimeSpan.FromMinutes(999), true);

        Assert.Equal(TimeSpan.FromMinutes(10), timer.Elapsed);
        Assert.True(timer.IsCompleted);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Restore_clamps_negative_elapsed_to_zero()
    {
        var timer = new TimerItem { Duration = TimeSpan.FromMinutes(10) };

        timer.Restore(-TimeSpan.FromMinutes(1), false);

        Assert.Equal(TimeSpan.Zero, timer.Elapsed);
        Assert.False(timer.IsCompleted);
    }
}