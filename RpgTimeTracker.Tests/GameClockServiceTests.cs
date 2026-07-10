using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class GameClockServiceTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 8, 0, 0);

    [Fact]
    public void Jump_forward_advances_current_time()
    {
        using var clock = new GameClockService(Start);

        clock.Jump(TimeSpan.FromHours(8));

        Assert.Equal(Start + TimeSpan.FromHours(8), clock.CurrentTime);
    }

    [Fact]
    public void Jump_backward_rewinds_current_time()
    {
        using var clock = new GameClockService(Start);

        clock.Jump(-TimeSpan.FromDays(1));

        Assert.Equal(Start - TimeSpan.FromDays(1), clock.CurrentTime);
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
        DateTime? tickTime = null;
        TimeSpan? tickDelta = null;
        DateTime? jumpedTime = null;
        clock.Tick += (time, delta) =>
        {
            tickTime = time;
            tickDelta = delta;
        };
        clock.Jumped += time => jumpedTime = time;

        var delta = TimeSpan.FromHours(3);
        clock.Jump(delta);

        Assert.Equal(Start + delta, tickTime);
        Assert.Equal(delta, tickDelta);
        Assert.Equal(Start + delta, jumpedTime);
    }

    [Fact]
    public void SetTime_is_equivalent_to_a_jump_by_the_difference()
    {
        using var clock = new GameClockService(Start);
        TimeSpan? tickDelta = null;
        clock.Tick += (_, delta) => tickDelta = delta;

        var target = Start.AddDays(2).AddHours(-1);
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
