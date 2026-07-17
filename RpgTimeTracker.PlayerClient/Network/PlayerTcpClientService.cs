using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RpgTimeTracker.PlayerClient.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Network;
using RpgTimeTracker.Shared.Services.Rpc;
using Serilog;

namespace RpgTimeTracker.PlayerClient.Network;

/// <summary>
///     Client for the JSON-RPC delta protocol (see RpgTimeTracker.Shared.Models.Rpc/RpgTimeTracker.Shared.Services.Rpc):
///     receives a
///     full session.snapshot on connect, only targeted events after that. Media arrives
///     chunked (media.begin + N binary frames) and is written progressively into a temp
///     file, so playback can start before the transfer is finished.
/// </summary>
public sealed class PlayerTcpClientService : IDisposable
{
    private const int MaxRpcPayloadBytes = 256 * 1024;

    // Shared with the host (see RpgTimeTracker.Shared.Models.Network.MediaLimits) - different
    // values here and on the host would cause a larger medium accepted/distributed by the host
    // to be silently discarded here without any error appearing anywhere.
    // Fits comfortably into int (array/NetworkFrame lengths are int-limited anyway).
    private const int MaxMediaBytes = (int)MediaLimits.MaxMediaBytes;

    // Server sends at least something every TcpPlayerServerService.HeartbeatInterval (5s).
    // 15s idle tolerance gives buffer for jitter, without detecting a real disconnect too late.
    // TcpClient.ReceiveTimeout does NOT apply to ReadAsync, hence this separate watchdog -
    // without it the client never notices a silently cut cable/WiFi and stays stuck
    // "connected" even though nothing arrives anymore (UI appears frozen as a result).
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    // Automatic reconnect after an unexpected disconnection (idle timeout, network error,
    // host restart) - grows per failure so a permanently unreachable host isn't knocked on
    // every second, while a brief hiccup (WiFi stall, host restart) is still bridged quickly.
    // Only a MANUAL disconnect (Disconnect()) stops the reconnect attempt for good - see
    // _userDisconnectRequested.
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

    // State of the currently running media stream (only one medium at a time per connection).
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
    public event Action<GameInstant>? ClockTimeJumped;
    public event Action<string, string>? HeaderChanged;
    public event Action<string>? ThemeChanged;
    public event Action<TimelineItemSnapshotDto>? TimelineItemUpserted;
    public event Action<Guid>? TimelineItemRemoved;
    public event Action<bool>? DisplayFullscreenRequested;

    /// <summary>Periodic heartbeat including clock state - for drift correction of the local clock.</summary>
    public event Action<GameInstant, double, bool>? ClockHeartbeatReceived;

    /// <summary>A medium begins: kind/filename/MIME + path of a (still growing) temp file.</summary>
    public event Action<MediaHeaderDto, string>? MediaBeginReceived;

    /// <summary>Transfer complete - relevant for images (which can only now be decoded).</summary>
    public event Action<MediaHeaderDto, string>? MediaCompleted;

    public event Action? MediaCleared;

    /// <summary>GM stops a single, currently running sound specifically (by MediaId).</summary>
    public event Action<string>? SoundStopRequested;

    /// <summary>GM adjusts the volume of a currently running sound live (MediaId, 0-100).</summary>
    public event Action<string, int>? SoundVolumeChangeRequested;

    /// <summary>GM removes an image/video specifically from the gallery (by MediaId).</summary>
    public event Action<string>? MediaRetractRequested;

    /// <summary>GM highlights a gallery item (by MediaId) - a jump suggestion, not a lock.</summary>
    public event Action<string>? MediaHighlightRequested;

    /// <summary>Automatic advance time per image (seconds, 0 = manual).</summary>
    public event Action<double>? SlideshowIntervalChanged;

    /// <summary>
    ///     A map floor's image finished transferring (see MediaHeaderDto.MediaKindMapFloor) -
    ///     kept as its own event/temp file rather than routed through MediaCompleted, since a
    ///     map floor is cached long-term for the whole time the map stays open, not displayed
    ///     once and discarded like gallery/event media.
    /// </summary>
    public event Action<Guid, string>? MapFloorImageReceived;

    public event Action<MapShowParams>? MapShowReceived;
    public event Action<MapFogUpdateParams>? MapFogUpdateReceived;
    public event Action<Guid>? MapFogResetReceived;
    public event Action? MapHideReceived;
    public event Action<MapTokenSnapshotDto>? MapTokenUpsertReceived;
    public event Action<Guid>? MapTokenRemoveReceived;
    public event Action<MapRenderStyleChangedParams>? MapRenderStyleChanged;
    public event Action<MapAutoZoomChangedParams>? MapAutoZoomChanged;

    /// <summary>GM double-clicked their own map - see RpcMethods.MapPing.</summary>
    public event Action<MapPingParams>? MapPingReceived;

    /// <summary>
    ///     A music track finished transferring (see MediaHeaderDto.LayerMusic) - kept as its
    ///     own event/temp file rather than routed through MediaCompleted, since music plays on
    ///     its own independent channel (a Host-driven playlist sequencer), never touching the
    ///     image/video gallery slot or the sound-effect ActiveSoundViewModel tracking.
    /// </summary>
    public event Action<MediaHeaderDto, string>? MusicTrackReceived;

    /// <summary>GM stops the currently playing music track/playlist.</summary>
    public event Action? MusicStopRequested;

    /// <summary>GM adjusts the volume of the currently playing music track live (0-100).</summary>
    public event Action<int>? MusicVolumeChangeRequested;

    /// <summary>
    ///     This window's current Music/Sound/Image/Video/Map routing state, sent right after
    ///     handshake and again whenever the GM changes it live (see
    ///     RpcMethods.AudioRoutingChanged). Args: musicEnabled, soundEnabled, imageEnabled,
    ///     videoEnabled, mapEnabled.
    /// </summary>
    public event Action<bool, bool, bool, bool, bool>? AudioRoutingChanged;

    public event Action<string>? StatusChanged;

    /// <summary>
    ///     Discrete connection state, separate from the free-text StatusChanged message - the
    ///     status text is language-dependent (localized) and must never be string-matched to
    ///     derive state, so callers that need a plain connected/disconnected boolean should use
    ///     this event instead of inspecting the StatusChanged text.
    /// </summary>
    public event Action<bool>? ConnectionStateChanged;

    public async Task ConnectAsync(string host, int port, string pin = "")
    {
        Disconnect();
        // Disconnect() sets _userDisconnectRequested=true as a side effect (see there) - that
        // doesn't apply here, since a NEW connection attempt is actively starting right now
        // (whether manual or from within ReconnectLoopAsync).
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

        StatusChanged?.Invoke(string.Format(LocalizationService.Get("PlayerTcpClientService.Status.Connecting"), host,
            port));
        Log.Information("Connection attempt to {Host}:{Port}", host, port);

        // Otherwise a wrong/unreachable manual host can hang for minutes without any
        // feedback (OS TCP SYN timeout) before ConnectAsync even fails - which looks
        // like a UI that doesn't respond to input.
        using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _client.ConnectAsync(host, port, connectTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Log.Warning("Connection to {Host}:{Port} aborted after 5s timeout", host, port);
            throw new TimeoutException(string.Format(
                LocalizationService.Get("PlayerTcpClientService.Errors.NoResponseTimeout"), host, port));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Connection to {Host}:{Port} failed", host, port);
            throw;
        }

        StatusChanged?.Invoke(string.Format(LocalizationService.Get("PlayerTcpClientService.Status.Connected"), host,
            port));
        ConnectionStateChanged?.Invoke(true);
        Log.Information("Connected to {Host}:{Port}", host, port);

        var stream = _client.GetStream();
        _writeStream = stream;
        _ = Task.Run(() => ReadLoopAsync(stream, _cts.Token));

        // Must arrive at the host before the session.snapshot (see
        // TcpPlayerServerService.PerformHandshakeAsync) - without this signal (or with a
        // wrong PIN/incompatible version) the host rejects the connection via
        // session.helloRejected and disconnects it again.
        await SendRpcAsync(RpcMethods.SessionHello,
            new SessionHelloParams
            {
                ProtocolVersion = ProtocolInfo.Version,
                Pin = pin,
                ClientId = ClientSettingsService.GetOrCreateClientId()
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Manual/final disconnect: marks the disconnection as intentional, so the running
    ///     ReadLoopAsync (if currently active) does NOT trigger an automatic reconnect afterward.
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
    ///     Reports to the host that a video has actually started playing, including the duration
    ///     determined locally (LibVLC).
    /// </summary>
    public Task SendMediaPlaybackStartedAsync(string mediaId, TimeSpan duration)
    {
        return SendRpcAsync(RpcMethods.MediaPlaybackStarted,
            new MediaPlaybackStartedParams { MediaId = mediaId, DurationMs = (long)duration.TotalMilliseconds });
    }

    /// <summary>Reports to the host that a non-looping video has ended (host resumes the clock/closes the medium).</summary>
    public Task SendMediaPlaybackEndedAsync(string mediaId)
    {
        return SendRpcAsync(RpcMethods.MediaPlaybackEnded, new MediaPlaybackEndedParams { MediaId = mediaId });
    }

    /// <summary>
    ///     Reports to the host that the currently playing music track has ended, so the
    ///     Host's playlist sequencer can advance to the next track.
    /// </summary>
    public Task SendMusicTrackEndedAsync(string mediaId)
    {
        return SendRpcAsync(RpcMethods.MusicTrackEnded, new MusicTrackEndedParams { MediaId = mediaId });
    }

    /// <summary>Reports a player double-clicking the map, pointing at something for the GM only.</summary>
    public Task SendMapPingFromPlayerAsync(Guid floorId, double x, double y)
    {
        return SendRpcAsync(RpcMethods.MapPingFromPlayer, new MapPingParams { FloorId = floorId, X = x, Y = y });
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
            Log.Error(ex, "RPC notification {Method} could not be serialized", method);
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await NetworkFrame.WriteAsync(stream, NetworkFrame.TypeRpc, payload).ConfigureAwait(false);
            Log.Debug("RPC notification {Method} sent to host", method);
        }
        catch (Exception ex)
        {
            // Connection dead - the ReadLoop detects this itself anyway and cleans up.
            Log.Debug(ex, "RPC notification {Method} could not be sent (connection presumably disconnected)",
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
            Log.Information("Connection closed (disconnected or idle timeout)");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Connection closed with error");
            StatusChanged?.Invoke(string.Format(
                LocalizationService.Get("PlayerTcpClientService.Status.ConnectionEnded"), ex.Message));
        }
        finally
        {
            watchdogCts.Cancel();
            await watchdogTask.ConfigureAwait(false);
            CleanupMediaStream();
            ConnectionStateChanged?.Invoke(false);

            // Only reconnect automatically on an UNEXPECTED disconnection - a manual
            // Disconnect() (see there) sets _userDisconnectRequested beforehand, so this
            // connection stays disconnected for good instead of re-establishing itself.
            if (!_userDisconnectRequested && _lastHost is not null)
                _ = ReconnectLoopAsync(_lastHost, _lastPort);
            else
                StatusChanged?.Invoke(LocalizationService.Get("PlayerTcpClientService.Status.NotConnected"));
        }
    }

    /// <summary>
    ///     Attempts to re-establish the same connection at growing intervals (2s up to max. 20s),
    ///     until it works or the user disconnects themselves/starts another connection. On a
    ///     successful reconnect the reconnecting client automatically gets a full
    ///     session.snapshot from the host (see TcpPlayerServerService.SendCatchUpAsync) - no
    ///     separate resync path needed here.
    /// </summary>
    private async Task ReconnectLoopAsync(string host, int port)
    {
        CancelReconnect();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;
        var delay = ReconnectInitialDelay;

        while (!token.IsCancellationRequested && !_userDisconnectRequested)
        {
            StatusChanged?.Invoke(string.Format(
                LocalizationService.Get("PlayerTcpClientService.Status.ConnectionLostRetrying"),
                delay.TotalSeconds.ToString("0")));
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
                Log.Information("Automatic reconnect attempt to {Host}:{Port}", host, port);
                await ConnectAsync(host, port, _lastPin).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Reconnect attempt to {Host}:{Port} failed", host, port);
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
                    Log.Warning("Idle timeout ({Timeout}) reached - no activity from host, connection considered dead",
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
            Log.Warning("Incoming RPC frame ({Size} bytes) exceeds limit ({Max} bytes) - ignored",
                payload.Length, MaxRpcPayloadBytes);
            return;
        }

        var raw = RpcMessage.TryParseRaw(payload);
        if (raw is null)
        {
            Log.Warning("Incoming RPC frame could not be parsed as JSON-RPC ({Size} bytes)",
                payload.Length);
            return;
        }

        try
        {
            switch (raw.Method)
            {
                case RpcMethods.SessionHelloRejected:
                    var rejected = raw.GetParams<SessionHelloRejectedParams>();
                    var reason = rejected?.Reason ??
                                 LocalizationService.Get(
                                     "PlayerTcpClientService.Status.ConnectionRejectedDefaultReason");
                    Log.Warning("Connection rejected by host: {Reason}", reason);
                    StatusChanged?.Invoke(string.Format(
                        LocalizationService.Get("PlayerTcpClientService.Status.ConnectionRejected"), reason));
                    // No reconnect attempt with the same (apparently wrong) credentials -
                    // Disconnect() sets _userDisconnectRequested=true, see ReadLoopAsync.
                    Disconnect();
                    break;
                case RpcMethods.SessionSnapshot:
                    var snapshot = raw.GetParams<SessionSnapshotParams>();
                    if (snapshot is not null)
                    {
                        Log.Information("session.snapshot received ({ItemCount} items)", snapshot.Items.Count);
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
                    if (jumped is not null) ClockTimeJumped?.Invoke(new GameInstant(jumped.NewGameTimeSeconds));
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
                    Log.Information("media.cleared received");
                    CleanupMediaStream();
                    MediaCleared?.Invoke();
                    break;
                case RpcMethods.SessionHeartbeat:
                    var heartbeat = raw.GetParams<ClockHeartbeatParams>();
                    if (heartbeat is not null)
                        ClockHeartbeatReceived?.Invoke(new GameInstant(heartbeat.CurrentGameTimeSeconds),
                            heartbeat.SpeedMultiplier, heartbeat.IsClockRunning);
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
                case RpcMethods.MapShow:
                    var mapShow = raw.GetParams<MapShowParams>();
                    if (mapShow is not null) MapShowReceived?.Invoke(mapShow);
                    break;
                case RpcMethods.MapFogUpdate:
                    var fogUpdate = raw.GetParams<MapFogUpdateParams>();
                    if (fogUpdate is not null) MapFogUpdateReceived?.Invoke(fogUpdate);
                    break;
                case RpcMethods.MapFogReset:
                    var fogReset = raw.GetParams<MapFogResetParams>();
                    if (fogReset is not null) MapFogResetReceived?.Invoke(fogReset.FloorId);
                    break;
                case RpcMethods.MapHide:
                    MapHideReceived?.Invoke();
                    break;
                case RpcMethods.MapRenderStyleChanged:
                    var renderStyle = raw.GetParams<MapRenderStyleChangedParams>();
                    if (renderStyle is not null) MapRenderStyleChanged?.Invoke(renderStyle);
                    break;
                case RpcMethods.MapAutoZoomChanged:
                    var autoZoom = raw.GetParams<MapAutoZoomChangedParams>();
                    if (autoZoom is not null) MapAutoZoomChanged?.Invoke(autoZoom);
                    break;
                case RpcMethods.MapTokenUpsert:
                    var tokenUpsert = raw.GetParams<MapTokenSnapshotDto>();
                    if (tokenUpsert is not null) MapTokenUpsertReceived?.Invoke(tokenUpsert);
                    break;
                case RpcMethods.MapTokenRemove:
                    var tokenRemove = raw.GetParams<MapTokenRemoveParams>();
                    if (tokenRemove is not null) MapTokenRemoveReceived?.Invoke(tokenRemove.TokenId);
                    break;
                case RpcMethods.MapPing:
                    var ping = raw.GetParams<MapPingParams>();
                    if (ping is not null) MapPingReceived?.Invoke(ping);
                    break;
                case RpcMethods.MusicStop:
                    MusicStopRequested?.Invoke();
                    break;
                case RpcMethods.MusicSetVolume:
                    var musicVolume = raw.GetParams<MusicSetVolumeParams>();
                    if (musicVolume is not null) MusicVolumeChangeRequested?.Invoke(musicVolume.Volume);
                    break;
                case RpcMethods.AudioRoutingChanged:
                    var routing = raw.GetParams<DataRoutingChangedParams>();
                    if (routing is not null)
                        AudioRoutingChanged?.Invoke(routing.MusicEnabled, routing.SoundEnabled,
                            routing.ImageEnabled, routing.VideoEnabled, routing.MapEnabled);
                    break;
                default:
                    Log.Debug("Unknown incoming RPC method {Method} ignored", raw.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Rogue or buggy server: ignore malformed notification, do not crash.
            Log.Warning(ex, "Malformed incoming RPC notification {Method} ignored", raw.Method);
        }
    }

    private void BeginMediaStream(MediaHeaderDto header)
    {
        CleanupMediaStream();

        if (header.TotalLength < 0 || header.TotalLength > MaxMediaBytes)
        {
            Log.Warning("media.begin for {FileName} discarded: TotalLength {TotalLength} invalid/over limit ({Max})",
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

            // Map floor images and music tracks are separate concerns from the gallery/
            // current-medium slot (see MapFloorImageReceived/MusicTrackReceived) - never raise
            // MediaBeginReceived for them, so gallery/event-media handling never has to
            // special-case either. Music is Kind=MediaKindAudio like Sound (see
            // MediaHeaderDto.Layer), so this checks Layer rather than Kind for that one.
            if (header.Kind != MediaHeaderDto.MediaKindMapFloor && header.Layer != MediaHeaderDto.LayerMusic)
                // For videos, playback can start immediately (VLC tolerates a growing file);
                // for images, the caller waits for MediaCompleted since a partial image
                // cannot be decoded.
                MediaBeginReceived?.Invoke(header, _mediaTempPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "media.begin for {FileName} could not be processed (temp file)", header.FileName);
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
                Log.Information("Medium {FileName} fully received ({Bytes} bytes)", header.FileName,
                    _mediaBytesWritten);

                if (header.Kind == MediaHeaderDto.MediaKindMapFloor)
                {
                    if (Guid.TryParse(header.MediaId, out var floorId))
                        MapFloorImageReceived?.Invoke(floorId, path);
                }
                else if (header.Layer == MediaHeaderDto.LayerMusic)
                {
                    MusicTrackReceived?.Invoke(header, path);
                }
                else
                {
                    MediaCompleted?.Invoke(header, path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Media chunk for {FileName} could not be written", _mediaHeader?.FileName);
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