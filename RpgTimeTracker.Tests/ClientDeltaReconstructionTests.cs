using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

/// <summary>
///     The host and the player client each run their own GameClockService,
///     wiring its Tick/Jumped events into the exact same TimerItem/AlarmItem/
///     IntervalEventItem model methods (see ClientMainWindowViewModel.OnLocalClockTick)
///     instead of the client asking the host for remaining time on every tick. These
///     tests simulate that "host" and "client" side by side, driven by the same
///     sequence of jumps/speed changes but through two independent clocks/models, and
///     assert both ends up in identical state - proving the reconstruction is a pure
///     function of (starting state, sequence of deltas), not something that silently
///     depends on wall-clock timing or which side computed it.
/// </summary>
public class ClientDeltaReconstructionTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 8, 0, 0);

    private sealed class Session
    {
        public GameClockService Clock { get; }
        public TimerItem Timer { get; } = new() { Duration = TimeSpan.FromMinutes(30) };
        public AlarmItem Alarm { get; } = new() { TriggerAt = Start + TimeSpan.FromHours(4) };
        public IntervalEventItem Interval { get; } = new()
        {
            Interval = TimeSpan.FromMinutes(15),
            ActiveDuration = TimeSpan.FromMinutes(2)
        };

        public Session()
        {
            Clock = new GameClockService(Start);
            Clock.Tick += (newTime, delta) =>
            {
                Timer.Advance(delta);
                Alarm.SyncToTime(newTime);
                Interval.Advance(delta);
            };
            Timer.Start();
            Interval.Start();
        }
    }

    [Fact]
    public void Host_and_client_end_up_in_identical_state_after_the_same_sequence_of_jumps()
    {
        var host = new Session();
        var client = new Session();

        void ApplyToBoth(Action<Session> action)
        {
            action(host);
            action(client);
        }

        // Rest for an hour (timer completes, interval fires twice), then a big forward
        // jump past the alarm, then a rewind that should disarm the alarm again.
        ApplyToBoth(s => s.Clock.Jump(TimeSpan.FromHours(1)));
        ApplyToBoth(s => s.Clock.Jump(TimeSpan.FromHours(4)));
        ApplyToBoth(s => s.Clock.Jump(-TimeSpan.FromHours(2)));

        Assert.Equal(host.Clock.CurrentTime, client.Clock.CurrentTime);

        Assert.Equal(host.Timer.Elapsed, client.Timer.Elapsed);
        Assert.Equal(host.Timer.IsCompleted, client.Timer.IsCompleted);
        Assert.True(host.Timer.IsCompleted);

        Assert.Equal(host.Alarm.IsTriggered, client.Alarm.IsTriggered);
        Assert.Equal(host.Alarm.TriggerAt, client.Alarm.TriggerAt);
        Assert.False(host.Alarm.IsTriggered);

        Assert.Equal(host.Interval.Elapsed, client.Interval.Elapsed);
        Assert.Equal(host.Interval.CurrentRepeatNumber, client.Interval.CurrentRepeatNumber);
    }

    [Fact]
    public void Client_reconstructs_the_same_remaining_time_regardless_of_jump_granularity()
    {
        // The host might apply one big jump while the client (or a differently-timed
        // reconnect) sees the same total delta split into several smaller ticks - the
        // end state must be identical either way, since Advance/SyncToTime are pure
        // functions of the accumulated delta, not of how it was chunked.
        var host = new Session();
        var client = new Session();

        host.Clock.Jump(TimeSpan.FromMinutes(37));

        client.Clock.Jump(TimeSpan.FromMinutes(10));
        client.Clock.Jump(TimeSpan.FromMinutes(10));
        client.Clock.Jump(TimeSpan.FromMinutes(10));
        client.Clock.Jump(TimeSpan.FromMinutes(7));

        Assert.Equal(host.Clock.CurrentTime, client.Clock.CurrentTime);
        Assert.Equal(host.Timer.Remaining, client.Timer.Remaining);
        Assert.Equal(host.Timer.IsCompleted, client.Timer.IsCompleted);
        Assert.Equal(host.Interval.Remaining, client.Interval.Remaining);
        Assert.Equal(host.Interval.CurrentRepeatNumber, client.Interval.CurrentRepeatNumber);
    }
}
