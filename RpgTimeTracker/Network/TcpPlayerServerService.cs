using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Network;
using RpgTimeTracker.Shared.Services.Rpc;
using Serilog;

namespace RpgTimeTracker.Network;

/// <summary>
///     Anzeige-Info für einen verbundenen Client - für die SL-seitige "Verbundene Clients"-Liste inkl. manuellem
///     Trennen.
/// </summary>
public sealed record ConnectedClientInfo(string RemoteEndpoint, DateTime ConnectedAtUtc);

/// <summary>
///     Read-only TCP publisher für den Spieler-Client. Sendet ausschließlich JSON-RPC-
///     Notifications (siehe RpgTimeTracker.Shared.Models.Rpc/RpgTimeTracker.Shared.Services.Rpc): ein Vollzustand einmalig beim Connect
///     (session.snapshot), danach nur noch gezielte Delta-Events bei tatsächlichen Änderungen
///     (Start/Stop/Speed/Sprung/Item-Änderung) statt eines periodisch gepushten Gesamtzustands.
///     Medien werden gechunkt gestreamt: media.begin (Metadaten) gefolgt von N rohen Binär-Frames.
/// </summary>
public sealed class TcpPlayerServerService : IDisposable
{
    public const int DefaultPort = 48550;

    /// <summary>
    ///     Sicherheitsgrenze für ein einzelnes Bild/Video - siehe RpgTimeTracker.Shared.Models.Network.MediaLimits (gemeinsam
    ///     mit dem Client, damit beide Seiten dasselbe Limit kennen).
    /// </summary>
    public const long MaxMediaBytes = MediaLimits.MaxMediaBytes;

    /// <summary>Chunk-Größe beim Medien-Streaming: klein genug, damit State-RPCs zwischendurch nicht lange warten.</summary>
    private const int MediaChunkSize = 64 * 1024;

    /// <summary>
    ///     Der Client sendet nur winzige Playback-Status-Notifications zurück - großzügig genug, aber klein genug, um
    ///     einen kaputten/böswilligen Client zu begrenzen.
    /// </summary>
    private const int MaxInboundRpcPayloadBytes = 4 * 1024;

    /// <summary>
    ///     Wie oft ein Lebenszeichen gesendet wird, auch ohne inhaltliche Änderung. Der Client
    ///     nutzt das als Idle-Watchdog-Signal: TcpClient.ReceiveTimeout greift nicht bei
    ///     asynchronen Reads, ohne Heartbeat würde ein still abgebrochenes Kabel/WLAN nie erkannt.
    ///     Der Heartbeat trägt zusätzlich den aktuellen Uhrzustand, damit die lokal abgeleitete
    ///     Client-Uhr periodisch gegen Drift korrigiert wird.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly List<ClientConnection> _clients = [];
    private readonly Func<ClockHeartbeatParams> _clockStateProvider;
    private readonly object _gate = new();
    private readonly object _mediaGate = new();

    /// <summary>
    ///     Serialisiert komplette Medien-Übertragungen (Header + alle Chunks): das Chunk-Frame
    ///     trägt keine MediaId (siehe NetworkFrame.TypeMediaChunk), daher würden zwei gleichzeitig
    ///     gesendete Medien (z.B. zwei schnell hintereinander ausgelöste Sounds) ihre Chunks auf derselben
    ///     TCP-Verbindung sonst vermischen. Wartende Übertragungen laufen einfach nacheinander - die
    ///     bereits VOLLSTÄNDIG empfangene Wiedergabe eines vorherigen Sounds läuft davon unabhängig weiter.
    /// </summary>
    private readonly SemaphoreSlim _mediaSendLock = new(1, 1);

    private readonly Func<SessionSnapshotParams> _snapshotProvider;
    private readonly Func<string?> _connectionPinProvider;
    private Task? _acceptTask;
    private PlayerMdnsAnnouncer? _announcer;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private LanDiscoveryResponder? _lanDiscoveryResponder;

    /// <summary>
    ///     Zuletzt gesendetes Bild/Video (Header + komplette Datei), für neu verbindende Clients.
    ///     Sounds tragen hier BEWUSST nichts ein - sie sind kein "aktuell angezeigtes" Medium, auf das
    ///     ein Nachzügler warten müsste (siehe PublishMediaAsync).
    /// </summary>
    private (MediaHeaderDto Header, byte[] FileBytes)? _lastMedia;

    private TcpListener? _listener;

    public TcpPlayerServerService(Func<SessionSnapshotParams> snapshotProvider,
        Func<ClockHeartbeatParams> clockStateProvider,
        Func<string?>? connectionPinProvider = null)
    {
        _snapshotProvider = snapshotProvider;
        _clockStateProvider = clockStateProvider;
        _connectionPinProvider = connectionPinProvider ?? (() => null);
    }

    /// <summary>Wie lange auf das erste session.hello eines neu verbundenen Clients gewartet wird, bevor die Verbindung als tot gilt.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    public int Port { get; private set; } = DefaultPort;
    public bool IsRunning { get; private set; }

    public void Dispose()
    {
        Stop();
        _mediaSendLock.Dispose();
    }

    /// <summary>Feuert bei jeder Änderung der verbundenen Client-Anzahl (Hintergrund-Thread, Aufrufer muss ggf. dispatchen).</summary>
    public event Action<int>? ClientCountChanged;

    /// <summary>
    ///     Feuert mit der vollständigen Liste bei jeder Änderung der verbundenen Clients (Hintergrund-Thread, Aufrufer
    ///     muss ggf. dispatchen).
    /// </summary>
    public event Action<IReadOnlyList<ConnectedClientInfo>>? ClientsChanged;

    /// <summary>Ein Client meldet, dass ein Video tatsächlich zu spielen begonnen hat (Hintergrund-Thread).</summary>
    public event Action<string, long>? ClientReportedPlaybackStarted;

    /// <summary>Ein Client meldet, dass ein nicht-loopendes Video bei ihm zu Ende ist (Hintergrund-Thread).</summary>
    public event Action<string>? ClientReportedPlaybackEnded;

    public void Start(int port = DefaultPort, string serverName = "RpgTimeTracker")
    {
        if (IsRunning) return;

        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start(8);
        IsRunning = true;

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

        _announcer = new PlayerMdnsAnnouncer(Port, serverName);
        _announcer.Start();

        _lanDiscoveryResponder = new LanDiscoveryResponder(Port, serverName);
        _lanDiscoveryResponder.Start();

        Log.Information("TCP-Spielerserver gestartet auf Port {Port} (Servername {ServerName})", Port, serverName);
    }

    // ==================== Granulare State-Events ====================

    public Task PublishClockStartedAsync()
    {
        return BroadcastRpcAsync(RpcMethods.ClockStarted, RpcEmptyParams.Instance);
    }

    public Task PublishClockStoppedAsync()
    {
        return BroadcastRpcAsync(RpcMethods.ClockStopped, RpcEmptyParams.Instance);
    }

    public Task PublishClockSpeedChangedAsync(double speedMultiplier)
    {
        return BroadcastRpcAsync(RpcMethods.ClockSpeedChanged,
            new ClockSpeedChangedParams { SpeedMultiplier = speedMultiplier });
    }

    public Task PublishClockTimeJumpedAsync(DateTime newGameTime)
    {
        return BroadcastRpcAsync(RpcMethods.ClockTimeJumped, new ClockTimeJumpedParams { NewGameTime = newGameTime });
    }

    public Task PublishHeaderChangedAsync(string title, string subtitle)
    {
        return BroadcastRpcAsync(RpcMethods.HeaderChanged,
            new HeaderChangedParams { Title = title, Subtitle = subtitle });
    }

    public Task PublishThemeChangedAsync(string theme)
    {
        return BroadcastRpcAsync(RpcMethods.ThemeChanged, new ThemeChangedParams { Theme = theme });
    }

    public Task PublishTimelineItemUpsertedAsync(TimelineItemSnapshotDto item)
    {
        return BroadcastRpcAsync(RpcMethods.TimelineItemUpserted, item);
    }

    public Task PublishTimelineItemRemovedAsync(Guid id)
    {
        return BroadcastRpcAsync(RpcMethods.TimelineItemRemoved, new TimelineItemRemovedParams { Id = id.ToString() });
    }

    public Task PublishDisplayFullscreenAsync(bool fullscreen)
    {
        return BroadcastRpcAsync(RpcMethods.DisplayFullscreen, new DisplayFullscreenParams { Fullscreen = fullscreen });
    }

    /// <summary>
    ///     Sendet einen kompletten Zustands-Resync an ALLE verbundenen Clients (nicht nur neu
    ///     verbindende) - für Sonderfälle, in denen der Zustand komplett ersetzt wird (z.B. Laden
    ///     eines Spielstands), wo einzelne Deltas unpraktikabel wären.
    /// </summary>
    public Task PublishFullResyncAsync(SessionSnapshotParams snapshot)
    {
        return BroadcastRpcAsync(RpcMethods.SessionSnapshot, snapshot);
    }

    /// <summary>
    ///     Verteilt ein Bild, Video oder einen Sound gechunkt (media.begin + N Binär-Chunks).
    ///     Bild/Video werden zusätzlich für Nachzügler gemerkt (siehe _lastMedia), Sounds nicht.
    /// </summary>
    public async Task PublishMediaAsync(MediaHeaderDto header, byte[] fileBytes)
    {
        if (!IsRunning) return;
        if (fileBytes.Length > MaxMediaBytes)
        {
            Log.Warning("Medium {FileName} verworfen: {SizeMb} MB überschreitet das Limit von {MaxMb} MB",
                header.FileName, fileBytes.Length / (1024 * 1024), MaxMediaBytes / (1024 * 1024));
            return;
        }

        header.TotalLength = fileBytes.Length;

        if (header.Kind != MediaHeaderDto.MediaKindAudio)
            lock (_mediaGate)
            {
                _lastMedia = (header, fileBytes);
            }

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
        }

        Log.Information("Sende Medium {FileName} ({Kind}, {SizeKb} KB, Loop={Loop}) an {ClientCount} Client(s)",
            header.FileName, header.Kind, fileBytes.Length / 1024, header.Loop, clients.Length);

        // Ganze Übertragung (Header + alle Chunks) serialisiert - siehe _mediaSendLock-Kommentar.
        await _mediaSendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in clients)
                await SendMediaToClientAsync(client, header, fileBytes).ConfigureAwait(false);
        }
        finally
        {
            _mediaSendLock.Release();
        }
    }

    public Task PublishMediaClearAsync()
    {
        lock (_mediaGate)
        {
            _lastMedia = null;
        }

        Log.Debug("Medium zurückgesetzt (media.cleared an alle Clients)");
        return BroadcastRpcAsync(RpcMethods.MediaCleared, RpcEmptyParams.Instance);
    }

    /// <summary>
    ///     Beendet einen einzelnen, gerade laufenden Sound bei allen Clients (per MediaId) -
    ///     unabhängig vom Bild/Video-"aktuelles Medium"-Slot, siehe MediaKindAudio.
    /// </summary>
    public Task PublishStopSoundAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaStopSound, new MediaStopSoundParams { MediaId = mediaId });
    }

    /// <summary>Passt die Lautstärke eines gerade laufenden Sounds bei allen Clients live an (0-100).</summary>
    public Task PublishSetSoundVolumeAsync(string mediaId, int volume)
    {
        return BroadcastRpcAsync(RpcMethods.MediaSetVolume,
            new MediaSetVolumeParams { MediaId = mediaId, Volume = volume });
    }

    /// <summary>Entfernt ein Bild/Video gezielt aus der Galerie bei allen Clients (per MediaId).</summary>
    public Task PublishRetractAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaRetract, new MediaRetractParams { MediaId = mediaId });
    }

    /// <summary>SL "hebt hervor": alle Clients springen (lokal, nicht sperrend) auf dieses Galerie-Element.</summary>
    public Task PublishHighlightAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaHighlight, new MediaHighlightParams { MediaId = mediaId });
    }

    /// <summary>Setzt die automatische Weiterschalt-Zeit pro Bild bei allen Clients (Sekunden, 0 = manuell).</summary>
    public Task PublishSlideshowIntervalAsync(double seconds)
    {
        return BroadcastRpcAsync(RpcMethods.MediaSlideshowInterval,
            new MediaSlideshowIntervalParams { Seconds = seconds });
    }

    private async Task SendMediaToClientAsync(ClientConnection client, MediaHeaderDto header, byte[] fileBytes)
    {
        var beginPayload = RpcMessage.Serialize(RpcMethods.MediaBegin, header);
        if (!await client.TryWriteFrameAsync(NetworkFrame.TypeRpc, beginPayload).ConfigureAwait(false))
        {
            Log.Warning("media.begin an {Client} fehlgeschlagen - Verbindung wird getrennt", client);
            RemoveClient(client);
            return;
        }

        for (var offset = 0; offset < fileBytes.Length; offset += MediaChunkSize)
        {
            var length = Math.Min(MediaChunkSize, fileBytes.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(fileBytes, offset, chunk, 0, length);

            if (!await client.TryWriteFrameAsync(NetworkFrame.TypeMediaChunk, chunk).ConfigureAwait(false))
            {
                Log.Warning(
                    "Medien-Chunk an {Client} bei Offset {Offset}/{Total} fehlgeschlagen - Verbindung wird getrennt",
                    client, offset, fileBytes.Length);
                RemoveClient(client);
                return;
            }
        }

        Log.Debug("Medium {FileName} vollständig an {Client} gesendet", header.FileName, client);
    }

    private async Task BroadcastRpcAsync<TParams>(string method, TParams @params)
    {
        if (!IsRunning) return;

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

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
        }

        foreach (var client in clients)
            if (!await client.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload, true).ConfigureAwait(false))
                RemoveClient(client);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient? tcpClient = null;
            try
            {
                tcpClient = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                tcpClient.NoDelay = true;
                tcpClient.ReceiveTimeout = 5000;
                tcpClient.SendTimeout = 5000;

                var connection = new ClientConnection(tcpClient);

                // Verbindung wird ERST nach erfolgreichem session.hello (siehe
                // PerformHandshakeAsync) zu _clients hinzugefügt bzw. bekommt ihren
                // session.snapshot - vorher ist sie "unauthentifiziert" und für den Rest der
                // App unsichtbar (kein Eintrag in der Client-Liste, kein Broadcast-Ziel).
                _ = connection.ReadLoopAsync(token, payload => HandleClientRpcFrame(connection, payload))
                    .ContinueWith(_ =>
                    {
                        // Nur eine "echte" Trennung melden, wenn die Verbindung überhaupt
                        // akzeptiert wurde - eine im Handshake abgelehnte Verbindung wurde nie
                        // zu _clients hinzugefügt und braucht keine Trennungs-Meldung/-Aufräumen.
                        if (connection.IsAccepted) RemoveClient(connection);
                        else connection.Dispose();
                    }, TaskScheduler.Default);

                _ = PerformHandshakeAsync(connection, token);
            }
            catch (OperationCanceledException)
            {
                tcpClient?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Annehmen einer eingehenden Verbindung");
                tcpClient?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Wartet auf das erste session.hello einer neu angenommenen Verbindung (siehe
    ///     AcceptLoopAsync) und entscheidet danach: kein Hello innerhalb von HandshakeTimeout,
    ///     falscher PIN oder abweichende Protokoll-Version -> session.helloRejected + trennen;
    ///     sonst wird die Verbindung erst jetzt zu _clients hinzugefügt und bekommt ihren
    ///     session.snapshot. Absichtlich VOR dem Hinzufügen zu _clients geprüft, damit ein
    ///     abgelehnter Client nie in der SL-seitigen "Verbundene Clients"-Liste auftaucht oder
    ///     Broadcasts empfängt.
    /// </summary>
    private async Task PerformHandshakeAsync(ClientConnection connection, CancellationToken token)
    {
        SessionHelloParams hello;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(HandshakeTimeout);
            hello = await connection.HelloTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) return;
            await RejectAndCloseAsync(connection,
                "Kein Hallo-Signal vom Client erhalten (veraltete Client-Version?).").ConfigureAwait(false);
            return;
        }

        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            await RejectAndCloseAsync(connection,
                    $"Protokoll-Version nicht kompatibel (Host: {ProtocolInfo.Version}, Client: {hello.ProtocolVersion}). Bitte Host und Client auf dieselbe Version aktualisieren.")
                .ConfigureAwait(false);
            return;
        }

        var requiredPin = _connectionPinProvider();
        if (!string.IsNullOrEmpty(requiredPin) && !string.Equals(hello.Pin, requiredPin, StringComparison.Ordinal))
        {
            await RejectAndCloseAsync(connection, "Falscher PIN.").ConfigureAwait(false);
            return;
        }

        connection.IsAccepted = true;
        lock (_gate)
        {
            _clients.Add(connection);
        }

        NotifyClientsChanged();
        Log.Information("Client verbunden: {Client}", connection);

        // _snapshotProvider()/das Nachschicken des Mediums lesen UI-gebundene Daten,
        // daher auf den UI-Thread posten statt direkt von diesem Hintergrund-Loop aus.
        Dispatcher.UIThread.Post(() => _ = SendCatchUpAsync(connection));
    }

    private async Task RejectAndCloseAsync(ClientConnection connection, string reason)
    {
        Log.Information("Verbindung von {Client} abgelehnt: {Reason}", connection, reason);
        try
        {
            var payload = RpcMessage.Serialize(RpcMethods.SessionHelloRejected,
                new SessionHelloRejectedParams { Reason = reason });
            await connection.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "session.helloRejected an {Client} konnte nicht gesendet werden", connection);
        }

        connection.Dispose();
    }

    private async Task SendCatchUpAsync(ClientConnection connection)
    {
        SessionSnapshotParams snapshot;
        try
        {
            snapshot = _snapshotProvider();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "session.snapshot konnte für {Client} nicht erstellt werden", connection);
            return;
        }

        byte[] payload;
        try
        {
            payload = RpcMessage.Serialize(RpcMethods.SessionSnapshot, snapshot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "session.snapshot konnte für {Client} nicht serialisiert werden", connection);
            return;
        }

        if (!await connection.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false))
        {
            Log.Warning("session.snapshot an neu verbundenen {Client} fehlgeschlagen - Verbindung wird getrennt",
                connection);
            RemoveClient(connection);
            return;
        }

        Log.Debug("session.snapshot an {Client} gesendet ({ItemCount} Items)", connection, snapshot.Items.Count);

        (MediaHeaderDto Header, byte[] FileBytes)? cachedMedia;
        lock (_mediaGate)
        {
            cachedMedia = _lastMedia;
        }

        if (cachedMedia is not null)
            await SendMediaToClientAsync(connection, cachedMedia.Value.Header, cachedMedia.Value.FileBytes)
                .ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);

                // _clockStateProvider liest UI-gebundene Felder (_clock, SpeedMultiplier, ...),
                // daher auf den UI-Thread posten statt direkt von diesem Hintergrund-Loop aus.
                var clockState = await Dispatcher.UIThread.InvokeAsync(_clockStateProvider);
                await BroadcastRpcAsync(RpcMethods.SessionHeartbeat, clockState).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // erwartet bei Stop()
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Heartbeat-Schleife unerwartet beendet");
        }
    }

    private void HandleClientRpcFrame(ClientConnection connection, byte[] payload)
    {
        if (payload.Length > MaxInboundRpcPayloadBytes)
        {
            Log.Warning("Eingehendes RPC-Frame ({Size} Bytes) überschreitet Limit ({Max} Bytes) - wird ignoriert",
                payload.Length, MaxInboundRpcPayloadBytes);
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
                case RpcMethods.SessionHello:
                    var hello = raw.GetParams<SessionHelloParams>();
                    if (hello is not null) connection.HelloTcs.TrySetResult(hello);
                    break;
                case RpcMethods.MediaPlaybackStarted:
                    var started = raw.GetParams<MediaPlaybackStartedParams>();
                    if (started is not null)
                    {
                        Log.Debug("Client meldet Wiedergabestart für {MediaId} ({DurationMs} ms)", started.MediaId,
                            started.DurationMs);
                        ClientReportedPlaybackStarted?.Invoke(started.MediaId, started.DurationMs);
                    }

                    break;
                case RpcMethods.MediaPlaybackEnded:
                    var ended = raw.GetParams<MediaPlaybackEndedParams>();
                    if (ended is not null)
                    {
                        Log.Information("Client meldet Wiedergabeende für {MediaId}", ended.MediaId);
                        ClientReportedPlaybackEnded?.Invoke(ended.MediaId);
                    }

                    break;
                default:
                    Log.Debug("Unbekannte eingehende RPC-Methode {Method} ignoriert", raw.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fehlerhafte eingehende RPC-Notification {Method} ignoriert", raw.Method);
        }
    }

    private void RemoveClient(ClientConnection client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }

        client.Dispose();
        NotifyClientsChanged();
        Log.Information("Client getrennt: {Client}", client);
    }

    /// <summary>Trennt einen einzelnen Client manuell (SL-Aktion "Trennen" in der Client-Liste).</summary>
    public void DisconnectClient(string remoteEndpoint)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
        }

        if (target is null)
        {
            Log.Warning("Manuelles Trennen fehlgeschlagen: kein verbundener Client mit Endpoint {Endpoint}",
                remoteEndpoint);
            return;
        }

        Log.Information("Client manuell getrennt: {Client}", target);
        RemoveClient(target);
    }

    private void NotifyClientsChanged()
    {
        List<ConnectedClientInfo> snapshot;
        lock (_gate)
        {
            snapshot = _clients.Select(c => new ConnectedClientInfo(c.RemoteEndpoint, c.ConnectedAtUtc)).ToList();
        }

        ClientCountChanged?.Invoke(snapshot.Count);
        ClientsChanged?.Invoke(snapshot);
    }

    public void Stop()
    {
        if (!IsRunning && _listener is null) return;

        Log.Information("TCP-Spielerserver wird gestoppt (Port {Port})", Port);
        IsRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        try
        {
            _announcer?.Dispose();
        }
        catch
        {
        }

        try
        {
            _lanDiscoveryResponder?.Dispose();
        }
        catch
        {
        }

        _listener = null;
        _announcer = null;
        _lanDiscoveryResponder = null;

        lock (_gate)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }

        NotifyClientsChanged();

        lock (_mediaGate)
        {
            _lastMedia = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    private sealed class ClientConnection : IDisposable
    {
        // Großzügig genug, um ein großes Video-Chunk auch über eine langsame Verbindung noch
        // zuzustellen, statt es als tote Verbindung zu werten (Chunks sind klein, siehe
        // MediaChunkSize, daher genügt hier ein deutlich kürzeres Timeout als für einen
        // hypothetischen Ein-Frame-Transfer der ganzen Datei).
        private static readonly TimeSpan MustDeliverWriteTimeout = TimeSpan.FromSeconds(15);

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unbekannt";
        }

        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public string RemoteEndpoint { get; }

        /// <summary>Gesetzt, sobald der Handshake erfolgreich war (siehe PerformHandshakeAsync) - vorher "unauthentifiziert".</summary>
        public bool IsAccepted { get; set; }

        /// <summary>Wird von HandleClientRpcFrame beim Empfang von session.hello aufgelöst; PerformHandshakeAsync wartet darauf.</summary>
        public TaskCompletionSource<SessionHelloParams> HelloTcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            try
            {
                _stream.Dispose();
            }
            catch
            {
            }

            try
            {
                _client.Dispose();
            }
            catch
            {
            }

            _writeLock.Dispose();
        }

        public async Task<bool> TryWriteFrameAsync(byte frameType, byte[] payload, bool skipIfBusy = false)
        {
            bool acquired;
            if (skipIfBusy)
            {
                // Nicht warten: Wenn gerade Medien-Chunks für diese Verbindung geschrieben
                // werden, überspringen wir dieses State-Event einfach, statt zu blockieren
                // oder die Verbindung fälschlich zu trennen.
                acquired = await _writeLock.WaitAsync(0).ConfigureAwait(false);
                if (!acquired) return true;
            }
            else
            {
                acquired = await _writeLock.WaitAsync(MustDeliverWriteTimeout).ConfigureAwait(false);
                if (!acquired)
                {
                    Log.Warning("Schreib-Timeout ({Timeout}) für {Client} - Verbindung gilt als tot",
                        MustDeliverWriteTimeout, this);
                    return false;
                }
            }

            try
            {
                await NetworkFrame.WriteAsync(_stream, frameType, payload).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Schreiben an {Client} fehlgeschlagen (Verbindung vermutlich getrennt)", this);
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public override string ToString()
        {
            return RemoteEndpoint;
        }

        /// <summary>
        ///     Liest Frames vom Client (nur kleine Playback-Status-Notifications erwartet, siehe MaxInboundRpcPayloadBytes)
        ///     bis die Verbindung endet.
        /// </summary>
        public async Task ReadLoopAsync(CancellationToken token, Action<byte[]> onRpcFrame)
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await NetworkFrame.ReadAsync(_stream, MaxInboundRpcPayloadBytes, token)
                    .ConfigureAwait(false);
                if (frame is null) break;

                if (frame.Value.Type == NetworkFrame.TypeRpc) onRpcFrame(frame.Value.Payload);
                // Unbekannter Frame-Typ vom Client: ignorieren statt zu trennen.
            }
        }
    }
}