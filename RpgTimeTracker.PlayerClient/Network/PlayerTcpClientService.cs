using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Network;
using RpgTimeTracker.Shared.Services.Rpc;
using Serilog;

namespace RpgTimeTracker.PlayerClient.Network;

/// <summary>
///     Client für das JSON-RPC-Delta-Protokoll (siehe RpgTimeTracker.Shared.Models.Rpc/RpgTimeTracker.Shared.Services.Rpc): empfängt einen
///     vollen session.snapshot beim Connect, danach nur noch gezielte Events. Medien kommen
///     gechunkt (media.begin + N Binär-Frames) und werden progressiv in eine Tempdatei
///     geschrieben, damit die Wiedergabe starten kann, bevor die Übertragung fertig ist.
/// </summary>
public sealed class PlayerTcpClientService : IDisposable
{
    private const int MaxRpcPayloadBytes = 256 * 1024;

    // Gemeinsam mit dem Host (siehe RpgTimeTracker.Shared.Models.Network.MediaLimits) - unterschiedliche
    // Werte hier und im Host würden dazu führen, dass ein vom Host akzeptiertes/verteiltes
    // größeres Medium hier kommentarlos verworfen wird, ohne dass irgendwo ein Fehler auftaucht.
    // Passt komfortabel in int (Array-/NetworkFrame-Längen sind ohnehin int-begrenzt).
    private const int MaxMediaBytes = (int)MediaLimits.MaxMediaBytes;

    // Server sendet mindestens alle TcpPlayerServerService.HeartbeatInterval (5s) etwas.
    // 15s Idle-Toleranz gibt Puffer für Jitter, ohne einen echten Abbruch spät zu erkennen.
    // TcpClient.ReceiveTimeout greift NICHT bei ReadAsync, deshalb dieser eigene Watchdog -
    // ohne ihn merkt der Client ein still gekapptes Kabel/WLAN nie und bleibt "verbunden"
    // stehen, obwohl nichts mehr ankommt (UI wirkt dadurch eingefroren).
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    // Automatischer Reconnect nach einer unerwarteten Trennung (Idle-Timeout, Netzwerkfehler,
    // Host-Neustart) - wächst pro Fehlschlag, damit ein dauerhaft nicht erreichbarer Host nicht
    // im Sekundentakt angeklopft wird, aber ein kurzer Aussetzer (WLAN-Hänger, Host-Neustart)
    // trotzdem zügig überbrückt wird. Nur EIN manueller Verbindungsabbruch (Disconnect()) stoppt
    // den Reconnect-Versuch endgültig - siehe _userDisconnectRequested.
    private static readonly TimeSpan ReconnectInitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;

    private CancellationTokenSource? _cts;
    private DateTime _lastActivityUtc;
    private string? _lastHost;
    private string _lastPin = string.Empty;
    private int _lastPort;
    private long _mediaBytesWritten;

    // Zustand des gerade laufenden Medien-Streams (nur ein Medium gleichzeitig pro Verbindung).
    private FileStream? _mediaFile;
    private MediaHeaderDto? _mediaHeader;
    private string? _mediaTempPath;
    private CancellationTokenSource? _reconnectCts;
    private volatile bool _userDisconnectRequested;
    private NetworkStream? _writeStream;

    public void Dispose()
    {
        Disconnect();
        _writeLock.Dispose();
    }

    public event Action<SessionSnapshotParams>? SessionSnapshotReceived;
    public event Action? ClockStarted;
    public event Action? ClockStopped;
    public event Action<double>? ClockSpeedChanged;
    public event Action<DateTime>? ClockTimeJumped;
    public event Action<string, string>? HeaderChanged;
    public event Action<string>? ThemeChanged;
    public event Action<TimelineItemSnapshotDto>? TimelineItemUpserted;
    public event Action<Guid>? TimelineItemRemoved;
    public event Action<bool>? DisplayFullscreenRequested;

    /// <summary>Periodisches Lebenszeichen inkl. Uhrzustand - für Drift-Korrektur der lokalen Uhr.</summary>
    public event Action<DateTime, double, bool>? ClockHeartbeatReceived;

    /// <summary>Ein Medium beginnt: Kind/Dateiname/MIME + Pfad einer (noch wachsenden) Tempdatei.</summary>
    public event Action<MediaHeaderDto, string>? MediaBeginReceived;

    /// <summary>Übertragung vollständig - relevant für Bilder (die erst jetzt dekodiert werden können).</summary>
    public event Action<MediaHeaderDto, string>? MediaCompleted;

    public event Action? MediaCleared;

    /// <summary>SL beendet einen einzelnen, gerade laufenden Sound gezielt (per MediaId).</summary>
    public event Action<string>? SoundStopRequested;

    /// <summary>SL passt die Lautstärke eines gerade laufenden Sounds live an (MediaId, 0-100).</summary>
    public event Action<string, int>? SoundVolumeChangeRequested;

    /// <summary>SL entfernt ein Bild/Video gezielt aus der Galerie (per MediaId).</summary>
    public event Action<string>? MediaRetractRequested;

    /// <summary>SL hebt ein Galerie-Element hervor (per MediaId) - ein Sprung-Vorschlag, keine Sperre.</summary>
    public event Action<string>? MediaHighlightRequested;

    /// <summary>Automatische Weiterschalt-Zeit pro Bild (Sekunden, 0 = manuell).</summary>
    public event Action<double>? SlideshowIntervalChanged;

    public event Action<string>? StatusChanged;

    public async Task ConnectAsync(string host, int port, string pin = "")
    {
        Disconnect();
        // Disconnect() setzt _userDisconnectRequested=true als Nebeneffekt (siehe dort) - das
        // gilt hier nicht, da gerade aktiv ein NEUER Verbindungsversuch beginnt (egal ob manuell
        // oder aus ReconnectLoopAsync heraus).
        _userDisconnectRequested = false;
        _lastHost = host;
        _lastPort = port;
        _lastPin = pin;

        _cts = new CancellationTokenSource();
        _client = new TcpClient
        {
            ReceiveTimeout = 15000,
            SendTimeout = 5000,
            NoDelay = true
        };

        StatusChanged?.Invoke($"Verbinde zu {host}:{port} ...");
        Log.Information("Verbindungsversuch zu {Host}:{Port}", host, port);

        // Ein falscher/unerreichbarer manueller Host kann sonst minutenlang ohne jede
        // Rückmeldung hängen (TCP-SYN-Timeout des Betriebssystems), bevor ConnectAsync
        // überhaupt fehlschlägt - das wirkt wie ein UI, das auf Eingaben nicht reagiert.
        using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _client.ConnectAsync(host, port, connectTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Log.Warning("Verbindung zu {Host}:{Port} nach 5s Timeout abgebrochen", host, port);
            throw new TimeoutException($"Keine Antwort von {host}:{port} innerhalb von 5 Sekunden.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Verbindung zu {Host}:{Port} fehlgeschlagen", host, port);
            throw;
        }

        StatusChanged?.Invoke($"Verbunden mit {host}:{port}");
        Log.Information("Verbunden mit {Host}:{Port}", host, port);

        var stream = _client.GetStream();
        _writeStream = stream;
        _ = Task.Run(() => ReadLoopAsync(stream, _cts.Token));

        // Muss vor dem session.snapshot beim Host ankommen (siehe
        // TcpPlayerServerService.PerformHandshakeAsync) - ohne dieses Signal (oder bei
        // falschem PIN/inkompatibler Version) lehnt der Host die Verbindung per
        // session.helloRejected ab und trennt sie wieder.
        await SendRpcAsync(RpcMethods.SessionHello,
            new SessionHelloParams { ProtocolVersion = ProtocolInfo.Version, Pin = pin }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Manuelles/endgültiges Trennen: markiert die Trennung als gewollt, damit die laufende
    ///     ReadLoopAsync (falls gerade aktiv) danach KEINEN automatischen Reconnect anstößt.
    /// </summary>
    public void Disconnect()
    {
        _userDisconnectRequested = true;
        CancelReconnect();

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
        }

        _cts?.Dispose();
        _cts = null;
        _client = null;
        _writeStream = null;
        CleanupMediaStream();
    }

    private void CancelReconnect()
    {
        try
        {
            _reconnectCts?.Cancel();
        }
        catch
        {
        }

        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    /// <summary>
    ///     Meldet dem Host, dass ein Video tatsächlich zu spielen begonnen hat, inkl. der lokal (LibVLC) ermittelten
    ///     Dauer.
    /// </summary>
    public Task SendMediaPlaybackStartedAsync(string mediaId, TimeSpan duration)
    {
        return SendRpcAsync(RpcMethods.MediaPlaybackStarted,
            new MediaPlaybackStartedParams { MediaId = mediaId, DurationMs = (long)duration.TotalMilliseconds });
    }

    /// <summary>Meldet dem Host, dass ein nicht-loopendes Video zu Ende ist (Host pausiert Uhrzeit fort/schließt das Medium).</summary>
    public Task SendMediaPlaybackEndedAsync(string mediaId)
    {
        return SendRpcAsync(RpcMethods.MediaPlaybackEnded, new MediaPlaybackEndedParams { MediaId = mediaId });
    }

    private async Task SendRpcAsync<TParams>(string method, TParams @params)
    {
        var stream = _writeStream;
        if (stream is null) return;

        byte[] payload;
        try
        {
            payload = RpcMessage.Serialize(method, @params);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RPC-Notification {Method} konnte nicht serialisiert werden", method);
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await NetworkFrame.WriteAsync(stream, NetworkFrame.TypeRpc, payload).ConfigureAwait(false);
            Log.Debug("RPC-Notification {Method} an Host gesendet", method);
        }
        catch (Exception ex)
        {
            // Verbindung tot - der ReadLoop erkennt das ohnehin selbst und räumt auf.
            Log.Debug(ex, "RPC-Notification {Method} konnte nicht gesendet werden (Verbindung vermutlich getrennt)",
                method);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        _lastActivityUtc = DateTime.UtcNow;
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watchdogTask = IdleWatchdogAsync(watchdogCts);

        try
        {
            while (!watchdogCts.IsCancellationRequested)
            {
                var maxLength = Math.Max(MaxRpcPayloadBytes, MaxMediaBytes);
                var frame = await NetworkFrame.ReadAsync(stream, maxLength, watchdogCts.Token, MarkActivity)
                    .ConfigureAwait(false);
                if (frame is null) break;
                MarkActivity();

                var (type, payload) = frame.Value;
                switch (type)
                {
                    case NetworkFrame.TypeRpc:
                        HandleRpcFrame(payload);
                        break;
                    case NetworkFrame.TypeMediaChunk:
                        HandleMediaChunk(payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect or idle-watchdog timeout
            Log.Information("Verbindung beendet (getrennt oder Idle-Timeout)");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Verbindung mit Fehler beendet");
            StatusChanged?.Invoke($"Verbindung beendet: {ex.Message}");
        }
        finally
        {
            watchdogCts.Cancel();
            await watchdogTask.ConfigureAwait(false);
            CleanupMediaStream();

            // Nur bei einer UNERWARTETEN Trennung automatisch neu verbinden - ein manuelles
            // Disconnect() (siehe dort) setzt _userDisconnectRequested vorher, damit diese
            // Verbindung endgültig getrennt bleibt, statt sich selbst wieder aufzubauen.
            if (!_userDisconnectRequested && _lastHost is not null)
                _ = ReconnectLoopAsync(_lastHost, _lastPort);
            else
                StatusChanged?.Invoke("Nicht verbunden");
        }
    }

    /// <summary>
    ///     Versucht in wachsenden Abständen (2s bis max. 20s), dieselbe Verbindung wiederherzustellen,
    ///     bis es klappt oder der Nutzer selbst trennt/eine andere Verbindung startet. Der neu
    ///     verbindende Client bekommt beim erfolgreichen Reconnect automatisch einen vollen
    ///     session.snapshot vom Host (siehe TcpPlayerServerService.SendCatchUpAsync) - kein
    ///     gesonderter Resync-Pfad hier nötig.
    /// </summary>
    private async Task ReconnectLoopAsync(string host, int port)
    {
        CancelReconnect();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;
        var delay = ReconnectInitialDelay;

        while (!token.IsCancellationRequested && !_userDisconnectRequested)
        {
            StatusChanged?.Invoke($"Verbindung verloren - erneuter Versuch in {delay.TotalSeconds:0}s ...");
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_userDisconnectRequested || token.IsCancellationRequested) return;

            try
            {
                Log.Information("Automatischer Reconnect-Versuch zu {Host}:{Port}", host, port);
                await ConnectAsync(host, port, _lastPin).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Reconnect-Versuch zu {Host}:{Port} fehlgeschlagen", host, port);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, ReconnectMaxDelay.TotalSeconds));
            }
        }
    }

    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
    }

    private async Task IdleWatchdogAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                if (DateTime.UtcNow - _lastActivityUtc > IdleTimeout)
                {
                    Log.Warning("Idle-Timeout ({Timeout}) erreicht - keine Aktivität vom Host, Verbindung gilt als tot",
                        IdleTimeout);
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal on disconnect
        }
    }

    private void HandleRpcFrame(byte[] payload)
    {
        if (payload.Length > MaxRpcPayloadBytes)
        {
            Log.Warning("Eingehendes RPC-Frame ({Size} Bytes) überschreitet Limit ({Max} Bytes) - wird ignoriert",
                payload.Length, MaxRpcPayloadBytes);
            return;
        }

        var raw = RpcMessage.TryParseRaw(payload);
        if (raw is null)
        {
            Log.Warning("Eingehendes RPC-Frame konnte nicht als JSON-RPC geparst werden ({Size} Bytes)",
                payload.Length);
            return;
        }

        try
        {
            switch (raw.Method)
            {
                case RpcMethods.SessionHelloRejected:
                    var rejected = raw.GetParams<SessionHelloRejectedParams>();
                    var reason = rejected?.Reason ?? "Verbindung abgelehnt.";
                    Log.Warning("Verbindung vom Host abgelehnt: {Reason}", reason);
                    StatusChanged?.Invoke($"Verbindung abgelehnt: {reason}");
                    // Kein Reconnect-Versuch mit denselben (offenbar falschen) Zugangsdaten -
                    // Disconnect() setzt _userDisconnectRequested=true, siehe ReadLoopAsync.
                    Disconnect();
                    break;
                case RpcMethods.SessionSnapshot:
                    var snapshot = raw.GetParams<SessionSnapshotParams>();
                    if (snapshot is not null)
                    {
                        Log.Information("session.snapshot empfangen ({ItemCount} Items)", snapshot.Items.Count);
                        SessionSnapshotReceived?.Invoke(snapshot);
                    }

                    break;
                case RpcMethods.ClockStarted:
                    ClockStarted?.Invoke();
                    break;
                case RpcMethods.ClockStopped:
                    ClockStopped?.Invoke();
                    break;
                case RpcMethods.ClockSpeedChanged:
                    var speed = raw.GetParams<ClockSpeedChangedParams>();
                    if (speed is not null) ClockSpeedChanged?.Invoke(speed.SpeedMultiplier);
                    break;
                case RpcMethods.ClockTimeJumped:
                    var jumped = raw.GetParams<ClockTimeJumpedParams>();
                    if (jumped is not null) ClockTimeJumped?.Invoke(jumped.NewGameTime);
                    break;
                case RpcMethods.HeaderChanged:
                    var header = raw.GetParams<HeaderChangedParams>();
                    if (header is not null) HeaderChanged?.Invoke(header.Title, header.Subtitle);
                    break;
                case RpcMethods.ThemeChanged:
                    var theme = raw.GetParams<ThemeChangedParams>();
                    if (theme is not null) ThemeChanged?.Invoke(theme.Theme);
                    break;
                case RpcMethods.TimelineItemUpserted:
                    var item = raw.GetParams<TimelineItemSnapshotDto>();
                    if (item is not null) TimelineItemUpserted?.Invoke(item);
                    break;
                case RpcMethods.TimelineItemRemoved:
                    var removed = raw.GetParams<TimelineItemRemovedParams>();
                    if (removed is not null && Guid.TryParse(removed.Id, out var removedId))
                        TimelineItemRemoved?.Invoke(removedId);
                    break;
                case RpcMethods.DisplayFullscreen:
                    var fullscreen = raw.GetParams<DisplayFullscreenParams>();
                    if (fullscreen is not null) DisplayFullscreenRequested?.Invoke(fullscreen.Fullscreen);
                    break;
                case RpcMethods.MediaBegin:
                    var mediaHeader = raw.GetParams<MediaHeaderDto>();
                    if (mediaHeader is not null) BeginMediaStream(mediaHeader);
                    break;
                case RpcMethods.MediaCleared:
                    Log.Information("media.cleared empfangen");
                    CleanupMediaStream();
                    MediaCleared?.Invoke();
                    break;
                case RpcMethods.SessionHeartbeat:
                    var heartbeat = raw.GetParams<ClockHeartbeatParams>();
                    if (heartbeat is not null)
                        ClockHeartbeatReceived?.Invoke(heartbeat.CurrentGameTime, heartbeat.SpeedMultiplier,
                            heartbeat.IsClockRunning);
                    break;
                case RpcMethods.MediaStopSound:
                    var stopSound = raw.GetParams<MediaStopSoundParams>();
                    if (stopSound is not null) SoundStopRequested?.Invoke(stopSound.MediaId);
                    break;
                case RpcMethods.MediaSetVolume:
                    var setVolume = raw.GetParams<MediaSetVolumeParams>();
                    if (setVolume is not null) SoundVolumeChangeRequested?.Invoke(setVolume.MediaId, setVolume.Volume);
                    break;
                case RpcMethods.MediaRetract:
                    var retract = raw.GetParams<MediaRetractParams>();
                    if (retract is not null) MediaRetractRequested?.Invoke(retract.MediaId);
                    break;
                case RpcMethods.MediaHighlight:
                    var highlight = raw.GetParams<MediaHighlightParams>();
                    if (highlight is not null) MediaHighlightRequested?.Invoke(highlight.MediaId);
                    break;
                case RpcMethods.MediaSlideshowInterval:
                    var slideshow = raw.GetParams<MediaSlideshowIntervalParams>();
                    if (slideshow is not null) SlideshowIntervalChanged?.Invoke(slideshow.Seconds);
                    break;
                default:
                    Log.Debug("Unbekannte eingehende RPC-Methode {Method} ignoriert", raw.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Rogue or buggy server: ignore malformed notification, do not crash.
            Log.Warning(ex, "Fehlerhafte eingehende RPC-Notification {Method} ignoriert", raw.Method);
        }
    }

    private void BeginMediaStream(MediaHeaderDto header)
    {
        CleanupMediaStream();

        if (header.TotalLength < 0 || header.TotalLength > MaxMediaBytes)
        {
            Log.Warning("media.begin für {FileName} verworfen: TotalLength {TotalLength} ungültig/über Limit ({Max})",
                header.FileName, header.TotalLength, MaxMediaBytes);
            return;
        }

        try
        {
            var extension = Path.GetExtension(header.FileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = header.Kind switch
                {
                    MediaHeaderDto.MediaKindVideo => ".mp4",
                    MediaHeaderDto.MediaKindAudio => ".mp3",
                    _ => ".img"
                };

            _mediaTempPath = Path.Combine(Path.GetTempPath(), $"rpgtimetracker-media-{Guid.NewGuid():N}{extension}");
            _mediaFile = new FileStream(_mediaTempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _mediaHeader = header;
            _mediaBytesWritten = 0;

            Log.Information("media.begin: {FileName} ({Kind}, {SizeKb} KB, Loop={Loop}) -> {TempPath}",
                header.FileName, header.Kind, header.TotalLength / 1024, header.Loop, _mediaTempPath);

            // Für Videos kann die Wiedergabe sofort starten (VLC verträgt eine wachsende
            // Datei); für Bilder wartet der Aufrufer auf MediaCompleted, da ein Teilbild
            // nicht dekodierbar ist.
            MediaBeginReceived?.Invoke(header, _mediaTempPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "media.begin für {FileName} konnte nicht verarbeitet werden (Tempdatei)", header.FileName);
            CleanupMediaStream();
        }
    }

    private void HandleMediaChunk(byte[] payload)
    {
        if (_mediaFile is null || _mediaHeader is null) return;

        try
        {
            _mediaFile.Write(payload, 0, payload.Length);
            _mediaFile.Flush();
            _mediaBytesWritten += payload.Length;

            if (_mediaBytesWritten >= _mediaHeader.TotalLength)
            {
                var header = _mediaHeader;
                var path = _mediaTempPath!;
                _mediaFile.Dispose();
                _mediaFile = null;
                Log.Information("Medium {FileName} vollständig empfangen ({Bytes} Bytes)", header.FileName,
                    _mediaBytesWritten);
                MediaCompleted?.Invoke(header, path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medien-Chunk für {FileName} konnte nicht geschrieben werden", _mediaHeader?.FileName);
            CleanupMediaStream();
        }
    }

    private void CleanupMediaStream()
    {
        try
        {
            _mediaFile?.Dispose();
        }
        catch
        {
        }

        _mediaFile = null;
        _mediaHeader = null;
        _mediaBytesWritten = 0;
        _mediaTempPath = null;
    }
}