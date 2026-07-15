using System.Reflection;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class GameClockServiceTests
{
    private static readonly GameInstant Start = new(1_000_000);

    [Fact]
    public void Jump_forward_advances_current_time()
    {
        using var clock = new GameClockService(Start);

        clock.Jump(TimeSpan.FromHours(8));

        Assert.Equal(Start.Add(TimeSpan.FromHours(8)), clock.CurrentTime);
    }

    [Fact]
    public void Jump_backward_rewinds_current_time()
    {
        using var clock = new GameClockService(Start);

        clock.Jump(-TimeSpan.FromDays(1));

        Assert.Equal(Start.Add(-TimeSpan.FromDays(1)), clock.CurrentTime);
    }

    [Fact]
    public void Jump_by_zero_does_not_raise_events()
    {
        using var clock = new GameClockService(Start);
        var tickRaised = false;
        var jumpedRaised = false;
        clock.Tick += (_, _) => tickRaised = true;
        clock.Jumped += _ => jumpedRaised = true;

        clock.Jump(TimeSpan.Zero);

        Assert.False(tickRaised);
        Assert.False(jumpedRaised);
        Assert.Equal(Start, clock.CurrentTime);
    }

    [Fact]
    public void Jump_raises_Tick_with_new_time_and_delta_and_raises_Jumped()
    {
        using var clock = new GameClockService(Start);
        GameInstant? tickTime = null;
        TimeSpan? tickDelta = null;
        GameInstant? jumpedTime = null;
        clock.Tick += (time, delta) =>
        {
            tickTime = time;
            tickDelta = delta;
        };
        clock.Jumped += time => jumpedTime = time;

        var delta = TimeSpan.FromHours(3);
        clock.Jump(delta);

        Assert.Equal(Start.Add(delta), tickTime);
        Assert.Equal(delta, tickDelta);
        Assert.Equal(Start.Add(delta), jumpedTime);
    }

    [Fact]
    public void SetTime_is_equivalent_to_a_jump_by_the_difference()
    {
        using var clock = new GameClockService(Start);
        TimeSpan? tickDelta = null;
        clock.Tick += (_, delta) => tickDelta = delta;

        var target = Start.Add(TimeSpan.FromDays(2)).Add(TimeSpan.FromHours(-1));
        clock.SetTime(target);

        Assert.Equal(target, clock.CurrentTime);
        Assert.Equal(target - Start, tickDelta);
    }

    [Fact]
    public void SetTime_to_the_current_time_is_a_no_op()
    {
        using var clock = new GameClockService(Start);
        var tickRaised = false;
        clock.Tick += (_, _) => tickRaised = true;

        clock.SetTime(Start);

        Assert.False(tickRaised);
    }

    [Fact]
    public void SpeedMultiplier_defaults_to_realtime_and_can_be_changed()
    {
        using var clock = new GameClockService(Start);

        Assert.Equal(1.0, clock.SpeedMultiplier);

        clock.SpeedMultiplier = 60.0;

        Assert.Equal(60.0, clock.SpeedMultiplier);
    }

    /// <summary>
    ///     Regression test for a real bug: the DispatcherTimer fires every 200ms, so each
    ///     tick's real-time delta at 1x speed is well under a second. GameInstant.Add truncates a
    ///     TimeSpan to whole seconds via a (long) cast, so applying each tick's delta directly
    ///     silently discarded it every time - CurrentTime never advanced at all, while Timers/Alarms
    ///     (which track elapsed time as a plain TimeSpan, not through GameInstant) kept working
    ///     fine, making the bug look like "just the display is frozen." Fixed by accumulating the
    ///     sub-second remainder across ticks instead of dropping it (see GameClockService's
    ///     _carrySeconds field) - this drives the same private OnTimerTick the DispatcherTimer
    ///     calls, since a DispatcherTimer needs a running Avalonia dispatcher to fire on its own,
    ///     which a plain unit test doesn't have.
    /// </summary>
    [Fact]
    public void Repeated_sub_second_ticks_accumulate_instead_of_being_dropped()
    {
        using var clock = new GameClockService(Start);
        clock.Start();

        var onTimerTick =
            typeof(GameClockService).GetMethod("OnTimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 25 ticks x ~50ms real each (well under 1s per tick, matching the DispatcherTimer's 200ms
        // interval at 1x-4x speed) - without the fix, every single one truncates to zero and
        // CurrentTime never moves, no matter how many ticks fire.
        for (var i = 0; i < 25; i++)
        {
            Thread.Sleep(50);
            onTimerTick.Invoke(clock, [null, EventArgs.Empty]);
        }

        Assert.True(clock.CurrentTime > Start,
            "CurrentTime should have advanced from the accumulated sub-second ticks, not stayed frozen at Start.");
    }

    [Fact]
    public void Start_and_Pause_toggle_IsRunning()
    {
        using var clock = new GameClockService(Start);

        Assert.False(clock.IsRunning);

        clock.Start();
        Assert.True(clock.IsRunning);

        clock.Pause();
        Assert.False(clock.IsRunning);
    }
}