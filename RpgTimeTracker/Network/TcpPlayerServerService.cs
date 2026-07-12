using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Network;
using RpgTimeTracker.Shared.Services.Rpc;
using Serilog;

namespace RpgTimeTracker.Network;

/// <summary>
///     Display info for a connected client - for the GM-side "connected clients" list including manual
///     disconnect.
/// </summary>
public sealed record ConnectedClientInfo(
    string RemoteEndpoint,
    DateTime ConnectedAtUtc,
    bool MusicEnabled,
    bool SoundEnabled);

/// <summary>
///     Read-only TCP publisher for the player client. Sends only JSON-RPC
///     notifications (see RpgTimeTracker.Shared.Models.Rpc/RpgTimeTracker.Shared.Services.Rpc): a full state once on connect
///     (session.snapshot), afterwards only targeted delta events on actual changes
///     (start/stop/speed/jump/item change) instead of a periodically pushed full state.
///     Media is streamed in chunks: media.begin (metadata) followed by N raw binary frames.
/// </summary>
public sealed class TcpPlayerServerService : IDisposable
{
    public const int DefaultPort = 48550;

    /// <summary>
    ///     Safety limit for a single image/video - see RpgTimeTracker.Shared.Models.Network.MediaLimits (shared
    ///     with the client, so both sides know the same limit).
    /// </summary>
    public const long MaxMediaBytes = MediaLimits.MaxMediaBytes;

    /// <summary>Chunk size for media streaming: small enough that state RPCs in between don't have to wait long.</summary>
    private const int MediaChunkSize = 64 * 1024;

    /// <summary>
    ///     The client only sends tiny playback status notifications back - generous enough, but small enough to
    ///     limit a broken/malicious client.
    /// </summary>
    private const int MaxInboundRpcPayloadBytes = 4 * 1024;

    /// <summary>
    ///     How often a heartbeat is sent, even without a content change. The client
    ///     uses this as an idle watchdog signal: TcpClient.ReceiveTimeout doesn't apply to
    ///     asynchronous reads, without a heartbeat a silently dropped cable/WiFi would never be detected.
    ///     The heartbeat also carries the current clock state, so that the locally derived
    ///     client clock is periodically corrected against drift.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly List<ClientConnection> _clients = [];
    private readonly Func<ClockHeartbeatParams> _clockStateProvider;
    private readonly object _gate = new();
    private readonly object _mediaGate = new();

    /// <summary>
    ///     Serializes complete media transfers (header + all chunks): the chunk frame
    ///     doesn't carry a MediaId (see NetworkFrame.TypeMediaChunk), so two media transfers
    ///     sent at the same time (e.g. two sounds triggered in quick succession) would otherwise mix
    ///     their chunks on the same TCP connection. Waiting transfers simply run one after another - the
    ///     already FULLY received playback of a previous sound keeps running independently of that.
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
    ///     Last sent image/video (header + complete file), for newly connecting clients.
    ///     Sounds DELIBERATELY don't register here - they aren't a "currently displayed" medium that
    ///     a late-joining client would need to wait for (see PublishMediaAsync).
    /// </summary>
    private (MediaHeaderDto Header, byte[] FileBytes)? _lastMedia;

    /// <summary>
    ///     The map currently open to players, if any - cached so a newly connecting/reconnecting
    ///     client gets a full resync (floor images + current fog) instead of an incremental
    ///     replay, per the "never partially revealed/hidden by accident" requirement. Updated
    ///     in place as fog changes (PublishMapFogUpdateAsync/PublishMapFogResetAsync) so the
    ///     cache always reflects exactly what clients currently see.
    /// </summary>
    private OpenMapState? _openMap;

    private TcpListener? _listener;

    public TcpPlayerServerService(Func<SessionSnapshotParams> snapshotProvider,
        Func<ClockHeartbeatParams> clockStateProvider,
        Func<string?>? connectionPinProvider = null)
    {
        _snapshotProvider = snapshotProvider;
        _clockStateProvider = clockStateProvider;
        _connectionPinProvider = connectionPinProvider ?? (() => null);
    }

    /// <summary>How long to wait for the first session.hello of a newly connected client before the connection is considered dead.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    public int Port { get; private set; } = DefaultPort;
    public bool IsRunning { get; private set; }

    public void Dispose()
    {
        Stop();
        _mediaSendLock.Dispose();
    }

    /// <summary>Fires on every change of the connected client count (background thread, caller must dispatch if needed).</summary>
    public event Action<int>? ClientCountChanged;

    /// <summary>
    ///     Fires with the full list on every change of connected clients (background thread, caller
    ///     must dispatch if needed).
    /// </summary>
    public event Action<IReadOnlyList<ConnectedClientInfo>>? ClientsChanged;

    /// <summary>A client reports that a video has actually started playing (background thread).</summary>
    public event Action<string, long>? ClientReportedPlaybackStarted;

    /// <summary>A client reports that a non-looping video has finished on its end (background thread).</summary>
    public event Action<string>? ClientReportedPlaybackEnded;

    /// <summary>A client reports that the currently playing music track has finished (background
    ///     thread) - see RpcMethods.MusicTrackEnded.</summary>
    public event Action<string>? ClientReportedMusicTrackEnded;

    /// <summary>
    ///     Starts the TCP listener, and by default the mDNS + LAN-broadcast discovery responders
    ///     alongside it. <paramref name="enableDiscovery"/> lets a caller skip those - the real
    ///     app exposes this as the <c>--no-discovery</c> CLI flag (see Program.cs) for restricted
    ///     networks, and integration tests use it to avoid best-effort UDP broadcast noise/flakiness
    ///     in CI sandboxes that don't need it (tests connect directly by IP:port, never via discovery).
    ///
    ///     <paramref name="port"/> of 0 asks the OS for a free ephemeral port instead of a fixed
    ///     one - <see cref="Port"/> is updated to the actual bound port afterward, so a caller
    ///     (currently only tests) can read it back. Real usage always passes an explicit port.
    /// </summary>
    public void Start(int port = DefaultPort, string serverName = "RpgTimeTracker", bool enableDiscovery = true)
    {
        if (IsRunning) return;

        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsRunning = true;

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

        if (enableDiscovery)
        {
            _announcer = new PlayerMdnsAnnouncer(Port, serverName);
            _announcer.Start();

            _lanDiscoveryResponder = new LanDiscoveryResponder(Port, serverName);
            _lanDiscoveryResponder.Start();
        }

        Log.Information("TCP player server started on port {Port} (server name {ServerName}, discovery={EnableDiscovery})",
            Port, serverName, enableDiscovery);
    }

    // ==================== Granular state events ====================

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
    ///     Sends a complete state resync to ALL connected clients (not just newly
    ///     connecting ones) - for special cases where the state is completely replaced (e.g. loading
    ///     a saved game), where individual deltas would be impractical.
    /// </summary>
    public Task PublishFullResyncAsync(SessionSnapshotParams snapshot)
    {
        return BroadcastRpcAsync(RpcMethods.SessionSnapshot, snapshot);
    }

    /// <summary>
    ///     Distributes an image, video, sound, or music track in chunks (media.begin + N binary
    ///     chunks). Image/video are additionally cached for late-joining clients (see
    ///     _lastMedia); sounds and music are not - neither is a "currently displayed" medium a
    ///     late-joining client would need to catch up on (music has its own Host-driven playlist
    ///     sequencer instead, see MainWindowViewModel).
    /// </summary>
    public async Task PublishMediaAsync(MediaHeaderDto header, byte[] fileBytes)
    {
        if (!IsRunning) return;
        if (fileBytes.Length > MaxMediaBytes)
        {
            Log.Warning("Medium {FileName} discarded: {SizeMb} MB exceeds the limit of {MaxMb} MB",
                header.FileName, fileBytes.Length / (1024 * 1024), MaxMediaBytes / (1024 * 1024));
            return;
        }

        header.TotalLength = fileBytes.Length;

        if (header.Kind != MediaHeaderDto.MediaKindAudio && header.Kind != MediaHeaderDto.MediaKindMusic)
            lock (_mediaGate)
            {
                _lastMedia = (header, fileBytes);
            }

        // Image/video/map-floor always go to every window; Sound/Music respect the GM's
        // per-window routing toggles (see SetClientMusicEnabled/SetClientSoundEnabled).
        Func<ClientConnection, bool>? routingFilter = header.Kind switch
        {
            MediaHeaderDto.MediaKindAudio => c => c.SoundEnabled,
            MediaHeaderDto.MediaKindMusic => c => c.MusicEnabled,
            _ => null
        };

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = routingFilter is null ? _clients.ToArray() : _clients.Where(routingFilter).ToArray();
        }

        Log.Information("Sending medium {FileName} ({Kind}, {SizeKb} KB, Loop={Loop}) to {ClientCount} client(s)",
            header.FileName, header.Kind, fileBytes.Length / 1024, header.Loop, clients.Length);

        // Whole transfer (header + all chunks) serialized - see _mediaSendLock comment.
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

        Log.Debug("Medium cleared (media.cleared to all clients)");
        return BroadcastRpcAsync(RpcMethods.MediaCleared, RpcEmptyParams.Instance);
    }

    /// <summary>
    ///     Stops a single, currently playing sound (by MediaId) on Sound-enabled client windows -
    ///     independent of the image/video "current medium" slot, see MediaKindAudio.
    /// </summary>
    public Task PublishStopSoundAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaStopSound, new MediaStopSoundParams { MediaId = mediaId },
            c => c.SoundEnabled);
    }

    /// <summary>Adjusts the volume of a currently playing sound live on Sound-enabled client windows (0-100).</summary>
    public Task PublishSetSoundVolumeAsync(string mediaId, int volume)
    {
        return BroadcastRpcAsync(RpcMethods.MediaSetVolume,
            new MediaSetVolumeParams { MediaId = mediaId, Volume = volume }, c => c.SoundEnabled);
    }

    /// <summary>
    ///     Distributes a music track (media.begin + chunks, Kind=MediaKindMusic) to Music-enabled
    ///     client windows - thin wrapper around PublishMediaAsync (which applies the routing
    ///     filter) kept as its own named method for symmetry with
    ///     PublishMusicStopAsync/PublishMusicSetVolumeAsync.
    /// </summary>
    public Task PublishMusicTrackAsync(MediaHeaderDto header, byte[] fileBytes)
    {
        header.Kind = MediaHeaderDto.MediaKindMusic;
        return PublishMediaAsync(header, fileBytes);
    }

    /// <summary>Stops the currently playing music track/playlist on Music-enabled client windows.</summary>
    public Task PublishMusicStopAsync()
    {
        return BroadcastRpcAsync(RpcMethods.MusicStop, RpcEmptyParams.Instance, c => c.MusicEnabled);
    }

    /// <summary>Adjusts the volume of the currently playing music track live on Music-enabled client windows (0-100).</summary>
    public Task PublishMusicSetVolumeAsync(int volume)
    {
        return BroadcastRpcAsync(RpcMethods.MusicSetVolume, new MusicSetVolumeParams { Volume = volume },
            c => c.MusicEnabled);
    }

    /// <summary>Removes an image/video from the gallery on all clients specifically (by MediaId).</summary>
    public Task PublishRetractAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaRetract, new MediaRetractParams { MediaId = mediaId });
    }

    /// <summary>GM "highlights": all clients jump (locally, non-blocking) to this gallery item.</summary>
    public Task PublishHighlightAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaHighlight, new MediaHighlightParams { MediaId = mediaId });
    }

    /// <summary>Sets the automatic advance time per image on all clients (seconds, 0 = manual).</summary>
    public Task PublishSlideshowIntervalAsync(double seconds)
    {
        return BroadcastRpcAsync(RpcMethods.MediaSlideshowInterval,
            new MediaSlideshowIntervalParams { Seconds = seconds });
    }

    /// <summary>
    ///     Opens a map to all connected clients: streams each floor's image (chunk pipeline,
    ///     Kind=MediaKindMapFloor) followed by the map.show metadata (fog masks base64-encoded).
    ///     Caches the open state so later-connecting clients get the same full resync.
    /// </summary>
    public async Task PublishMapShowAsync(Guid mapId, string mapName, List<OpenMapFloor> floors)
    {
        if (!IsRunning) return;

        lock (_mediaGate)
        {
            _openMap = new OpenMapState(mapId, mapName, floors);
        }

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
        }

        Log.Information("Map opened: {MapName} ({FloorCount} floors) to {ClientCount} client(s)",
            mapName, floors.Count, clients.Length);

        await _mediaSendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in clients)
                await SendMapShowToClientAsync(client, mapId, mapName, floors).ConfigureAwait(false);
        }
        finally
        {
            _mediaSendLock.Release();
        }
    }

    /// <summary>
    ///     Debounced reveal/hide update for one floor of the currently open map. Also applies the
    ///     same cells to the cached current fog, so a client that connects afterward sees the
    ///     latest state via a full map.show rather than this incremental delta.
    /// </summary>
    public Task PublishMapFogUpdateAsync(Guid floorId, List<FogCellDto> cells)
    {
        lock (_mediaGate)
        {
            var floor = _openMap?.Floors.FirstOrDefault(f => f.FloorId == floorId);
            if (floor is not null)
                foreach (var cell in cells)
                    floor.CurrentFog.SetRevealed(cell.X, cell.Y, cell.Revealed);
        }

        return BroadcastRpcAsync(RpcMethods.MapFogUpdate, new MapFogUpdateParams { FloorId = floorId, Cells = cells });
    }

    /// <summary>Resets one floor's live fog back to its starting template on all clients.</summary>
    public Task PublishMapFogResetAsync(Guid floorId)
    {
        lock (_mediaGate)
        {
            var floor = _openMap?.Floors.FirstOrDefault(f => f.FloorId == floorId);
            if (floor is not null) floor.CurrentFog = floor.StartingFog.Clone();
        }

        return BroadcastRpcAsync(RpcMethods.MapFogReset, new MapFogResetParams { FloorId = floorId });
    }

    /// <summary>Closes the currently open map on all clients; they return to the previous gallery display.</summary>
    public Task PublishMapHideAsync()
    {
        lock (_mediaGate)
        {
            _openMap = null;
        }

        Log.Debug("Map closed (map.hide to all clients)");
        return BroadcastRpcAsync(RpcMethods.MapHide, RpcEmptyParams.Instance);
    }

    /// <summary>
    ///     Live change of the player-side fog render style (one global preference - see issue
    ///     #22). No server-side cache needed for reconnect: the current value is read directly
    ///     from the Host's own settings each time session.snapshot is built.
    /// </summary>
    public Task PublishMapRenderStyleAsync(string colorHex, int opacityPercent, double blurRadius, bool blurEnabled)
    {
        return BroadcastRpcAsync(RpcMethods.MapRenderStyleChanged, new MapRenderStyleChangedParams
        {
            ColorHex = colorHex,
            OpacityPercent = opacityPercent,
            BlurRadius = blurRadius,
            BlurEnabled = blurEnabled
        });
    }

    private async Task SendMapShowToClientAsync(ClientConnection client, Guid mapId, string mapName,
        List<OpenMapFloor> floors)
    {
        foreach (var floor in floors)
        {
            var header = new MediaHeaderDto
            {
                MediaId = floor.FloorId.ToString(),
                Kind = MediaHeaderDto.MediaKindMapFloor,
                FileName = floor.ImageFileName,
                MimeType = floor.ImageMimeType,
                TotalLength = floor.ImageBytes.Length
            };
            await SendMediaToClientAsync(client, header, floor.ImageBytes).ConfigureAwait(false);
        }

        var showParams = new MapShowParams
        {
            MapId = mapId,
            MapName = mapName,
            Floors = floors.Select(f => new MapFloorShowDto
            {
                FloorId = f.FloorId,
                FloorName = f.FloorName,
                ImageMediaId = f.FloorId.ToString(),
                CellSizePx = f.CellSizePx,
                GridWidth = f.GridWidth,
                GridHeight = f.GridHeight,
                StartingFogBase64 = Convert.ToBase64String(FogMaskSerializer.Serialize(f.StartingFog)),
                CurrentFogBase64 = Convert.ToBase64String(FogMaskSerializer.Serialize(f.CurrentFog))
            }).ToList()
        };

        var payload = RpcMessage.Serialize(RpcMethods.MapShow, showParams);
        if (!await client.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false))
        {
            Log.Warning("map.show to {Client} failed - connection is being closed", client);
            RemoveClient(client);
        }
    }

    /// <summary>One floor of the currently open map, cached server-side for resync (see _openMap).</summary>
    public sealed class OpenMapFloor
    {
        public Guid FloorId { get; init; }
        public string FloorName { get; init; } = string.Empty;
        public string ImageFileName { get; init; } = string.Empty;
        public string ImageMimeType { get; init; } = string.Empty;
        public byte[] ImageBytes { get; init; } = [];
        public int CellSizePx { get; init; }
        public int GridWidth { get; init; }
        public int GridHeight { get; init; }
        public FogMask StartingFog { get; init; } = FogMask.CreateFullyHidden(1, 1, 32);
        public FogMask CurrentFog { get; set; } = FogMask.CreateFullyHidden(1, 1, 32);
    }

    private sealed class OpenMapState(Guid mapId, string mapName, List<OpenMapFloor> floors)
    {
        public Guid MapId { get; } = mapId;
        public string MapName { get; } = mapName;
        public List<OpenMapFloor> Floors { get; } = floors;
    }

    private async Task SendMediaToClientAsync(ClientConnection client, MediaHeaderDto header, byte[] fileBytes)
    {
        var beginPayload = RpcMessage.Serialize(RpcMethods.MediaBegin, header);
        if (!await client.TryWriteFrameAsync(NetworkFrame.TypeRpc, beginPayload).ConfigureAwait(false))
        {
            Log.Warning("media.begin to {Client} failed - connection is being closed", client);
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
                    "Media chunk to {Client} at offset {Offset}/{Total} failed - connection is being closed",
                    client, offset, fileBytes.Length);
                RemoveClient(client);
                return;
            }
        }

        Log.Debug("Medium {FileName} fully sent to {Client}", header.FileName, client);
    }

    private async Task BroadcastRpcAsync<TParams>(string method, TParams @params,
        Func<ClientConnection, bool>? filter = null)
    {
        if (!IsRunning) return;

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

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = filter is null ? _clients.ToArray() : _clients.Where(filter).ToArray();
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

                // Connection is added to _clients / gets its session.snapshot ONLY after
                // successful session.hello (see PerformHandshakeAsync) - before that it is
                // "unauthenticated" and invisible to the rest of the app (no entry in the
                // client list, not a broadcast target).
                _ = connection.ReadLoopAsync(token, payload => HandleClientRpcFrame(connection, payload))
                    .ContinueWith(_ =>
                    {
                        // Only report a "real" disconnect if the connection was actually
                        // accepted - a connection rejected during handshake was never
                        // added to _clients and needs no disconnect notification/cleanup.
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
                Log.Warning(ex, "Error accepting an incoming connection");
                tcpClient?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Waits for the first session.hello of a newly accepted connection (see
    ///     AcceptLoopAsync) and then decides: no hello within HandshakeTimeout,
    ///     wrong PIN, or mismatched protocol version -> session.helloRejected + disconnect;
    ///     otherwise the connection is only now added to _clients and gets its
    ///     session.snapshot. Deliberately checked BEFORE adding to _clients, so that a
    ///     rejected client never shows up in the GM-side "connected clients" list or
    ///     receives broadcasts.
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
        Log.Information("Client connected: {Client}", connection);

        // _snapshotProvider()/sending the medium afterward reads UI-bound data,
        // so post to the UI thread instead of doing it directly from this background loop.
        Dispatcher.UIThread.Post(() => _ = SendCatchUpAsync(connection));
    }

    private async Task RejectAndCloseAsync(ClientConnection connection, string reason)
    {
        Log.Information("Connection from {Client} rejected: {Reason}", connection, reason);
        try
        {
            var payload = RpcMessage.Serialize(RpcMethods.SessionHelloRejected,
                new SessionHelloRejectedParams { Reason = reason });
            await connection.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "session.helloRejected to {Client} could not be sent", connection);
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
            Log.Error(ex, "session.snapshot could not be created for {Client}", connection);
            return;
        }

        byte[] payload;
        try
        {
            payload = RpcMessage.Serialize(RpcMethods.SessionSnapshot, snapshot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "session.snapshot could not be serialized for {Client}", connection);
            return;
        }

        if (!await connection.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false))
        {
            Log.Warning("session.snapshot to newly connected {Client} failed - connection is being closed",
                connection);
            RemoveClient(connection);
            return;
        }

        Log.Debug("session.snapshot sent to {Client} ({ItemCount} items)", connection, snapshot.Items.Count);

        (MediaHeaderDto Header, byte[] FileBytes)? cachedMedia;
        lock (_mediaGate)
        {
            cachedMedia = _lastMedia;
        }

        if (cachedMedia is not null)
            await SendMediaToClientAsync(connection, cachedMedia.Value.Header, cachedMedia.Value.FileBytes)
                .ConfigureAwait(false);

        OpenMapState? openMap;
        lock (_mediaGate)
        {
            openMap = _openMap;
        }

        // Full resync (all floor images + current fog), never an incremental replay - a
        // reconnecting client must never end up with a fog state that's partially stale.
        if (openMap is not null)
            await SendMapShowToClientAsync(connection, openMap.MapId, openMap.MapName, openMap.Floors)
                .ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);

                // _clockStateProvider reads UI-bound fields (_clock, SpeedMultiplier, ...),
                // so post to the UI thread instead of doing it directly from this background loop.
                var clockState = await Dispatcher.UIThread.InvokeAsync(_clockStateProvider);
                await BroadcastRpcAsync(RpcMethods.SessionHeartbeat, clockState).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Stop()
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Heartbeat loop terminated unexpectedly");
        }
    }

    private void HandleClientRpcFrame(ClientConnection connection, byte[] payload)
    {
        if (payload.Length > MaxInboundRpcPayloadBytes)
        {
            Log.Warning("Incoming RPC frame ({Size} bytes) exceeds limit ({Max} bytes) - ignored",
                payload.Length, MaxInboundRpcPayloadBytes);
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
                case RpcMethods.SessionHello:
                    var hello = raw.GetParams<SessionHelloParams>();
                    if (hello is not null) connection.HelloTcs.TrySetResult(hello);
                    break;
                case RpcMethods.MediaPlaybackStarted:
                    var started = raw.GetParams<MediaPlaybackStartedParams>();
                    if (started is not null)
                    {
                        Log.Debug("Client reports playback started for {MediaId} ({DurationMs} ms)", started.MediaId,
                            started.DurationMs);
                        ClientReportedPlaybackStarted?.Invoke(started.MediaId, started.DurationMs);
                    }

                    break;
                case RpcMethods.MediaPlaybackEnded:
                    var ended = raw.GetParams<MediaPlaybackEndedParams>();
                    if (ended is not null)
                    {
                        Log.Information("Client reports playback ended for {MediaId}", ended.MediaId);
                        ClientReportedPlaybackEnded?.Invoke(ended.MediaId);
                    }

                    break;
                case RpcMethods.MusicTrackEnded:
                    var musicEnded = raw.GetParams<MusicTrackEndedParams>();
                    if (musicEnded is not null)
                    {
                        Log.Information("Client reports music track ended for {MediaId}", musicEnded.MediaId);
                        ClientReportedMusicTrackEnded?.Invoke(musicEnded.MediaId);
                    }

                    break;
                default:
                    Log.Debug("Unknown incoming RPC method {Method} ignored", raw.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Malformed incoming RPC notification {Method} ignored", raw.Method);
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
        Log.Information("Client disconnected: {Client}", client);
    }

    /// <summary>Disconnects a single client manually (GM action "disconnect" in the client list).</summary>
    public void DisconnectClient(string remoteEndpoint)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
        }

        if (target is null)
        {
            Log.Warning("Manual disconnect failed: no connected client with endpoint {Endpoint}",
                remoteEndpoint);
            return;
        }

        Log.Information("Client manually disconnected: {Client}", target);
        RemoveClient(target);
    }

    /// <summary>Toggles whether this client window receives Music broadcasts (GM per-window
    ///     routing control) - session-scoped, resets to enabled on reconnect.</summary>
    public void SetClientMusicEnabled(string remoteEndpoint, bool enabled)
    {
        lock (_gate)
        {
            var target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.MusicEnabled = enabled;
        }

        NotifyClientsChanged();
    }

    /// <summary>Toggles whether this client window receives Sound broadcasts (GM per-window
    ///     routing control) - session-scoped, resets to enabled on reconnect.</summary>
    public void SetClientSoundEnabled(string remoteEndpoint, bool enabled)
    {
        lock (_gate)
        {
            var target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.SoundEnabled = enabled;
        }

        NotifyClientsChanged();
    }

    private void NotifyClientsChanged()
    {
        List<ConnectedClientInfo> snapshot;
        lock (_gate)
        {
            snapshot = _clients
                .Select(c => new ConnectedClientInfo(c.RemoteEndpoint, c.ConnectedAtUtc, c.MusicEnabled, c.SoundEnabled))
                .ToList();
        }

        ClientCountChanged?.Invoke(snapshot.Count);
        ClientsChanged?.Invoke(snapshot);
    }

    public void Stop()
    {
        if (!IsRunning && _listener is null) return;

        Log.Information("TCP player server is stopping (port {Port})", Port);
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
        // Generous enough to still deliver a large video chunk even over a slow connection,
        // instead of treating it as a dead connection (chunks are small, see
        // MediaChunkSize, so a much shorter timeout suffices here than for a
        // hypothetical single-frame transfer of the whole file).
        private static readonly TimeSpan MustDeliverWriteTimeout = TimeSpan.FromSeconds(15);

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public string RemoteEndpoint { get; }

        /// <summary>Set once the handshake succeeded (see PerformHandshakeAsync) - "unauthenticated" before that.</summary>
        public bool IsAccepted { get; set; }

        /// <summary>Whether this window (session-scoped, resets to true on reconnect - no
        ///     persistent client identity today) receives Music/Sound broadcasts, set by the GM
        ///     via SetClientMusicEnabled/SetClientSoundEnabled.</summary>
        public bool MusicEnabled { get; set; } = true;

        public bool SoundEnabled { get; set; } = true;

        /// <summary>Resolved by HandleClientRpcFrame upon receiving session.hello; PerformHandshakeAsync waits for it.</summary>
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
                // Don't wait: if media chunks are currently being written for this
                // connection, we simply skip this state event instead of blocking
                // or incorrectly closing the connection.
                acquired = await _writeLock.WaitAsync(0).ConfigureAwait(false);
                if (!acquired) return true;
            }
            else
            {
                acquired = await _writeLock.WaitAsync(MustDeliverWriteTimeout).ConfigureAwait(false);
                if (!acquired)
                {
                    Log.Warning("Write timeout ({Timeout}) for {Client} - connection considered dead",
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
                Log.Debug(ex, "Write to {Client} failed (connection presumably closed)", this);
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
        ///     Reads frames from the client (only small playback status notifications expected, see MaxInboundRpcPayloadBytes)
        ///     until the connection ends.
        /// </summary>
        public async Task ReadLoopAsync(CancellationToken token, Action<byte[]> onRpcFrame)
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await NetworkFrame.ReadAsync(_stream, MaxInboundRpcPayloadBytes, token)
                    .ConfigureAwait(false);
                if (frame is null) break;

                if (frame.Value.Type == NetworkFrame.TypeRpc) onRpcFrame(frame.Value.Payload);
                // Unknown frame type from the client: ignore instead of disconnecting.
            }
        }
    }
}