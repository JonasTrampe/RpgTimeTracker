using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Treibt eine "Spielzeit" (GameTime) an, die schneller oder langsamer als die
///     Echtzeit laufen kann. Ein DispatcherTimer misst die reale vergangene Zeit
///     zwischen zwei Ticks und multipliziert sie mit SpeedMultiplier, um die
///     Spielzeit voranzutreiben.
///     Wird sowohl vom SL-Host (nutzereingaben-getrieben) als auch vom Spieler-Client
///     (RPC-Event-getrieben, siehe RemoteClockSync) verwendet - beide Seiten müssen mit
///     derselben Ableitungslogik rechnen, damit Restzeiten/Fortschritt lokal identisch
///     zum Server berechnet werden, ohne dass jeder Tick übers Netz geschickt werden muss.
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
    ///     Zeitfaktor: 1.0 = Spielzeit läuft wie Echtzeit, 60.0 = 1 Sekunde real
    ///     entspricht 1 Minute Spielzeit, 0.5 = halbe Geschwindigkeit usw.
    /// </summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    public void Dispose()
    {
        _timer.Tick -= OnTimerTick;
        _timer.Stop();
    }

    /// <summary>Wird bei jedem Tick mit dem neuen Zeitpunkt und dem Spielzeit-Delta ausgelöst.</summary>
    public event Action<DateTime, TimeSpan>? Tick;

    /// <summary>
    ///     Wird NUR bei einem expliziten Sprung (Jump/SetTime) ausgelöst, nicht beim normalen
    ///     Fortschreiten durch OnTimerTick. Der SL-Host nutzt das, um clock.timeJumped nur bei
    ///     echten Sprüngen übers Netz zu senden statt bei jedem Tick.
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
    ///     Setzt die Spielzeit direkt auf einen neuen Zeitpunkt (z.B. manuell
    ///     eingegebenes Datum). Intern nur ein Sprung um die Differenz zur
    ///     aktuellen Zeit - Timer/Wecker reagieren also identisch wie bei Jump().
    /// </summary>
    public void SetTime(DateTime newTime)
    {
        Jump(newTime - CurrentTime);
    }

    /// <summary>
    ///     Spult die Spielzeit um ein festes Delta vor oder zurück
    ///     (z.B. "+8 Stunden Rast" oder "-1 Tag" zum Zurückspulen).
    ///     Löst wie ein normaler Tick das Tick-Event aus, damit Timer und Wecker
    ///     synchron mitgehen.
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