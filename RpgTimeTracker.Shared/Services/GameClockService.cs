using System;
using System.Diagnostics;
using Avalonia.Threading;

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
/// </summary>
public class GameClockService : IDisposable
{
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;
    private TimeSpan _lastElapsed = TimeSpan.Zero;

    public GameClockService(DateTime startTime)
    {
        CurrentTime = startTime;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTimerTick;
    }

    public DateTime CurrentTime { get; private set; }
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
    public event Action<DateTime, TimeSpan>? Tick;

    /// <summary>
    ///     Raised ONLY on an explicit jump (Jump/SetTime), not on normal
    ///     progression via OnTimerTick. The GM host uses this to send clock.timeJumped over the
    ///     network only for real jumps instead of on every tick.
    /// </summary>
    public event Action<DateTime>? Jumped;

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
    public void SetTime(DateTime newTime)
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

        CurrentTime += gameDelta;
        Tick?.Invoke(CurrentTime, gameDelta);
    }
}