using System;
using System.Diagnostics;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Drives a "game time" (GameTime) that can run faster or slower than
///     real time. A DispatcherTimer measures the real elapsed time
///     between two ticks and multiplies it by SpeedMultiplier to
///     advance game time.
///     Used both by the GM host (driven by user input) and the player client
///     (driven by RPC events, see RemoteClockSync) - both sides must use
///     the same derivation logic so that remaining time/progress is calculated locally
///     identically to the server, without every tick having to be sent over the network.
///     Game time itself is a calendar-agnostic GameInstant (elapsed seconds since an arbitrary
///     epoch) - this class does all its arithmetic in real SI seconds via TimeSpan deltas and
///     never needs to know about months/years/calendars; only display code and
///     CalendarEntryDefinition's recurrence matching consult a CalendarDefinition.
/// </summary>
public class GameClockService : IDisposable
{
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;

    /// <summary>
    ///     Fractional game-seconds not yet applied to CurrentTime, carried over between
    ///     ticks. GameInstant only stores whole seconds (see its doc comment), and the DispatcherTimer
    ///     fires every 200ms - at 1x speed that's a ~0.2s delta per tick, which truncates straight to
    ///     zero if applied directly (see GameInstant.Add's (long) cast), silently discarding the
    ///     game time and freezing CurrentTime as a result. Accumulating the remainder here instead
    ///     of dropping it means CurrentTime still advances correctly once enough sub-second ticks
    ///     add up to a whole second, however slow SpeedMultiplier or the tick interval get.
    /// </summary>
    private double _carrySeconds;

    private TimeSpan _lastElapsed = TimeSpan.Zero;

    public GameClockService(GameInstant startTime)
    {
        CurrentTime = startTime;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTimerTick;
    }

    public GameInstant CurrentTime { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>
    ///     Time factor: 1.0 = game time runs like real time, 60.0 = 1 second real
    ///     equals 1 minute game time, 0.5 = half speed, etc.
    /// </summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    public void Dispose()
    {
        _timer.Tick -= OnTimerTick;
        _timer.Stop();
    }

    /// <summary>Raised on every tick with the new point in time and the game-time delta.</summary>
    public event Action<GameInstant, TimeSpan>? Tick;

    /// <summary>
    ///     Raised ONLY on an explicit jump (Jump/SetTime), not on normal
    ///     progression via OnTimerTick. The GM host uses this to send clock.timeJumped over the
    ///     network only for real jumps instead of on every tick.
    /// </summary>
    public event Action<GameInstant>? Jumped;

    public void Start()
    {
        if (IsRunning) return;
        _lastElapsed = _stopwatch.Elapsed;
        _stopwatch.Start();
        _timer.Start();
        IsRunning = true;
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _timer.Stop();
        _stopwatch.Stop();
        IsRunning = false;
    }

    /// <summary>
    ///     Sets the game time directly to a new point in time (e.g. a manually
    ///     entered date). Internally just a jump by the difference to the
    ///     current time - timers/alarms therefore react identically to Jump().
    /// </summary>
    public void SetTime(GameInstant newTime)
    {
        Jump(newTime - CurrentTime);
    }

    /// <summary>
    ///     Fast-forwards or rewinds the game time by a fixed delta
    ///     (e.g. "+8 hours rest" or "-1 day" to rewind).
    ///     Raises the Tick event like a normal tick, so timers and alarms
    ///     stay in sync.
    /// </summary>
    public void Jump(TimeSpan delta)
    {
        if (delta == TimeSpan.Zero) return;
        CurrentTime += delta;
        Tick?.Invoke(CurrentTime, delta);
        Jumped?.Invoke(CurrentTime);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var nowElapsed = _stopwatch.Elapsed;
        var realDelta = nowElapsed - _lastElapsed;
        _lastElapsed = nowElapsed;

        if (realDelta <= TimeSpan.Zero) return;

        var gameDelta = TimeSpan.FromTicks((long)(realDelta.Ticks * SpeedMultiplier));
        if (gameDelta == TimeSpan.Zero) return;

        // Apply only whole seconds to CurrentTime, keeping the sub-second remainder for next
        // tick instead of letting GameInstant.Add's (long) cast silently drop it every time.
        _carrySeconds += gameDelta.TotalSeconds;
        var wholeSeconds = (long)_carrySeconds;
        _carrySeconds -= wholeSeconds;
        if (wholeSeconds != 0) CurrentTime += TimeSpan.FromSeconds(wholeSeconds);

        Tick?.Invoke(CurrentTime, gameDelta);
    }
}