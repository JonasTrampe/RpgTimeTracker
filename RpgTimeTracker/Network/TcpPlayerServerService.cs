using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RpgTimeTracker.Services;
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
    bool SoundEnabled,
    bool ImageEnabled,
    bool VideoEnabled,
    bool MapEnabled,
    bool CanAnnotate);

/// <summary>
///     Read-only TCP publisher for the player client. Sends only JSON-RPC
///     notifications (see RpgTimeTracker.Shared.Models.Rpc/RpgTimeTracker.Shared.Services.Rpc): a full state once on
///     connect
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
    ///     Sized for the biggest inbound message the client actually sends: a freehand map
    ///     annotation stroke (map.annotationFromPlayer), which can run to several hundred points -
    ///     a fast/long Shift-drag easily serializes past several KB of JSON. Everything else the
    ///     client sends back (ping, playback status) is tiny in comparison. Still small enough to
    ///     limit a broken/malicious client - a single stroke has no legitimate reason to approach
    ///     this size (see MapDisplayView.MinStrokePointSpacing, which also caps point count from
    ///     the sending side).
    /// </summary>
    private const int MaxInboundRpcPayloadBytes = 64 * 1024;

    /// <summary>
    ///     How often a heartbeat is sent, even without a content change. The client
    ///     uses this as an idle watchdog signal: TcpClient.ReceiveTimeout doesn't apply to
    ///     asynchronous reads, without a heartbeat a silently dropped cable/WiFi would never be detected.
    ///     The heartbeat also carries the current clock state, so that the locally derived
    ///     client clock is periodically corrected against drift.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long to wait for the first session.hello of a newly connected client before the connection is considered
    ///     dead.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly List<ClientConnection> _clients = [];
    private readonly Func<ClockHeartbeatParams> _clockStateProvider;
    private readonly Func<string?> _connectionPinProvider;
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
    private Task? _acceptTask;
    private PlayerMdnsAnnouncer? _announcer;
    private CancellationTokenSource? _cts;

    /// <summary>
    ///     The music track currently playing (if any), cached so a newly-connecting client (or a
    ///     reconnecting one) gets the currently playing track instead of silence until the Host's
    ///     playlist sequencer happens to advance - unlike _lastMedia (which deliberately excludes
    ///     music), this exists specifically so music DOES catch up new clients, mirroring _openMap.
    ///     StartedAtUtc is when THIS track began (reset in PublishMusicTrackAsync, not touched by
    ///     a live volume change) - used to estimate SeekToMs for a client joining mid-track.
    /// </summary>
    private (MediaHeaderDto Header, byte[] FileBytes, DateTime StartedAtUtc)? _currentMusicTrack;

    private Task? _heartbeatTask;
    private LanDiscoveryResponder? _lanDiscoveryResponder;

    /// <summary>
    ///     Last sent image/video (header + complete file), for newly connecting clients.
    ///     Sounds DELIBERATELY don't register here - they aren't a "currently displayed" medium that
    ///     a late-joining client would need to wait for (see PublishMediaAsync).
    /// </summary>
    private (MediaHeaderDto Header, byte[] FileBytes)? _lastMedia;

    private TcpListener? _listener;

    /// <summary>
    ///     The map currently open to players, if any - cached so a newly connecting/reconnecting
    ///     client gets a full resync (floor images + current fog) instead of an incremental
    ///     replay, per the "never partially revealed/hidden by accident" requirement. Updated
    ///     in place as fog changes (PublishMapFogUpdateAsync/PublishMapFogResetAsync) so the
    ///     cache always reflects exactly what clients currently see.
    /// </summary>
    private OpenMapState? _openMap;

    public TcpPlayerServerService(Func<SessionSnapshotParams> snapshotProvider,
        Func<ClockHeartbeatParams> clockStateProvider,
        Func<string?>? connectionPinProvider = null)
    {
        _snapshotProvider = snapshotProvider;
        _clockStateProvider = clockStateProvider;
        _connectionPinProvider = connectionPinProvider ?? (() => null);
    }

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

    /// <summary>A player double-clicked the map, pinging the GM (background thread) - see RpcMethods.MapPingFromPlayer.</summary>
    public event Action<Guid, double, double>? ClientReportedMapPing;

    /// <summary>
    ///     A player Shift+left-drew a freehand annotation stroke (background thread) - see
    ///     RpcMethods.MapAnnotationFromPlayer. Carries the originating player's ClientId so the
    ///     GM's own views can render it with the same color/tag every other player will see (see
    ///     PainterTagHelper) - this event fires for the Host's own local render, the broadcast to
    ///     other players is handled separately (see PublishMapAnnotationBroadcastAsync).
    /// </summary>
    public event Action<Guid, IReadOnlyList<AnnotationPoint>, string>? ClientReportedMapAnnotation;

    /// <summary>
    ///     A client reports that the currently playing music track has finished (background
    ///     thread) - see RpcMethods.MusicTrackEnded.
    /// </summary>
    public event Action<string>? ClientReportedMusicTrackEnded;

    /// <summary>
    ///     Fires (with the RemoteEndpoint) when the GM turns Sound routing off for a
    ///     connected client - this service has no knowledge of which sounds are currently
    ///     playing (MainWindowViewModel.ActivePlayingSounds does), so it just raises this and
    ///     leaves "send a targeted stop per active sound" to the subscriber.
    /// </summary>
    public event Action<string>? ClientSoundRoutingDisabled;

    /// <summary>
    ///     Fires (with the RemoteEndpoint) when the GM turns Sound routing back on for a
    ///     connected client - mirrors ClientSoundRoutingDisabled: this service doesn't track
    ///     which sounds are currently playing, so it leaves "resend each active sound to this
    ///     client" to the subscriber (via PublishMediaToClientAsync), so a re-enabled window
    ///     doesn't stay silent until the next brand-new sound is triggered.
    /// </summary>
    public event Action<string>? ClientSoundRoutingEnabled;

    /// <summary>
    ///     Starts the TCP listener, and by default the mDNS + LAN-broadcast discovery responders
    ///     alongside it. <paramref name="enableDiscovery" /> lets a caller skip those - the real
    ///     app exposes this as the <c>--no-discovery</c> CLI flag (see Program.cs) for restricted
    ///     networks, and integration tests use it to avoid best-effort UDP broadcast noise/flakiness
    ///     in CI sandboxes that don't need it (tests connect directly by IP:port, never via discovery).
    ///     <paramref name="port" /> of 0 asks the OS for a free ephemeral port instead of a fixed
    ///     one - <see cref="Port" /> is updated to the actual bound port afterward, so a caller
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

        Log.Information(
            "TCP player server started on port {Port} (server name {ServerName}, discovery={EnableDiscovery})",
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

    public Task PublishClockTimeJumpedAsync(GameInstant newGameTime)
    {
        return BroadcastRpcAsync(RpcMethods.ClockTimeJumped,
            new ClockTimeJumpedParams { NewGameTimeSeconds = newGameTime.TotalSeconds });
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

        if (header.Kind != MediaHeaderDto.MediaKindAudio)
            lock (_mediaGate)
            {
                _lastMedia = (header, fileBytes);
            }

        // Image/Video/MapFloor each respect their own GM per-window routing toggle
        // (SetClientImageEnabled/SetClientVideoEnabled/SetClientMapEnabled); Sound/Music (both
        // Kind=MediaKindAudio, distinguished by Layer - see MediaHeaderDto.LayerMusic/LayerSound)
        // respect the separate Music/Sound toggles (see
        // SetClientMusicEnabled/SetClientSoundEnabled). An empty Layer defaults to Sound routing,
        // for back-compat with any header that predates Layer.
        Func<ClientConnection, bool> routingFilter = header.Kind switch
        {
            MediaHeaderDto.MediaKindAudio => header.Layer == MediaHeaderDto.LayerMusic
                ? c => c.MusicEnabled
                : c => c.SoundEnabled,
            MediaHeaderDto.MediaKindVideo => c => c.VideoEnabled,
            MediaHeaderDto.MediaKindMapFloor => c => c.MapEnabled,
            _ => c => c.ImageEnabled
        };

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.Where(routingFilter).ToArray();
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
        string? lastKind;
        lock (_mediaGate)
        {
            lastKind = _lastMedia?.Header.Kind;
            _lastMedia = null;
        }

        Log.Debug("Medium cleared (media.cleared to all clients)");
        return BroadcastRpcAsync(RpcMethods.MediaCleared, RpcEmptyParams.Instance, MediaVisualFilter(lastKind));
    }

    /// <summary>
    ///     Clears the currently displayed medium on exactly one client window, regardless of
    ///     its current Image/Video routing flag - used when the GM turns that routing off for that
    ///     window, so it doesn't keep showing stale content indefinitely (see
    ///     SetClientImageEnabled/SetClientVideoEnabled).
    /// </summary>
    public Task PublishMediaClearToClientAsync(string remoteEndpoint)
    {
        return BroadcastRpcAsync(RpcMethods.MediaCleared, RpcEmptyParams.Instance,
            c => c.RemoteEndpoint == remoteEndpoint);
    }

    /// <summary>
    ///     Picks the Image or Video per-client routing flag matching a cached medium's Kind -
    ///     used for RPCs (media.cleared, gallery retract/highlight/slideshow) that apply to
    ///     "whatever image/video is currently shown/in the gallery" without themselves carrying a
    ///     Kind. Defaults to ImageEnabled when the kind is unknown/null (the common case).
    /// </summary>
    private static Func<ClientConnection, bool> MediaVisualFilter(string? kind)
    {
        return kind == MediaHeaderDto.MediaKindVideo ? c => c.VideoEnabled : c => c.ImageEnabled;
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
    ///     Stops a single sound (by MediaId) on exactly one client window, regardless of its
    ///     current SoundEnabled flag - used when the GM turns Sound routing off for that window,
    ///     so an already-playing sound doesn't keep running there until it happens to end on its
    ///     own (see MainWindowViewModel's ClientSoundRoutingDisabled subscription).
    /// </summary>
    public Task PublishStopSoundToClientAsync(string mediaId, string remoteEndpoint)
    {
        return BroadcastRpcAsync(RpcMethods.MediaStopSound, new MediaStopSoundParams { MediaId = mediaId },
            c => c.RemoteEndpoint == remoteEndpoint);
    }

    /// <summary>
    ///     Distributes a music track (media.begin + chunks, Kind=MediaKindAudio, Layer=LayerMusic)
    ///     to Music-enabled client windows - thin wrapper around PublishMediaAsync (which applies
    ///     the routing filter) kept as its own named method for symmetry with
    ///     PublishMusicStopAsync/PublishMusicSetVolumeAsync. Also caches the track (see
    ///     _currentMusicTrack) so a client that connects mid-track gets caught up on it too,
    ///     instead of hearing nothing until the Host's sequencer happens to advance.
    /// </summary>
    public Task PublishMusicTrackAsync(MediaHeaderDto header, byte[] fileBytes)
    {
        header.Kind = MediaHeaderDto.MediaKindAudio;
        header.Layer = MediaHeaderDto.LayerMusic;
        lock (_mediaGate)
        {
            _currentMusicTrack = (header, fileBytes, DateTime.UtcNow);
        }

        return PublishMediaAsync(header, fileBytes);
    }

    /// <summary>Stops the currently playing music track/playlist on Music-enabled client windows.</summary>
    public Task PublishMusicStopAsync()
    {
        lock (_mediaGate)
        {
            _currentMusicTrack = null;
        }

        return BroadcastRpcAsync(RpcMethods.MusicStop, RpcEmptyParams.Instance, c => c.MusicEnabled);
    }

    /// <summary>
    ///     Adjusts the volume of the currently playing music track live on Music-enabled client windows (0-100).
    ///     Also updates the cached _currentMusicTrack header (a reference type, mutated in place)
    ///     so a client that connects afterward gets caught up at the current volume, not the
    ///     track's original one.
    /// </summary>
    public Task PublishMusicSetVolumeAsync(int volume)
    {
        lock (_mediaGate)
        {
            if (_currentMusicTrack is { } current) current.Header.Volume = volume;
        }

        return BroadcastRpcAsync(RpcMethods.MusicSetVolume, new MusicSetVolumeParams { Volume = volume },
            c => c.MusicEnabled);
    }

    /// <summary>
    ///     Stops music on exactly one client window, regardless of its current MusicEnabled flag -
    ///     used when the GM turns Music routing off for that window, so an already-playing track
    ///     doesn't keep running there. No MediaId is needed (unlike sounds): the client only ever
    ///     plays one music track at a time and stops whatever that is (see
    ///     ClientMainWindowViewModel.StopMusic) - harmless to send even if nothing is playing.
    /// </summary>
    public Task PublishMusicStopToClientAsync(string remoteEndpoint)
    {
        return BroadcastRpcAsync(RpcMethods.MusicStop, RpcEmptyParams.Instance,
            c => c.RemoteEndpoint == remoteEndpoint);
    }

    /// <summary>
    ///     Tells exactly one client window its current Music/Sound/Image/Video/Map routing
    ///     state (see RpcMethods.AudioRoutingChanged - kept its name despite now carrying more
    ///     flags, to avoid unnecessary wire-protocol churn) - sent right after handshake and again
    ///     on every live toggle, so the client can show a "muted by GM" indicator.
    /// </summary>
    public Task PublishAudioRoutingChangedAsync(string remoteEndpoint, bool musicEnabled, bool soundEnabled,
        bool imageEnabled, bool videoEnabled, bool mapEnabled, bool canAnnotate)
    {
        return BroadcastRpcAsync(RpcMethods.AudioRoutingChanged,
            new DataRoutingChangedParams
            {
                MusicEnabled = musicEnabled, SoundEnabled = soundEnabled, ImageEnabled = imageEnabled,
                VideoEnabled = videoEnabled, MapEnabled = mapEnabled, CanAnnotate = canAnnotate
            },
            c => c.RemoteEndpoint == remoteEndpoint);
    }

    /// <summary>
    ///     Removes an image/video from the gallery on all clients specifically (by MediaId) -
    ///     the gallery can mix Images and Videos, so this reaches clients with either enabled
    ///     rather than a single Kind-specific flag.
    /// </summary>
    public Task PublishRetractAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaRetract, new MediaRetractParams { MediaId = mediaId },
            c => c.ImageEnabled || c.VideoEnabled);
    }

    /// <summary>GM "highlights": all clients jump (locally, non-blocking) to this gallery item.</summary>
    public Task PublishHighlightAsync(string mediaId)
    {
        return BroadcastRpcAsync(RpcMethods.MediaHighlight, new MediaHighlightParams { MediaId = mediaId },
            c => c.ImageEnabled || c.VideoEnabled);
    }

    /// <summary>Sets the automatic advance time per image on all clients (seconds, 0 = manual).</summary>
    public Task PublishSlideshowIntervalAsync(double seconds)
    {
        return BroadcastRpcAsync(RpcMethods.MediaSlideshowInterval,
            new MediaSlideshowIntervalParams { Seconds = seconds }, c => c.ImageEnabled || c.VideoEnabled);
    }

    /// <summary>
    ///     Opens a map to all connected clients: streams each floor's image (chunk pipeline,
    ///     Kind=MediaKindMapFloor) followed by the map.show metadata (fog masks base64-encoded).
    ///     Caches the open state so later-connecting clients get the same full resync.
    /// </summary>
    public async Task PublishMapShowAsync(Guid mapId, string mapName, List<OpenMapFloor> floors,
        List<MapTokenSnapshotDto> tokens, List<MapLineSnapshotDto>? lines = null)
    {
        if (!IsRunning) return;

        lines ??= [];

        lock (_mediaGate)
        {
            _openMap = new OpenMapState(mapId, mapName, floors, tokens.ToList(), lines.ToList());
        }

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = _clients.Where(c => c.MapEnabled).ToArray();
        }

        Log.Information("Map opened: {MapName} ({FloorCount} floors, {TokenCount} tokens) to {ClientCount} client(s)",
            mapName, floors.Count, tokens.Count, clients.Length);

        await _mediaSendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in clients)
                await SendMapShowToClientAsync(client, mapId, mapName, floors, tokens, lines).ConfigureAwait(false);
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

        return BroadcastRpcAsync(RpcMethods.MapFogUpdate, new MapFogUpdateParams { FloorId = floorId, Cells = cells },
            c => c.MapEnabled);
    }

    /// <summary>Resets one floor's live fog back to its starting template on all clients.</summary>
    public Task PublishMapFogResetAsync(Guid floorId)
    {
        lock (_mediaGate)
        {
            var floor = _openMap?.Floors.FirstOrDefault(f => f.FloorId == floorId);
            if (floor is not null) floor.CurrentFog = floor.StartingFog.Clone();
        }

        return BroadcastRpcAsync(RpcMethods.MapFogReset, new MapFogResetParams { FloorId = floorId },
            c => c.MapEnabled);
    }

    /// <summary>Closes the currently open map on all clients; they return to the previous gallery display.</summary>
    public Task PublishMapHideAsync()
    {
        lock (_mediaGate)
        {
            _openMap = null;
        }

        Log.Debug("Map closed (map.hide to all clients)");
        return BroadcastRpcAsync(RpcMethods.MapHide, RpcEmptyParams.Instance, c => c.MapEnabled);
    }

    /// <summary>
    ///     Closes the currently open map on exactly one client window, regardless of its
    ///     current MapEnabled flag - used when the GM turns Map routing off for that window
    ///     (see SetClientMapEnabled), so it doesn't keep showing a stale map indefinitely.
    /// </summary>
    public Task PublishMapHideToClientAsync(string remoteEndpoint)
    {
        return BroadcastRpcAsync(RpcMethods.MapHide, RpcEmptyParams.Instance,
            c => c.RemoteEndpoint == remoteEndpoint);
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
        }, c => c.MapEnabled);
    }

    public Task PublishMapAutoZoomChangedAsync(bool enabled, double zoomLevel)
    {
        return BroadcastRpcAsync(RpcMethods.MapAutoZoomChanged,
            new MapAutoZoomChangedParams { Enabled = enabled, ZoomLevel = zoomLevel }, c => c.MapEnabled);
    }

    /// <summary>
    ///     Adds/fully updates one map token - see RpcMethods.MapTokenUpsert's doc comment for why
    ///     there's no separate "move" variant. Updates the cached snapshot list (replacing any
    ///     existing entry with the same Id) so a later-connecting client's map.show resync includes
    ///     it, exactly like PublishMapFogUpdateAsync keeps _openMap's cached fog current.
    /// </summary>
    public Task PublishMapTokenUpsertAsync(MapTokenSnapshotDto token)
    {
        lock (_mediaGate)
        {
            if (_openMap is null) return Task.CompletedTask;

            var index = _openMap.Tokens.FindIndex(t => t.Id == token.Id);
            if (index >= 0) _openMap.Tokens[index] = token;
            else _openMap.Tokens.Add(token);
        }

        return BroadcastRpcAsync(RpcMethods.MapTokenUpsert, token, c => c.MapEnabled);
    }

    /// <summary>See RpcMethods.MapTokenRemove's doc comment - also covers "no longer visible".</summary>
    public Task PublishMapTokenRemoveAsync(Guid tokenId)
    {
        lock (_mediaGate)
        {
            _openMap?.Tokens.RemoveAll(t => t.Id == tokenId);
        }

        return BroadcastRpcAsync(RpcMethods.MapTokenRemove, new MapTokenRemoveParams { TokenId = tokenId },
            c => c.MapEnabled);
    }

    /// <summary>
    ///     GM double-clicked their own map - broadcast to every connected player. Ephemeral, no
    ///     cached state to keep for a later map.show resync (see RpcMethods.MapPing).
    /// </summary>
    public Task PublishMapPingAsync(Guid floorId, double x, double y)
    {
        return BroadcastRpcAsync(RpcMethods.MapPing, new MapPingParams { FloorId = floorId, X = x, Y = y },
            c => c.MapEnabled);
    }

    /// <summary>
    ///     Adds/fully updates one SemiPermanent/Permanent map line - caller (MainWindowViewModel)
    ///     is responsible for never calling this for a HiddenUntilRevealed=true line, since unlike
    ///     tokens there's no per-client filtering step here; a hidden line simply never reaches
    ///     this method until the GM un-hides it. Updates the cached snapshot list (replacing any
    ///     existing entry with the same Id) so a later-connecting client's map.show resync
    ///     includes it, exactly like PublishMapTokenUpsertAsync.
    /// </summary>
    public Task PublishMapLineUpsertAsync(MapLineSnapshotDto line)
    {
        lock (_mediaGate)
        {
            if (_openMap is null) return Task.CompletedTask;

            var index = _openMap.Lines.FindIndex(l => l.Id == line.Id);
            if (index >= 0) _openMap.Lines[index] = line;
            else _openMap.Lines.Add(line);
        }

        return BroadcastRpcAsync(RpcMethods.MapLineUpsert, line, c => c.MapEnabled);
    }

    /// <summary>One specific line is gone (erased, or its SemiPermanent timer expired) - see RpcMethods.MapLineRemove.</summary>
    public Task PublishMapLineRemoveAsync(Guid floorId, Guid lineId)
    {
        lock (_mediaGate)
        {
            _openMap?.Lines.RemoveAll(l => l.Id == lineId);
        }

        return BroadcastRpcAsync(RpcMethods.MapLineRemove, new MapLineRemoveParams { FloorId = floorId, LineId = lineId },
            c => c.MapEnabled);
    }

    /// <summary>GM's "Erase all" for one floor - see RpcMethods.MapLineClearAll.</summary>
    public Task PublishMapLineClearAllAsync(Guid floorId)
    {
        lock (_mediaGate)
        {
            _openMap?.Lines.RemoveAll(l => l.FloorId == floorId);
        }

        return BroadcastRpcAsync(RpcMethods.MapLineClearAll, new MapLineClearAllParams { FloorId = floorId },
            c => c.MapEnabled);
    }

    /// <summary>
    ///     Relays a just-received player annotation stroke to every OTHER connected player - the
    ///     originating connection already rendered its own local echo the instant it finished
    ///     drawing, so it's excluded here to avoid a redundant second render of the identical
    ///     stroke. See RpcMethods.MapAnnotationBroadcast's doc comment.
    /// </summary>
    private Task PublishMapAnnotationBroadcastAsync(Guid floorId, IReadOnlyList<AnnotationPoint> points,
        string originClientId, ClientConnection originConnection)
    {
        return BroadcastRpcAsync(RpcMethods.MapAnnotationBroadcast,
            new MapAnnotationBroadcastParams { FloorId = floorId, Points = points.ToList(), ClientId = originClientId },
            c => c.MapEnabled && !ReferenceEquals(c, originConnection));
    }

    /// <summary>
    ///     Broadcasts the GM's own Temporary-tier Draw-tool stroke to every connected player - the
    ///     GM isn't a ClientConnection (unlike a player's own stroke), so unlike
    ///     PublishMapAnnotationBroadcastAsync there's no originating connection to exclude.
    ///     clientId is a PainterTagHelper GM sentinel (see MainWindowViewModel.BroadcastGmAnnotationAsync),
    ///     not a real player's ClientId.
    /// </summary>
    public Task PublishGmAnnotationAsync(Guid floorId, IReadOnlyList<AnnotationPoint> points, string clientId)
    {
        return BroadcastRpcAsync(RpcMethods.MapAnnotationBroadcast,
            new MapAnnotationBroadcastParams { FloorId = floorId, Points = points.ToList(), ClientId = clientId },
            c => c.MapEnabled);
    }

    private async Task SendMapShowToClientAsync(ClientConnection client, Guid mapId, string mapName,
        List<OpenMapFloor> floors, List<MapTokenSnapshotDto> tokens, List<MapLineSnapshotDto>? lines = null)
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
            }).ToList(),
            Tokens = tokens,
            Lines = lines ?? []
        };

        var payload = RpcMessage.Serialize(RpcMethods.MapShow, showParams);
        if (!await client.TryWriteFrameAsync(NetworkFrame.TypeRpc, payload).ConfigureAwait(false))
        {
            Log.Warning("map.show to {Client} failed - connection is being closed", client);
            RemoveClient(client);
        }
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

        connection.ClientId = hello.ClientId;
        var routingPreference = ThemeSettingsService.LoadClientRoutingPreference(hello.ClientId);
        if (routingPreference is not null)
        {
            connection.MusicEnabled = routingPreference.MusicEnabled;
            connection.SoundEnabled = routingPreference.SoundEnabled;
            connection.ImageEnabled = routingPreference.ImageEnabled;
            connection.VideoEnabled = routingPreference.VideoEnabled;
            connection.MapEnabled = routingPreference.MapEnabled;
            connection.CanAnnotate = routingPreference.CanAnnotate;
        }

        connection.IsAccepted = true;
        lock (_gate)
        {
            _clients.Add(connection);
        }

        NotifyClientsChanged();
        Log.Information("Client connected: {Client}", connection);

        // Reflects any restored (non-default) routing preference right away, so the client's
        // "muted by GM" indicator is correct from the start rather than only updating on the
        // next live toggle.
        _ = PublishAudioRoutingChangedAsync(connection.RemoteEndpoint, connection.MusicEnabled,
            connection.SoundEnabled, connection.ImageEnabled, connection.VideoEnabled, connection.MapEnabled,
            connection.CanAnnotate);

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

        // Image/video/map catch-up is each gated on its own routing flag (mirroring how
        // SendCurrentMusicTrackToClientAsync below self-gates on MusicEnabled) - a client whose
        // persisted routing preference for that kind is off shouldn't receive it here either.
        (MediaHeaderDto Header, byte[] FileBytes)? cachedMedia;
        lock (_mediaGate)
        {
            cachedMedia = _lastMedia;
        }

        if (cachedMedia is { } media &&
            (media.Header.Kind == MediaHeaderDto.MediaKindVideo ? connection.VideoEnabled : connection.ImageEnabled))
            await SendMediaToClientAsync(connection, media.Header, media.FileBytes).ConfigureAwait(false);

        if (connection.MapEnabled)
        {
            (OpenMapState State, List<MapTokenSnapshotDto> Tokens, List<MapLineSnapshotDto> Lines)? openMap;
            lock (_mediaGate)
            {
                openMap = _openMap is { } state ? (state, state.Tokens.ToList(), state.Lines.ToList()) : null;
            }

            // Full resync (all floor images + current fog), never an incremental replay - a
            // reconnecting client must never end up with a fog state that's partially stale.
            if (openMap is { } mapSnapshot)
                await SendMapShowToClientAsync(connection, mapSnapshot.State.MapId, mapSnapshot.State.MapName,
                        mapSnapshot.State.Floors, mapSnapshot.Tokens, mapSnapshot.Lines)
                    .ConfigureAwait(false);
        }

        await SendCurrentMusicTrackToClientAsync(connection).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends the currently playing music track (if any) to exactly one client, estimating how
    ///     far into the track playback already is (wall-clock elapsed since it started) so the
    ///     client can seek there instead of restarting from 0. Used both to catch up a newly
    ///     connecting client (SendCatchUpAsync) and to resume music for a client whose Music
    ///     routing was just turned back on (SetClientMusicEnabled) - re-enabling shouldn't require
    ///     waiting for the Host's sequencer to happen to advance to the next track. Respects the
    ///     connection's current MusicEnabled flag, so a caller can always call this unconditionally.
    /// </summary>
    private async Task SendCurrentMusicTrackToClientAsync(ClientConnection connection)
    {
        (MediaHeaderDto Header, byte[] FileBytes, DateTime StartedAtUtc)? currentMusicTrack;
        lock (_mediaGate)
        {
            currentMusicTrack = _currentMusicTrack;
        }

        if (currentMusicTrack is not { } music || !connection.MusicEnabled) return;

        var elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - music.StartedAtUtc).TotalMilliseconds);
        var catchUpHeader = music.Header.CloneWithSeek(elapsedMs);
        await SendMediaToClientAsync(connection, catchUpHeader, music.FileBytes).ConfigureAwait(false);
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
                case RpcMethods.MapPingFromPlayer:
                    var ping = raw.GetParams<MapPingParams>();
                    if (ping is not null)
                    {
                        Log.Debug("Client pinged the map at floor {FloorId} ({X}, {Y})", ping.FloorId, ping.X,
                            ping.Y);
                        ClientReportedMapPing?.Invoke(ping.FloorId, ping.X, ping.Y);
                    }

                    break;
                case RpcMethods.MapAnnotationFromPlayer:
                    var annotation = raw.GetParams<MapAnnotationParams>();
                    if (annotation is not null && !connection.CanAnnotate)
                    {
                        Log.Debug("Ignored map annotation from {Client} - painting is disabled for this window",
                            connection);
                    }
                    else if (annotation is not null)
                    {
                        Log.Debug("Client drew a map annotation on floor {FloorId} ({PointCount} points)",
                            annotation.FloorId, annotation.Points.Count);
                        ClientReportedMapAnnotation?.Invoke(annotation.FloorId, annotation.Points, connection.ClientId);
                        _ = PublishMapAnnotationBroadcastAsync(annotation.FloorId, annotation.Points,
                            connection.ClientId, connection);
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

    /// <summary>
    ///     Toggles whether this client window receives Music broadcasts (GM per-window
    ///     routing control) - persisted by ClientId (see ThemeSettingsService.SaveClientRoutingPreference)
    ///     so it survives a reconnect. Also tells the client live (audio.routingChanged); disabling
    ///     stops whatever it's currently playing (a toggle-off shouldn't wait for the Host's own
    ///     playlist sequencer to naturally advance/end), and re-enabling resumes the currently
    ///     playing track for it (with an estimated seek) rather than leaving it silent until the
    ///     sequencer happens to advance to the next track.
    /// </summary>
    public void SetClientMusicEnabled(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.MusicEnabled = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);
        if (enabled) _ = SendCurrentMusicTrackToClientAsync(target);
        else _ = PublishMusicStopToClientAsync(remoteEndpoint);
    }

    /// <summary>
    ///     Toggles whether this client window receives Sound broadcasts (GM per-window
    ///     routing control) - persisted by ClientId so it survives a reconnect. Also tells the
    ///     client live (audio.routingChanged); disabling raises ClientSoundRoutingDisabled so
    ///     whoever tracks currently-active sounds (MainWindowViewModel) can stop them on this one
    ///     client specifically, and re-enabling raises ClientSoundRoutingEnabled so it can resend
    ///     them instead - either way the client isn't left in a stale state until the next brand-
    ///     new sound happens to play.
    /// </summary>
    public void SetClientSoundEnabled(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.SoundEnabled = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);
        if (enabled) ClientSoundRoutingEnabled?.Invoke(remoteEndpoint);
        else ClientSoundRoutingDisabled?.Invoke(remoteEndpoint);
    }

    /// <summary>
    ///     Toggles whether this client window receives Image broadcasts (GM per-window
    ///     routing control, e.g. for a multi-monitor setup where only one window shows images) -
    ///     persisted by ClientId so it survives a reconnect. Enabling resends the currently
    ///     displayed medium to just this client if it's an image; disabling clears it on just this
    ///     client so it doesn't keep showing stale content.
    /// </summary>
    public void SetClientImageEnabled(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.ImageEnabled = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);
        ResendOrClearCachedMedia(target, remoteEndpoint, enabled, MediaHeaderDto.MediaKindImage);
    }

    /// <summary>
    ///     Toggles whether this client window receives Video broadcasts (GM per-window
    ///     routing control) - persisted by ClientId so it survives a reconnect. Enabling resends
    ///     the currently displayed medium to just this client if it's a video; disabling clears it
    ///     on just this client so it doesn't keep showing stale content.
    /// </summary>
    public void SetClientVideoEnabled(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.VideoEnabled = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);
        ResendOrClearCachedMedia(target, remoteEndpoint, enabled, MediaHeaderDto.MediaKindVideo);
    }

    /// <summary>
    ///     Toggles whether this client window receives Map broadcasts (GM per-window routing
    ///     control) - persisted by ClientId so it survives a reconnect. Enabling resends the
    ///     currently open map to just this client, disabling hides it on just this client so it
    ///     doesn't keep showing a stale map.
    /// </summary>
    public void SetClientMapEnabled(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.MapEnabled = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);

        if (enabled)
        {
            (OpenMapState State, List<MapTokenSnapshotDto> Tokens, List<MapLineSnapshotDto> Lines)? openMap;
            lock (_mediaGate)
            {
                openMap = _openMap is { } state ? (state, state.Tokens.ToList(), state.Lines.ToList()) : null;
            }

            if (openMap is { } mapSnapshot)
                _ = SendMapShowToClientAsync(target, mapSnapshot.State.MapId, mapSnapshot.State.MapName,
                    mapSnapshot.State.Floors, mapSnapshot.Tokens, mapSnapshot.Lines);
        }
        else
        {
            _ = PublishMapHideToClientAsync(remoteEndpoint);
        }
    }

    /// <summary>
    ///     Shared tail of SetClientImageEnabled/SetClientVideoEnabled: resends the cached
    ///     current medium to this one client if enabling and it matches the given kind, or clears
    ///     it on this one client if disabling (harmless if nothing of that kind is currently
    ///     shown).
    /// </summary>
    private void ResendOrClearCachedMedia(ClientConnection target, string remoteEndpoint, bool enabled, string kind)
    {
        if (enabled)
        {
            (MediaHeaderDto Header, byte[] FileBytes)? cachedMedia;
            lock (_mediaGate)
            {
                cachedMedia = _lastMedia;
            }

            if (cachedMedia is { } media && media.Header.Kind == kind)
                _ = PublishMediaToClientAsync(media.Header, media.FileBytes, remoteEndpoint);
        }
        else
        {
            _ = PublishMediaClearToClientAsync(remoteEndpoint);
        }
    }

    private static void SaveRoutingPreference(ClientConnection target)
    {
        ThemeSettingsService.SaveClientRoutingPreference(target.ClientId, target.MusicEnabled, target.SoundEnabled,
            target.ImageEnabled, target.VideoEnabled, target.MapEnabled, target.CanAnnotate);
    }

    private Task PublishRoutingChanged(ClientConnection target)
    {
        return PublishAudioRoutingChangedAsync(target.RemoteEndpoint, target.MusicEnabled, target.SoundEnabled,
            target.ImageEnabled, target.VideoEnabled, target.MapEnabled, target.CanAnnotate);
    }

    /// <summary>
    ///     Toggles whether this client window is allowed to draw map annotation strokes (GM
    ///     per-window routing control) - persisted by ClientId so it survives a reconnect. Unlike
    ///     the Map/Image/Video toggles this doesn't gate anything sent TO the client; the Host
    ///     simply drops any map.annotationFromPlayer from a client with this disabled (see
    ///     HandleClientRpcFrame) - the notification just lets the client also grey out/skip the
    ///     Shift-drag gesture locally instead of drawing a stroke that then silently goes nowhere.
    /// </summary>
    public void SetClientCanAnnotate(string remoteEndpoint, bool enabled)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
            if (target is null) return;
            target.CanAnnotate = enabled;
        }

        NotifyClientsChanged();
        SaveRoutingPreference(target);
        _ = PublishRoutingChanged(target);
    }

    /// <summary>
    ///     Sends an already-known medium to exactly one client by RemoteEndpoint - used to resend
    ///     currently active sounds to a client whose Sound routing was just turned back on (see
    ///     MainWindowViewModel's ClientSoundRoutingEnabled subscription), since this service
    ///     itself doesn't track which sounds are currently playing.
    /// </summary>
    public async Task PublishMediaToClientAsync(MediaHeaderDto header, byte[] fileBytes, string remoteEndpoint)
    {
        ClientConnection? target;
        lock (_gate)
        {
            target = _clients.Find(c => c.RemoteEndpoint == remoteEndpoint);
        }

        if (target is not null) await SendMediaToClientAsync(target, header, fileBytes).ConfigureAwait(false);
    }

    private void NotifyClientsChanged()
    {
        List<ConnectedClientInfo> snapshot;
        lock (_gate)
        {
            snapshot = _clients
                .Select(c => new ConnectedClientInfo(c.RemoteEndpoint, c.ConnectedAtUtc, c.MusicEnabled,
                    c.SoundEnabled, c.ImageEnabled, c.VideoEnabled, c.MapEnabled, c.CanAnnotate))
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
            _currentMusicTrack = null;
        }

        _cts?.Dispose();
        _cts = null;
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

    private sealed class OpenMapState(Guid mapId, string mapName, List<OpenMapFloor> floors,
        List<MapTokenSnapshotDto> tokens, List<MapLineSnapshotDto>? lines = null)
    {
        public Guid MapId { get; } = mapId;
        public string MapName { get; } = mapName;
        public List<OpenMapFloor> Floors { get; } = floors;

        /// <summary>
        ///     Every token currently visible to players (already filtered/resolved by the
        ///     caller) - kept in sync by PublishMapTokenUpsertAsync/PublishMapTokenRemoveAsync so a
        ///     later-connecting client's resync (SendCatchUpAsync) sees the same state already-
        ///     connected clients do.
        /// </summary>
        public List<MapTokenSnapshotDto> Tokens { get; } = tokens;

        /// <summary>
        ///     Every currently-active SemiPermanent/Permanent line visible to players (GM already
        ///     excluded any HiddenUntilRevealed=true one before it ever reached PublishMapLineUpsertAsync)
        ///     - kept in sync the same way Tokens is, for the same resync reason.
        /// </summary>
        public List<MapLineSnapshotDto> Lines { get; } = lines ?? [];
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

        /// <summary>
        ///     Whether this window receives Music/Sound broadcasts, set by the GM via
        ///     SetClientMusicEnabled/SetClientSoundEnabled - defaults to true for a never-seen
        ///     ClientId, otherwise restored from ThemeSettingsService.LoadClientAudioPreference
        ///     in PerformHandshakeAsync so a reconnecting window keeps its previous routing.
        /// </summary>
        public bool MusicEnabled { get; set; } = true;

        public bool SoundEnabled { get; set; } = true;

        /// <summary>
        ///     Whether this window receives Image/Video/Map broadcasts respectively, set by
        ///     the GM via SetClientImageEnabled/SetClientVideoEnabled/SetClientMapEnabled -
        ///     defaults to true for a never-seen ClientId, otherwise restored from
        ///     ThemeSettingsService.LoadClientRoutingPreference in PerformHandshakeAsync so a
        ///     reconnecting window keeps its previous routing.
        /// </summary>
        public bool ImageEnabled { get; set; } = true;

        public bool VideoEnabled { get; set; } = true;
        public bool MapEnabled { get; set; } = true;

        /// <summary>Whether this window is allowed to draw map annotation strokes, set by the GM via SetClientCanAnnotate.</summary>
        public bool CanAnnotate { get; set; } = true;

        /// <summary>
        ///     Stable per-installation id sent in session.hello (see
        ///     SessionHelloParams.ClientId) - empty until the handshake completes. Used to key
        ///     persisted Music/Sound routing preferences, since RemoteEndpoint changes every
        ///     reconnect (ephemeral TCP port).
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

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