using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using RpgTimeTracker.PlayerClient.Network;
using RpgTimeTracker.PlayerClient.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.PlayerClient.ViewModels;

public partial class ClientMainWindowViewModel : ObservableObject, IDisposable, IPlayerDisplayContext
{
    // ==================== Sounds: independent of the image/video display slot ====================
    // Sounds NEVER run through CurrentMediaKind/MediaPlayer (video field) - no window, no
    // replacement of a currently shown image/video, and any number in parallel: each sound
    // gets its own, short-lived MediaPlayer instead of the shared video player.

    private readonly Dictionary<string, MediaPlayer> _activeSoundPlayers = new();

    private readonly List<CalendarEntryDefinition> _calendarEntries = new();
    private readonly PlayerTcpClientService _client = new();
    private readonly MdnsDiscoveryService _discovery = new();

    private readonly Dictionary<Guid, RemoteTimelineEntry> _entries = new();
    private readonly Dictionary<Guid, FogMask> _floorStartingFogs = new();

    private readonly List<GalleryEntry> _gallery = new();

    /// <summary>
    ///     Runs purely locally (not network-driven): the server sends time only on jumps,
    ///     in between this instance ticks on its own with the last known speed
    ///     - identical derivation logic as on the host, see RpgTimeTracker.Shared.Services.
    /// </summary>
    private readonly GameClockService _localClock = new(CalendarService.Active.FromCalendarDate(1, 0, 1, 12, 0, 0));

    // ==================== Map display (fog of war) ====================
    // Replaces the gallery/current-medium display while open (mutually exclusive, matching how
    // event-trigger media temporarily takes over). Rendering/floor-navigation logic lives in the
    // shared MapDisplayViewModel (also used by the Host's local player-window preview,
    // MainWindowViewModel.MapDisplay) - only the network-specific plumbing (temp image paths,
    // base64 fog decoding) stays here.

    private readonly Dictionary<Guid, string> _pendingFloorImagePaths = new();

    /// <summary>
    ///     Remaining plays per MediaId (-1 = endless/loop, otherwise counts down on each
    ///     natural end - see OnSoundEndReached).
    /// </summary>
    private readonly Dictionary<string, int> _soundRepeatsRemaining = new();

    [ObservableProperty]
    private string _connectionStatus = LocalizationService.Get("PlayerTcpClientService.Status.NotConnected");

    private int _currentGalleryIndex = -1;
    [ObservableProperty] private string _currentGameTimeText = "—";
    [ObservableProperty] private Bitmap? _currentMediaBitmap;
    [ObservableProperty] private string? _currentMediaFileName;
    private string? _currentMediaId;

    [ObservableProperty] private MediaKind _currentMediaKind = MediaKind.None;
    private bool _currentMediaLoop;
    [ObservableProperty] private string _currentTheme = "Shadowrun";
    private string? _currentVideoTempPath;

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private bool _isImageMuted;
    [ObservableProperty] private bool _isMapMuted;

    /// <summary>
    ///     Whether the GM has turned Music/Sound/Image/Video/Map routing off for this window
    ///     (see RpcMethods.AudioRoutingChanged) - purely informational, shown as a small indicator
    ///     so the player understands why they hear/see nothing instead of assuming something is
    ///     broken.
    /// </summary>
    [ObservableProperty] private bool _isMusicMuted;

    private bool _isShowingEventMedia;

    [ObservableProperty] private bool _isSoundMuted;
    [ObservableProperty] private bool _isVideoMuted;
    [ObservableProperty] private string? _mediaErrorMessage;
    [ObservableProperty] private bool _mediaFullscreen;
    [ObservableProperty] private MediaPlayer? _mediaPlayer;

    // ==================== Music: independent playback channel, one track at a time ====================
    // Separate from _activeSoundPlayers (sound effects) and MediaPlayer (video/current-medium) -
    // music is driven by the Host's playlist sequencer, never by this client, so it only ever
    // plays/stops/reports end for whatever single track the Host currently sent.
    private MediaPlayer? _musicPlayer;
    private string? _musicTempPath;
    [ObservableProperty] private string _pin = string.Empty;
    private bool _playbackStartedReported;
    [ObservableProperty] private GameInstant _playerCalendarMonth;
    [ObservableProperty] private string _playerCalendarMonthLabel = string.Empty;
    [ObservableProperty] private GameInstant _playerCalendarSelectedDate;
    [ObservableProperty] private string _playerCalendarSelectedDateLabel = string.Empty;

    [ObservableProperty] private string _playerHeaderSubtitle =
        LocalizationService.Get("ClientMainWindowViewModel.Defaults.PlayerHeaderSubtitle");

    [ObservableProperty] private string _playerHeaderTitle =
        LocalizationService.Get("MainWindowViewModel.Defaults.PlayerHeaderTitle");

    [ObservableProperty] private int _port = 48550;
    [ObservableProperty] private string _selectedLanguageOption = "English";
    [ObservableProperty] private DiscoveredRpgTimeTrackerServer? _selectedServer;
    [ObservableProperty] private bool _showConnectionSettings = true;
    [ObservableProperty] private bool _showPlayerCalendarView;
    private DispatcherTimer? _slideshowTimer;
    [ObservableProperty] private string _speedMultiplierDisplay = "—";

    public ClientMainWindowViewModel()
    {
        var clientSettings = ClientSettingsService.LoadSettings();
        _mediaFullscreen = clientSettings.MediaFullscreen;
        _pin = clientSettings.Pin;
        _selectedLanguageOption = clientSettings.Language == "de" ? "Deutsch" : "English";

        _localClock.Tick += (newTime, gameDelta) => OnLocalClockTick(newTime, gameDelta);

        _client.SessionSnapshotReceived += s => Dispatcher.UIThread.Post(() => OnSessionSnapshot(s));
        _client.ClockStarted += () => Dispatcher.UIThread.Post(() => _localClock.Start());
        _client.ClockStopped += () => Dispatcher.UIThread.Post(() => _localClock.Pause());
        _client.ClockSpeedChanged += speed => Dispatcher.UIThread.Post(() =>
        {
            _localClock.SpeedMultiplier = speed <= 0 ? 1.0 : speed;
            SpeedMultiplierDisplay = _localClock.SpeedMultiplier.ToString("0.0", LocalizationService.Culture) + "×";
        });
        _client.ClockTimeJumped += newTime => Dispatcher.UIThread.Post(() => _localClock.SetTime(newTime));
        _client.ClockHeartbeatReceived += (gameTime, speed, isRunning) =>
            Dispatcher.UIThread.Post(() => OnClockHeartbeat(gameTime, speed, isRunning));
        _client.HeaderChanged += (title, subtitle) => Dispatcher.UIThread.Post(() =>
        {
            PlayerHeaderTitle = Limit(title, 120);
            PlayerHeaderSubtitle = Limit(subtitle, 180);
        });
        _client.ThemeChanged += theme => Dispatcher.UIThread.Post(() => ApplyTheme(theme));
        _client.TimelineItemUpserted += dto => Dispatcher.UIThread.Post(() => ApplyUpsert(dto));
        _client.TimelineItemRemoved += id => Dispatcher.UIThread.Post(() => ApplyRemove(id));
        _client.DisplayFullscreenRequested += fullscreen =>
            Dispatcher.UIThread.Post(() => RemoteFullscreenRequested?.Invoke(fullscreen));
        _client.MediaBeginReceived += (header, path) => Dispatcher.UIThread.Post(() => OnMediaBegin(header, path));
        _client.MediaCompleted += (header, path) => Dispatcher.UIThread.Post(() => OnMediaCompleted(header, path));
        _client.MediaCleared += () => Dispatcher.UIThread.Post(OnMediaCleared);
        _client.SoundStopRequested += mediaId => Dispatcher.UIThread.Post(() => StopSound(mediaId));
        _client.SoundVolumeChangeRequested += (mediaId, volume) =>
            Dispatcher.UIThread.Post(() => ApplySoundVolume(mediaId, volume));
        _client.MediaRetractRequested += mediaId => Dispatcher.UIThread.Post(() => RetractGalleryItem(mediaId));
        _client.MediaHighlightRequested += mediaId => Dispatcher.UIThread.Post(() => ShowGalleryItem(mediaId));
        _client.SlideshowIntervalChanged += seconds => Dispatcher.UIThread.Post(() => ApplySlideshowInterval(seconds));
        _client.MapFloorImageReceived += (floorId, path) =>
            Dispatcher.UIThread.Post(() => OnMapFloorImageReceived(floorId, path));
        _client.MapShowReceived += mapShow => Dispatcher.UIThread.Post(() => OnMapShow(mapShow));
        _client.MapFogUpdateReceived += fogUpdate => Dispatcher.UIThread.Post(() => OnMapFogUpdate(fogUpdate));
        _client.MapFogResetReceived += floorId => Dispatcher.UIThread.Post(() => OnMapFogReset(floorId));
        _client.MapHideReceived += () => Dispatcher.UIThread.Post(OnMapHide);
        _client.MapRenderStyleChanged += style => Dispatcher.UIThread.Post(() => MapDisplay.ApplyRenderStyle(
            FogOverlayRenderer.BuildHiddenColor(style.ColorHex, style.OpacityPercent), style.BlurRadius,
            style.BlurEnabled));
        _client.MusicTrackReceived += (header, path) => Dispatcher.UIThread.Post(() => PlayMusicTrack(path, header));
        _client.MusicStopRequested += () => Dispatcher.UIThread.Post(StopMusic);
        _client.MusicVolumeChangeRequested += volume => Dispatcher.UIThread.Post(() => ApplyMusicVolume(volume));
        _client.AudioRoutingChanged += (musicEnabled, soundEnabled, imageEnabled, videoEnabled, mapEnabled) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsMusicMuted = !musicEnabled;
                IsSoundMuted = !soundEnabled;
                IsImageMuted = !imageEnabled;
                IsVideoMuted = !videoEnabled;
                IsMapMuted = !mapEnabled;
            });
        _client.StatusChanged += status => Dispatcher.UIThread.Post(() => ConnectionStatus = status);
        _client.ConnectionStateChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = connected;
            if (connected)
            {
                ShowConnectionSettings = false;
            }
            else
            {
                // Connection lost: pause the local clock, so that remaining times don't keep
                // running "phantom-like" without the server until a new connection re-syncs.
                _localClock.Pause();
                // Immediately stop all still-running sounds - without a host connection the
                // client device should not keep playing uncontrolled (e.g. a looping ambience).
                StopAllSounds();
                StopMusic();
                IsMusicMuted = false;
                IsSoundMuted = false;
                IsImageMuted = false;
                IsVideoMuted = false;
                IsMapMuted = false;
                ClearGallery();
                _calendarEntries.Clear();
                PlayerCalendarDays.Clear();
                PlayerCalendarEntries.Clear();
                ShowPlayerCalendarView = false;
                OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
                OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
            }
        });

        // Refreshes every computed display property that calls LocalizationService.Get() (e.g.
        // ConnectionToggleLabel, MediaFullscreenLabel) - a language switch alone doesn't raise
        // property-changed on its own, since these properties only depend on other observable
        // state, not on the active language. Per-item entries (Items/PlayerCalendarEntries) are
        // NOT refreshed here: their display text is re-derived from the underlying model on
        // every clock tick anyway (see OnLocalClockTick), so they self-correct within about a
        // second without needing an explicit refresh call.
        LocalizationService.LanguageChanged += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
    }

    public bool HasMedia => CurrentMediaKind != MediaKind.None;
    public bool HasImageMedia => CurrentMediaKind == MediaKind.Image;
    public bool HasVideoMedia => CurrentMediaKind == MediaKind.Video;

    public MapDisplayViewModel MapDisplay { get; } = new();

    public ObservableCollection<RemoteTimelineItemViewModel> Items { get; } = new();
    public ObservableCollection<PlayerCalendarDayViewModel> PlayerCalendarDays { get; } = new();
    public ObservableCollection<PlayerCalendarEntryViewModel> PlayerCalendarEntries { get; } = new();

    public ObservableCollection<DiscoveredRpgTimeTrackerServer> DiscoveredServers { get; } = new();

    public string ConnectionToggleLabel => ShowConnectionSettings
        ? LocalizationService.Get("ClientMainWindowViewModel.Connection.HideButton")
        : LocalizationService.Get("ClientMainWindowViewModel.Connection.ShowButton");

    public bool HasDiscoveredServers => DiscoveredServers.Count > 0;
    public bool HasNoDiscoveredServers => DiscoveredServers.Count == 0;

    public string MediaFullscreenLabel => MediaFullscreen
        ? LocalizationService.Get("ClientMainWindowViewModel.Media.FullscreenOff")
        : LocalizationService.Get("ClientMainWindowViewModel.Media.FullscreenOn");

    /// <summary>
    ///     Display names for the language ComboBox; native names on purpose, not translated by LocalizationService
    ///     itself.
    /// </summary>
    public ObservableCollection<string> LanguageOptions { get; } = ["English", "Deutsch"];

    /// <summary>Whether there is anything to clean up at all when the app closes - only then is asking worthwhile.</summary>
    public bool HasTempFilesToClean => _gallery.Count > 0 || _currentVideoTempPath is not null;

    public void Dispose()
    {
        _client.Dispose();
        _localClock.Dispose();
        StopVideo();
        if (MediaPlayer is not null)
        {
            MediaPlayer.LengthChanged -= OnVlcLengthChanged;
            MediaPlayer.EndReached -= OnVlcEndReached;
            MediaPlayer.Dispose();
        }
    }

    public bool HasPlayerVisibleCalendarEntries => _calendarEntries.Count > 0;
    public bool HasPlayerCalendarEntriesForSelectedDate => PlayerCalendarEntries.Count > 0;
    ICommand IPlayerDisplayContext.ShowPlayerTimelineCommand => ShowPlayerTimelineCommand;

    ICommand IPlayerDisplayContext.ShowPlayerCalendarCommand => ShowPlayerCalendarCommand;

    ICommand IPlayerDisplayContext.PreviousPlayerCalendarMonthCommand => PreviousPlayerCalendarMonthCommand;

    ICommand IPlayerDisplayContext.NextPlayerCalendarMonthCommand => NextPlayerCalendarMonthCommand;

    public bool ShowPlayerTimelineView => !ShowPlayerCalendarView;

    /// <summary>
    ///     For PlayerTimelineListView (Shared) - the same collection instance, no copy, so live updates are
    ///     preserved.
    /// </summary>
    IEnumerable IPlayerDisplayContext.TimelineEntries => Items;

    IEnumerable IPlayerDisplayContext.CalendarMonthDays => PlayerCalendarDays;
    IEnumerable IPlayerDisplayContext.CalendarEntries => PlayerCalendarEntries;

    public string SpeedLabel =>
        string.Format(LocalizationService.Get("PlayerHeaderView.SpeedLabel"), SpeedMultiplierDisplay);

    private void OnMapFloorImageReceived(Guid floorId, string tempPath)
    {
        _pendingFloorImagePaths[floorId] = tempPath;

        // Self-heals a floor whose image arrives (or is retried) after ShowMap already ran for
        // this map - e.g. if this event happens to be processed out of order relative to
        // MapShowReceived, the display picks it up as soon as it does show up instead of
        // needing a full reconnect to recover.
        var bitmap = TryDecodeFloorImage(tempPath, floorId);
        if (bitmap is not null) MapDisplay.UpdateFloorImage(floorId, bitmap);
    }

    private static Bitmap? TryDecodeFloorImage(string imagePath, Guid floorId)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            Log.Warning(ex, "Map floor image could not be decoded ({FloorId})", floorId);
            return null;
        }
    }

    private void OnMapShow(MapShowParams mapShow)
    {
        _floorStartingFogs.Clear();
        var floors = new List<MapDisplayFloor>();
        foreach (var floor in mapShow.Floors)
        {
            Bitmap? bitmap = null;
            if (_pendingFloorImagePaths.TryGetValue(floor.FloorId, out var imagePath))
                bitmap = TryDecodeFloorImage(imagePath, floor.FloorId);
            else
                Log.Warning("Map floor image for {FloorName} ({FloorId}) not yet received when map.show arrived - " +
                            "will self-heal once media.begin/chunks for it complete", floor.FloorName, floor.FloorId);

            FogMask? startingFog = null, currentFog = null;
            try
            {
                startingFog = FogMaskSerializer.Deserialize(Convert.FromBase64String(floor.StartingFogBase64));
                currentFog = FogMaskSerializer.Deserialize(Convert.FromBase64String(floor.CurrentFogBase64));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Log.Warning(ex, "Map floor fog could not be decoded ({FloorName})", floor.FloorName);
            }

            if (startingFog is not null) _floorStartingFogs[floor.FloorId] = startingFog;
            floors.Add(new MapDisplayFloor
            {
                FloorId = floor.FloorId,
                Name = floor.FloorName,
                Image = bitmap,
                CurrentFog = currentFog
            });
        }

        _pendingFloorImagePaths.Clear();
        MapDisplay.ShowMap(mapShow.MapName, floors);
        MediaWindowShouldShow?.Invoke();
        Log.Information("Map shown: {MapName} ({FloorCount} floors)", mapShow.MapName, floors.Count);
    }

    private void OnMapFogUpdate(MapFogUpdateParams update)
    {
        MapDisplay.ApplyFogCells(update.FloorId, update.Cells);
    }

    private void OnMapFogReset(Guid floorId)
    {
        if (_floorStartingFogs.TryGetValue(floorId, out var startingFog))
            MapDisplay.ResetFloorFog(floorId, startingFog.Clone());
    }

    private void OnMapHide()
    {
        MapDisplay.HideMap();
        _floorStartingFogs.Clear();
        _pendingFloorImagePaths.Clear();
        MediaWindowShouldHide?.Invoke();
        Log.Information("Map hidden");
    }

    partial void OnSpeedMultiplierDisplayChanged(string value)
    {
        OnPropertyChanged(nameof(SpeedLabel));
    }

    /// <summary>GM requests toggling the display (medium if open, otherwise timeline) to fullscreen.</summary>
    public event Action<bool>? RemoteFullscreenRequested;

    /// <summary>
    ///     Fires unconditionally on every new medium (not only when switching from "no medium"
    ///     to "medium") - a pure HasMedia change check would not trigger for two media of the
    ///     same kind in a row (e.g. image -> image), because the bool value doesn't change.
    ///     This reliably reopens a media window that the player closed in the meantime, the
    ///     next time a medium is sent.
    /// </summary>
    public event Action? MediaWindowShouldShow;

    public event Action? MediaWindowShouldHide;

    partial void OnCurrentMediaKindChanged(MediaKind value)
    {
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasImageMedia));
        OnPropertyChanged(nameof(HasVideoMedia));
    }

    partial void OnMediaFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(MediaFullscreenLabel));
        SaveClientSettings();
    }

    partial void OnPinChanged(string value)
    {
        SaveClientSettings();
    }

    partial void OnSelectedLanguageOptionChanged(string value)
    {
        LocalizationService.Apply(value == "Deutsch" ? "de" : "en");
        SaveClientSettings();
    }

    private void SaveClientSettings()
    {
        ClientSettingsService.SaveSettings(new ClientSettingsService.ClientSettingsDto
        {
            MediaFullscreen = MediaFullscreen,
            Pin = Pin,
            Language = SelectedLanguageOption == "Deutsch" ? "de" : "en"
        });
    }

    partial void OnShowPlayerCalendarViewChanged(bool value)
    {
        if (value && !HasPlayerVisibleCalendarEntries)
        {
            ShowPlayerCalendarView = false;
            return;
        }

        OnPropertyChanged(nameof(ShowPlayerTimelineView));
    }

    [RelayCommand]
    private void ToggleMediaFullscreen()
    {
        MediaFullscreen = !MediaFullscreen;
    }

    [RelayCommand]
    private async Task Connect()
    {
        try
        {
            await _client.ConnectAsync(Host.Trim(), Port, Pin);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Connection to {Host}:{Port} failed", Host, Port);
            IsConnected = false;
            ShowConnectionSettings = true;
            ConnectionStatus = string.Format(
                LocalizationService.Get("ClientMainWindowViewModel.Status.ConnectionFailed"), ex.Message);
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        Log.Information("Connection manually disconnected");
        _client.Disconnect();
        IsConnected = false;
        ShowConnectionSettings = true;
        ConnectionStatus = LocalizationService.Get("PlayerTcpClientService.Status.NotConnected");
        _localClock.Pause();
    }

    [RelayCommand]
    private async Task Discover()
    {
        IsDiscovering = true;
        ConnectionStatus = "Suche per LAN/mDNS ...";
        DiscoveredServers.Clear();
        OnPropertyChanged(nameof(HasDiscoveredServers));
        OnPropertyChanged(nameof(HasNoDiscoveredServers));

        try
        {
            var servers = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            foreach (var server in servers) DiscoveredServers.Add(server);

            if (servers.Count > 0 && SelectedServer is null) SelectedServer = servers[0];

            Log.Information("Server search completed: {ServerCount} found", servers.Count);
            ConnectionStatus = servers.Count == 0
                ? LocalizationService.Get("ClientMainWindowViewModel.Connection.NoServerFound")
                : string.Format(LocalizationService.Get("ClientMainWindowViewModel.Connection.ServersFound"),
                    servers.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server search failed");
            ConnectionStatus = $"Suche fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
            OnPropertyChanged(nameof(HasDiscoveredServers));
            OnPropertyChanged(nameof(HasNoDiscoveredServers));
        }
    }

    [RelayCommand]
    private void ToggleConnectionSettings()
    {
        ShowConnectionSettings = !ShowConnectionSettings;
    }

    partial void OnShowConnectionSettingsChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionToggleLabel));
    }

    partial void OnSelectedServerChanged(DiscoveredRpgTimeTrackerServer? value)
    {
        if (value is null) return;
        Host = value.Host;
        Port = value.Port;
    }

    // ==================== Session-Snapshot & Deltas ====================

    private void OnSessionSnapshot(SessionSnapshotParams snapshot)
    {
        Log.Information("session.snapshot processed: {ItemCount} items, clock running={IsRunning}, time={GameTime}",
            snapshot.Items.Count, snapshot.IsClockRunning, snapshot.CurrentGameTimeSeconds);
        PlayerHeaderTitle = Limit(snapshot.PlayerHeaderTitle, 120);
        PlayerHeaderSubtitle = Limit(snapshot.PlayerHeaderSubtitle, 180);
        ApplyTheme(snapshot.Theme);
        MapDisplay.ApplyRenderStyle(
            FogOverlayRenderer.BuildHiddenColor(snapshot.FogColorHex, snapshot.FogOpacityPercent),
            snapshot.FogBlurRadius, snapshot.FogBlurEnabled);

        CalendarService.Active = snapshot.ActiveCalendar;
        _localClock.SpeedMultiplier = snapshot.SpeedMultiplier <= 0 ? 1.0 : snapshot.SpeedMultiplier;
        _localClock.SetTime(new GameInstant(snapshot.CurrentGameTimeSeconds));
        if (snapshot.IsClockRunning) _localClock.Start();
        else _localClock.Pause();
        SpeedMultiplierDisplay = _localClock.SpeedMultiplier.ToString("0.0", LocalizationService.Culture) + "×";
        CurrentGameTimeText = FormatGameTime(_localClock.CurrentTime);

        _entries.Clear();
        Items.Clear();
        foreach (var dto in snapshot.Items.Take(200)) ApplyUpsert(dto);

        _calendarEntries.Clear();
        _calendarEntries.AddRange(snapshot.CalendarEntries.Where(entry => entry.IsPlayerVisible));
        if (_calendarEntries.Count == 0)
            ShowPlayerCalendarView = false;

        PlayerCalendarMonth = CalendarMonthStart(_localClock.CurrentTime);
        PlayerCalendarSelectedDate = CalendarService.Active.DayStart(_localClock.CurrentTime);
        RefreshPlayerCalendar();
        OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
    }

    /// <summary>
    ///     Periodic drift correction of the locally derived clock + self-healing, in case a
    ///     clock.* delta was discarded due to skipIfBusy (media transfer was in progress).
    /// </summary>
    private void OnClockHeartbeat(GameInstant gameTime, double speedMultiplier, bool isRunning)
    {
        _localClock.SpeedMultiplier = speedMultiplier <= 0 ? 1.0 : speedMultiplier;
        SpeedMultiplierDisplay = _localClock.SpeedMultiplier.ToString("0.0", LocalizationService.Culture) + "×";
        _localClock.SetTime(gameTime);
        if (isRunning) _localClock.Start();
        else _localClock.Pause();
    }

    // The client must own the same theme.json locally (bundled SampleThemes or the same
    // %AppData%/RpgTimeTracker/Themes file as the host), otherwise the last active theme
    // stays in place (no automatic fallback, to avoid showing a visually wrong theme -
    // see ThemeDefinitionLoader.Resolve).
    private void ApplyTheme(string? theme)
    {
        var newTheme = Limit(theme, 80);
        if (string.Equals(CurrentTheme, newTheme, StringComparison.Ordinal)) return;

        CurrentTheme = newTheme;

        var loaded = ThemeDefinitionLoader.Resolve(newTheme);
        if (loaded is not { } resolved)
        {
            Log.Warning("GM theme {Theme} not found locally - theme remains unchanged", newTheme);
            return;
        }

        ClientThemeService.Apply(resolved.Definition, resolved.FolderPath);
    }

    private void ApplyUpsert(TimelineItemSnapshotDto dto)
    {
        if (!Guid.TryParse(dto.Id, out var id)) return;

        if (!_entries.TryGetValue(id, out var entry))
        {
            entry = new RemoteTimelineEntry(id, new RemoteTimelineItemViewModel(id));
            _entries[id] = entry;
            Items.Add(entry.ViewModel);
        }

        entry.Blink = dto.Blink;

        switch (dto.Kind)
        {
            case TimelineItemSnapshotDto.KindAlarm:
                entry.Timer = null;
                entry.Interval = null;
                entry.Alarm ??= new AlarmItem { Id = id };
                entry.Alarm.Name = dto.Name;
                entry.Alarm.Icon = VisualItemHelper.NormalizeIcon(dto.Icon);
                entry.Alarm.ColorHex = dto.ColorHex;
                entry.Alarm.Blink = dto.Blink;
                entry.Alarm.IsPlayerVisible = dto.IsPlayerVisible;
                entry.Alarm.TriggerAt = new GameInstant(dto.TriggerAtSeconds);
                entry.Alarm.RepeatInterval = dto.RepeatIntervalTicks.HasValue
                    ? TimeSpan.FromTicks(dto.RepeatIntervalTicks.Value)
                    : null;
                entry.Alarm.Restore(dto.IsTriggered);
                break;

            case TimelineItemSnapshotDto.KindInterval:
                entry.Timer = null;
                entry.Alarm = null;
                entry.Interval ??= new IntervalEventItem { Id = id };
                entry.Interval.Name = dto.Name;
                entry.Interval.Icon = VisualItemHelper.NormalizeIcon(dto.Icon);
                entry.Interval.ColorHex = dto.ColorHex;
                entry.Interval.Blink = dto.Blink;
                entry.Interval.IsPlayerVisible = dto.IsPlayerVisible;
                entry.Interval.Interval = TimeSpan.FromTicks(Math.Max(1, dto.IntervalTicks));
                entry.Interval.ActiveDuration = TimeSpan.FromTicks(Math.Max(1, dto.ActiveDurationTicks));
                entry.Interval.MaxRepeats = dto.MaxRepeats;
                entry.Interval.Restore(TimeSpan.FromTicks(dto.ElapsedTicks), dto.IsRunning, dto.IsCompleted);
                break;

            default: // Timer
                entry.Alarm = null;
                entry.Interval = null;
                entry.Timer ??= new TimerItem { Id = id };
                entry.Timer.Name = dto.Name;
                entry.Timer.Icon = VisualItemHelper.NormalizeIcon(dto.Icon);
                entry.Timer.ColorHex = dto.ColorHex;
                entry.Timer.Blink = dto.Blink;
                entry.Timer.IsPlayerVisible = dto.IsPlayerVisible;
                entry.Timer.Duration = TimeSpan.FromTicks(Math.Max(1, dto.DurationTicks));
                entry.Timer.Restore(TimeSpan.FromTicks(dto.ElapsedTicks), dto.IsRunning);
                break;
        }

        RefreshEntryDisplay(entry);
    }

    private void ApplyRemove(Guid id)
    {
        if (_entries.Remove(id, out var entry)) Items.Remove(entry.ViewModel);
    }

    private void OnLocalClockTick(GameInstant newTime, TimeSpan gameDelta)
    {
        CurrentGameTimeText = FormatGameTime(newTime);

        foreach (var entry in _entries.Values)
        {
            entry.Timer?.Advance(gameDelta);
            entry.Alarm?.SyncToTime(newTime);
            entry.Interval?.Advance(gameDelta);
            RefreshEntryDisplay(entry);
        }

        RefreshPlayerCalendarDayStates(CalendarService.Active.DayStart(newTime));
    }

    private static GameInstant CalendarMonthStart(GameInstant instant)
    {
        var calendar = CalendarService.Active;
        var date = calendar.ToCalendarDate(instant);
        return calendar.FromCalendarDate(date.Year, date.MonthIndex, 1, 0, 0, 0);
    }

    private void RefreshPlayerCalendar()
    {
        var calendar = CalendarService.Active;
        var monthDate = calendar.ToCalendarDate(PlayerCalendarMonth);
        PlayerCalendarMonthLabel = $"{monthDate.MonthName} {monthDate.Year:0000}";

        var selectedDate = calendar.ToCalendarDate(PlayerCalendarSelectedDate);
        PlayerCalendarSelectedDateLabel =
            $"{selectedDate.WeekdayName}, {selectedDate.Day:00}.{selectedDate.MonthIndex + 1:00}.{selectedDate.Year:0000}";

        PlayerCalendarDays.Clear();
        var weekLength = calendar.Weekdays.Count > 0 ? calendar.Weekdays.Count : 7;
        var gridStart =
            PlayerCalendarMonth.Add(TimeSpan.FromSeconds(-(double)monthDate.WeekdayIndex * calendar.SecondsPerDay));
        var selectedDayNumber = calendar.ToDayNumber(PlayerCalendarSelectedDate);
        var todayDayNumber = calendar.ToDayNumber(_localClock.CurrentTime);
        for (var index = 0; index < weekLength * 6; index++)
        {
            var day = gridStart.Add(TimeSpan.FromSeconds((double)index * calendar.SecondsPerDay));
            var dayDate = calendar.ToCalendarDate(day);
            var vm = new PlayerCalendarDayViewModel(day, dayDate.Day.ToString(), SelectPlayerCalendarDate)
            {
                IsCurrentMonth = dayDate.MonthIndex == monthDate.MonthIndex && dayDate.Year == monthDate.Year,
                IsSelected = calendar.ToDayNumber(day) == selectedDayNumber,
                IsToday = calendar.ToDayNumber(day) == todayDayNumber,
                HasEntries = _calendarEntries.Any(entry => entry.TryGetOccurrenceOn(calendar, day, out _))
            };
            PlayerCalendarDays.Add(vm);
        }

        PlayerCalendarEntries.Clear();
        foreach (var item in _calendarEntries
                     .Select(entry => new
                     {
                         Entry = entry,
                         Occurs = entry.TryGetOccurrenceOn(calendar, PlayerCalendarSelectedDate, out var occurrence),
                         Occurrence = occurrence
                     })
                     .Where(x => x.Occurs)
                     .OrderBy(x => x.Occurrence))
        {
            var occurrenceDate = calendar.ToCalendarDate(item.Occurrence);
            PlayerCalendarEntries.Add(new PlayerCalendarEntryViewModel
            {
                Title = Limit(item.Entry.Title, 120),
                Description = Limit(item.Entry.Description, 220),
                Icon = VisualItemHelper.NormalizeIcon(item.Entry.Icon),
                ColorHex = item.Entry.ColorHex,
                TimeText = $"{occurrenceDate.Hour:00}:{occurrenceDate.Minute:00}"
            });
        }

        OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
    }

    private void RefreshPlayerCalendarDayStates(GameInstant today)
    {
        var calendar = CalendarService.Active;
        var todayDayNumber = calendar.ToDayNumber(today);
        var selectedDayNumber = calendar.ToDayNumber(PlayerCalendarSelectedDate);
        foreach (var day in PlayerCalendarDays)
        {
            var dayNumber = calendar.ToDayNumber(day.Date);
            day.IsToday = dayNumber == todayDayNumber;
            day.IsSelected = dayNumber == selectedDayNumber;
            day.HasEntries = _calendarEntries.Any(entry => entry.TryGetOccurrenceOn(calendar, day.Date, out _));
        }

        var selectedDate = calendar.ToCalendarDate(PlayerCalendarSelectedDate);
        PlayerCalendarSelectedDateLabel =
            $"{selectedDate.WeekdayName}, {selectedDate.Day:00}.{selectedDate.MonthIndex + 1:00}.{selectedDate.Year:0000}";
    }

    private void SelectPlayerCalendarDate(GameInstant date)
    {
        var calendar = CalendarService.Active;
        PlayerCalendarSelectedDate = calendar.DayStart(date);
        var dayDate = calendar.ToCalendarDate(date);
        var monthDate = calendar.ToCalendarDate(PlayerCalendarMonth);
        if (dayDate.MonthIndex != monthDate.MonthIndex || dayDate.Year != monthDate.Year)
            PlayerCalendarMonth = CalendarMonthStart(date);

        RefreshPlayerCalendar();
    }

    private void RefreshEntryDisplay(RemoteTimelineEntry entry)
    {
        var vm = entry.ViewModel;

        if (entry.Timer is { } timer)
        {
            vm.Name = Limit(timer.Name, 120);
            vm.Icon = VisualItemHelper.NormalizeIcon(timer.Icon);
            vm.ColorHex = Limit(timer.ColorHex, 24);
            vm.KindLabel = LocalizationService.Get("TimelineDisplayItem.KindTimer");
            vm.PrimaryValue = FormatTimeSpan(timer.Remaining);
            vm.DetailText = string.Format(LocalizationService.Get("ClientMainWindowViewModel.Timer.DetailFormat"),
                FormatTimeSpan(timer.Remaining), FormatTimeSpan(timer.Duration));
            vm.StatusText = timer.IsCompleted
                ? LocalizationService.Get("TimelineDisplayItem.StatusExpired")
                : timer.IsRunning
                    ? LocalizationService.Get("TimelineDisplayItem.StatusRunning")
                    : LocalizationService.Get("TimelineDisplayItem.StatusPaused");
            vm.IsActive = timer.IsRunning;
            vm.IsCompleted = timer.IsCompleted;
            vm.HasProgress = true;
            vm.Progress = timer.Progress;
            vm.IsBlinkActive = entry.Blink && timer.IsCompleted && VisualItemHelper.BlinkFrame();
            return;
        }

        if (entry.Alarm is { } alarm)
        {
            var currentTime = _localClock.CurrentTime;
            vm.Name = Limit(alarm.Name, 120);
            vm.Icon = VisualItemHelper.NormalizeIcon(alarm.Icon);
            vm.ColorHex = Limit(alarm.ColorHex, 24);
            vm.KindLabel = LocalizationService.Get("TimelineDisplayItem.KindAlarm");
            vm.PrimaryValue = alarm.IsOverdue(currentTime) || alarm.IsTriggered
                ? LocalizationService.Get("AlarmItem.NowLabel")
                : FormatTimeSpan(alarm.TimeRemaining(currentTime));
            var repeatDisplay = alarm.RepeatInterval.HasValue
                ? FormatTimeSpan(alarm.RepeatInterval.Value)
                : LocalizationService.Get("AlarmItem.RepeatOnce");
            vm.DetailText = string.Format(LocalizationService.Get("ClientMainWindowViewModel.Alarm.DetailFormat"),
                $"{alarm.TriggerAt:dddd, dd.MM.yyyy HH:mm}", repeatDisplay);
            vm.StatusText = alarm.IsTriggered
                ? LocalizationService.Get("TimelineDisplayItem.StatusTriggered")
                : LocalizationService.Get("TimelineDisplayItem.StatusReady");
            vm.IsActive = alarm.IsTriggered;
            vm.IsCompleted = alarm.IsTriggered;
            vm.HasProgress = false;
            vm.Progress = 0;
            vm.IsBlinkActive = entry.Blink && alarm.IsTriggered && VisualItemHelper.BlinkFrame();
            return;
        }

        if (entry.Interval is { } interval)
        {
            vm.Name = Limit(interval.Name, 120);
            vm.Icon = VisualItemHelper.NormalizeIcon(interval.Icon);
            vm.ColorHex = Limit(interval.ColorHex, 24);
            vm.KindLabel = LocalizationService.Get("TimelineDisplayItem.KindInterval");
            vm.PrimaryValue = interval.IsActive
                ? string.Format(LocalizationService.Get("IntervalEvent.RemainingActiveFormat"),
                    FormatTimeSpan(interval.Remaining))
                : string.Format(LocalizationService.Get("IntervalEvent.RemainingNextFormat"),
                    FormatTimeSpan(interval.Remaining));
            var repeatDisplay = interval.MaxRepeats is > 0
                ? $"{interval.CurrentRepeatNumber}/{interval.MaxRepeats}"
                : $"{interval.CurrentRepeatNumber}/∞";
            vm.DetailText = string.Format(LocalizationService.Get("ClientMainWindowViewModel.Interval.DetailFormat"),
                FormatTimeSpan(interval.Interval), FormatTimeSpan(interval.ActiveDuration), repeatDisplay);
            vm.StatusText = interval.IsCompleted
                ? LocalizationService.Get("IntervalEvent.StatusDone")
                : interval.IsActive
                    ? LocalizationService.Get("TimelineDisplayItem.StatusActive")
                    : interval.IsRunning
                        ? LocalizationService.Get("TimelineDisplayItem.StatusRunning")
                        : LocalizationService.Get("TimelineDisplayItem.StatusPaused");
            vm.IsActive = interval.IsActive;
            vm.IsCompleted = interval.IsCompleted;
            vm.HasProgress = true;
            vm.Progress = interval.Progress;
            vm.IsBlinkActive = entry.Blink && interval.IsActive && VisualItemHelper.BlinkFrame();
        }
    }

    // ==================== Media ====================

    private void OnMediaBegin(MediaHeaderDto header, string tempPath)
    {
        if (header.Kind == MediaHeaderDto.MediaKindAudio)
        {
            // Sounds deliberately get NO media window and do not replace a currently shown
            // image/video - see PlaySound comment. Hence also no CurrentMediaFileName/
            // MediaWindowShouldShow here, that is exclusive to the image/video display slot.
            PlaySound(tempPath, header);
            return;
        }

        // Event-trigger media (AddToGallery=false) take precedence: they temporarily
        // override the display and lock local gallery navigation until they close again
        // (media.cleared - see OnMediaCleared) - see design-decisions.md.
        _isShowingEventMedia = !header.AddToGallery;
        CurrentMediaFileName = Limit(header.FileName, 120);

        if (header.Kind == MediaHeaderDto.MediaKindVideo)
        {
            // Show the window first (size/position aligned to the main window, fullscreen
            // if applicable, Topmost set), THEN start playback - otherwise the native
            // video rendering surface (VideoView is an embedded native child window,
            // not a pure Avalonia control) gets resized mid-playback, which for
            // high-resolution videos leads to a wrong window size and/or a video that
            // appears behind rather than in the timeline.
            MediaWindowShouldShow?.Invoke();
            StartVideo(tempPath, header.MediaId, header.Loop, header.FileName, header.AddToGallery);
        }
        // For images: OnMediaCompleted waits, a partial image cannot be decoded.
    }

    private void OnMediaCompleted(MediaHeaderDto header, string tempPath)
    {
        if (header.Kind != MediaHeaderDto.MediaKindImage) return;

        StopVideo();
        try
        {
            using var stream = File.OpenRead(tempPath);
            var bitmap = new Bitmap(stream);
            CurrentMediaBitmap = bitmap;
            MediaErrorMessage = null;
            CurrentMediaKind = MediaKind.Image;
            MediaWindowShouldShow?.Invoke();

            if (header.AddToGallery)
                AddOrUpdateGalleryEntry(header.MediaId, header.FileName, MediaKind.Image, null, bitmap);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Image could not be decoded: {FileName}", header.FileName);
            MediaErrorMessage = $"Bild konnte nicht angezeigt werden: {ex.Message}";
            CurrentMediaKind = MediaKind.None;
        }
        finally
        {
            // The image is now in memory as a bitmap (gallery entry or not) - the
            // temp file is never needed again, unlike video (see StartVideo).
            DeleteFileQuietly(tempPath);
        }
    }

    private void StartVideo(string tempPath, string mediaId, bool loop, string fileName, bool addToGallery)
    {
        CurrentMediaBitmap = null;
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            Log.Error("Video cannot be played: LibVLC not available ({Error})",
                VlcMediaService.LastError);
            MediaErrorMessage = VlcMediaService.LastError ?? "Video konnte nicht abgespielt werden.";
            CurrentMediaKind = MediaKind.None;
            return;
        }

        try
        {
            var previousTempPath = _currentVideoTempPath;
            _currentVideoTempPath = tempPath;
            _currentMediaId = mediaId;
            _currentMediaLoop = loop;
            _playbackStartedReported = false;

            if (MediaPlayer is null)
            {
                MediaPlayer = new MediaPlayer(libVlc);
                // Once for the lifetime of the player - not per video, since the same
                // MediaPlayer is reused across StartVideo calls.
                MediaPlayer.LengthChanged += OnVlcLengthChanged;
                MediaPlayer.EndReached += OnVlcEndReached;
            }

            using var media = new Media(libVlc, tempPath);
            MediaPlayer.Play(media);

            // Only delete the previous video if it is NOT (no longer) part of the gallery -
            // gallery videos stay on disk until explicitly removed (RetractGalleryItem),
            // so local back-navigation doesn't need a re-transfer.
            if (!_gallery.Any(g => g.TempPath == previousTempPath)) DeleteFileQuietly(previousTempPath);

            MediaErrorMessage = null;
            CurrentMediaKind = MediaKind.Video;

            if (addToGallery) AddOrUpdateGalleryEntry(mediaId, fileName, MediaKind.Video, tempPath, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Video could not be played: {TempPath}", tempPath);
            MediaErrorMessage = $"Video konnte nicht abgespielt werden: {ex.Message}";
            CurrentMediaKind = MediaKind.None;
        }
    }

    private void PlaySound(string tempPath, MediaHeaderDto header)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            Log.Error("Sound cannot be played: LibVLC not available ({Error})",
                VlcMediaService.LastError);
            DeleteFileQuietly(tempPath);
            return;
        }

        try
        {
            var mediaId = header.MediaId;
            var player = new MediaPlayer(libVlc);
            var reported = false;
            player.LengthChanged += (_, e) =>
            {
                if (reported || e.Length <= 0) return;
                reported = true;
                Log.Information("Sound playback started: {MediaId} ({DurationMs} ms)", mediaId, e.Length);
                _ = _client.SendMediaPlaybackStartedAsync(mediaId, TimeSpan.FromMilliseconds(e.Length));
            };
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => OnSoundEndReached(mediaId, tempPath));

            _activeSoundPlayers[mediaId] = player;
            _soundRepeatsRemaining[mediaId] = header.Loop ? -1 : Math.Max(1, header.RepeatCount);

            using var media = new Media(libVlc, tempPath);
            VlcMediaService.ApplySoundTrim(media, header.TrimStartMs, header.TrimEndMs);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);

            // Estimated mid-playback catch-up (see MediaHeaderDto.SeekToMs) - only sent for
            // sounds longer than the GM's configured threshold when resending to a client whose
            // Sound routing was just re-enabled (see MainWindowViewModel.ResendActiveSoundsToClient).
            // Same one-shot-after-first-TimeChanged approach as PlayMusicTrack: VLC needs the
            // media to actually start playing before a seek sticks.
            if (header.SeekToMs > 0)
            {
                var seekToMs = header.SeekToMs;
                EventHandler<MediaPlayerTimeChangedEventArgs>? onFirstTimeChanged = null;
                onFirstTimeChanged = (_, _) =>
                {
                    player.TimeChanged -= onFirstTimeChanged;
                    Dispatcher.UIThread.Post(() => player.Time = seekToMs);
                };
                player.TimeChanged += onFirstTimeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound could not be played: {TempPath}", tempPath);
            DeleteFileQuietly(tempPath);
        }
    }

    /// <summary>
    ///     GM stops this sound specifically (media.stopSound) - regardless of whether it is
    ///     currently ending naturally or looping.
    /// </summary>
    private void StopSound(string mediaId)
    {
        if (!_activeSoundPlayers.Remove(mediaId, out var player)) return;

        _soundRepeatsRemaining.Remove(mediaId);
        Log.Information("Sound {MediaId} stopped at GM's request", mediaId);
        player.Stop();
        player.Dispose();
    }

    private void ApplySoundVolume(string mediaId, int volume)
    {
        if (!_activeSoundPlayers.TryGetValue(mediaId, out var player)) return;

        player.Volume = Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    ///     Stops ALL currently running sounds - e.g. when the connection to the host drops,
    ///     so this device does not keep playing uncontrolled (e.g. a looping ambience).
    /// </summary>
    private void StopAllSounds()
    {
        foreach (var (mediaId, player) in _activeSoundPlayers)
        {
            Log.Information("Sound {MediaId} stopped (StopAllSounds)", mediaId);
            player.Stop();
            player.Dispose();
        }

        _activeSoundPlayers.Clear();
        _soundRepeatsRemaining.Clear();
    }

    /// <summary>
    ///     Endless (loop) or repeats still remaining: the same sound player restarts locally.
    ///     Otherwise: clean up + notify the host (the host no longer actively tracks sounds, but
    ///     the notification does no harm and stays consistent with the video path for possible
    ///     later evaluation).
    /// </summary>
    private void OnSoundEndReached(string mediaId, string tempPath)
    {
        if (!_activeSoundPlayers.TryGetValue(mediaId, out var player)) return;

        var remaining = _soundRepeatsRemaining.GetValueOrDefault(mediaId, 0);
        if (remaining < 0 || remaining > 1)
        {
            Log.Debug("Sound {MediaId} ended - restarting (remaining: {Remaining})", mediaId, remaining);
            if (remaining > 0) _soundRepeatsRemaining[mediaId] = remaining - 1;
            player.Stop();
            player.Play();
            return;
        }

        Log.Information("Sound {MediaId} ended", mediaId);
        _activeSoundPlayers.Remove(mediaId);
        _soundRepeatsRemaining.Remove(mediaId);
        player.Stop();
        player.Dispose();
        DeleteFileQuietly(tempPath);
        _ = _client.SendMediaPlaybackEndedAsync(mediaId);
    }

    /// <summary>
    ///     Plays a music track received from the Host's playlist sequencer. Any previously
    ///     playing track is stopped first - the Host only ever has one track active at a time,
    ///     so this client never needs to track more than one MediaPlayer for music.
    /// </summary>
    private void PlayMusicTrack(string tempPath, MediaHeaderDto header)
    {
        StopMusic();

        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            Log.Error("Music track cannot be played: LibVLC not available ({Error})", VlcMediaService.LastError);
            DeleteFileQuietly(tempPath);
            return;
        }

        try
        {
            var mediaId = header.MediaId;
            var player = new MediaPlayer(libVlc);
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => OnMusicEndReached(mediaId));

            _musicPlayer = player;
            _musicTempPath = tempPath;

            using var media = new Media(libVlc, tempPath);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);

            // Estimated mid-track catch-up (see MediaHeaderDto.SeekToMs) - joining/reconnecting
            // mid-playlist starts at roughly the right spot instead of from 0. Not frame-accurate
            // (based on the Host's wall-clock elapsed-since-start estimate), and VLC needs the
            // media to actually start playing before a seek sticks, hence the one-shot handler
            // instead of setting player.Time immediately after Play().
            if (header.SeekToMs > 0)
            {
                var seekToMs = header.SeekToMs;
                EventHandler<MediaPlayerTimeChangedEventArgs>? onFirstTimeChanged = null;
                onFirstTimeChanged = (_, _) =>
                {
                    player.TimeChanged -= onFirstTimeChanged;
                    Dispatcher.UIThread.Post(() => player.Time = seekToMs);
                };
                player.TimeChanged += onFirstTimeChanged;
            }

            Log.Information("Music track playing: {FileName} ({MediaId}), seek {SeekToMs}ms", header.FileName,
                mediaId, header.SeekToMs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music track could not be played: {TempPath}", tempPath);
            DeleteFileQuietly(tempPath);
        }
    }

    /// <summary>GM stops the current music track/playlist (music.stop), or the connection dropped.</summary>
    private void StopMusic()
    {
        if (_musicPlayer is null) return;

        _musicPlayer.Stop();
        _musicPlayer.Dispose();
        _musicPlayer = null;

        if (_musicTempPath is not null) DeleteFileQuietly(_musicTempPath);
        _musicTempPath = null;
    }

    private void ApplyMusicVolume(int volume)
    {
        if (_musicPlayer is not null) _musicPlayer.Volume = Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    ///     A track ended naturally - unlike sounds, music never loops/repeats locally: the Host's
    ///     playlist sequencer decides what plays next (respecting loop/shuffle) and sends it, so
    ///     this just reports the end and clears local state.
    /// </summary>
    private void OnMusicEndReached(string mediaId)
    {
        if (_musicPlayer is null) return;

        Log.Information("Music track ended: {MediaId}", mediaId);
        var tempPath = _musicTempPath;
        _musicPlayer.Stop();
        _musicPlayer.Dispose();
        _musicPlayer = null;
        _musicTempPath = null;

        if (tempPath is not null) DeleteFileQuietly(tempPath);
        _ = _client.SendMusicTrackEndedAsync(mediaId);
    }

    /// <summary>Reports the actual (LibVLC-determined) video duration to the host once.</summary>
    private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_playbackStartedReported || e.Length <= 0) return;
        _playbackStartedReported = true;

        var mediaId = _currentMediaId;
        if (string.IsNullOrEmpty(mediaId)) return;
        Log.Information("Video playback started: {MediaId} ({DurationMs} ms)", mediaId, e.Length);
        _ = _client.SendMediaPlaybackStartedAsync(mediaId, TimeSpan.FromMilliseconds(e.Length));
    }

    /// <summary>
    ///     Loop: restart locally. Otherwise: notify the host, which then closes the medium/resumes
    ///     the paused clock.
    /// </summary>
    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        var loop = _currentMediaLoop;
        var mediaId = _currentMediaId;

        // Runs on a LibVLC-internal callback thread - per LibVLC docs one should not call back
        // directly into the player from there (Play/Stop), hence moving to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (loop)
            {
                Log.Debug("Video {MediaId} ended - looping, restarting", mediaId);
                MediaPlayer?.Stop();
                MediaPlayer?.Play();
                return;
            }

            if (!string.IsNullOrEmpty(mediaId))
            {
                Log.Information("Video {MediaId} ended - notifying host", mediaId);
                _ = _client.SendMediaPlaybackEndedAsync(mediaId);
            }
        });
    }

    private void OnMediaCleared()
    {
        Log.Information("Medium closed (media.cleared)");
        StopVideo();
        CurrentMediaKind = MediaKind.None;
        CurrentMediaFileName = null;
        CurrentMediaBitmap = null;
        MediaErrorMessage = null;
        // Unlocks local gallery navigation if this was an event medium (precedence now over) -
        // the host then usually sends a media.highlight to resume the gallery at the last
        // shown position (see MainWindowViewModel.ResumeGalleryAfterEventMedia).
        _isShowingEventMedia = false;
        MediaWindowShouldHide?.Invoke();
    }

    private void StopVideo()
    {
        MediaPlayer?.Stop();

        // Gallery videos stay on disk until explicitly removed (RetractGalleryItem).
        if (!_gallery.Any(g => g.TempPath == _currentVideoTempPath)) DeleteFileQuietly(_currentVideoTempPath);

        _currentVideoTempPath = null;
    }

    private void AddOrUpdateGalleryEntry(string mediaId, string name, MediaKind kind, string? tempPath, Bitmap? bitmap)
    {
        var entry = new GalleryEntry
            { MediaId = mediaId, Name = name, Kind = kind, TempPath = tempPath, Bitmap = bitmap };
        var existingIndex = _gallery.FindIndex(g => g.MediaId == mediaId);
        if (existingIndex >= 0)
        {
            _gallery[existingIndex] = entry;
            _currentGalleryIndex = existingIndex;
            return;
        }

        _gallery.Add(entry);
        _currentGalleryIndex = _gallery.Count - 1;
    }

    [RelayCommand]
    private void NextGalleryItem()
    {
        NavigateGalleryLocally(1);
    }

    [RelayCommand]
    private void PreviousGalleryItem()
    {
        NavigateGalleryLocally(-1);
    }

    private void NavigateGalleryLocally(int direction)
    {
        if (_isShowingEventMedia || _gallery.Count == 0) return;

        var nextIndex = ((_currentGalleryIndex < 0 ? 0 : _currentGalleryIndex) + direction + _gallery.Count) %
                        _gallery.Count;
        DisplayGalleryEntry(_gallery[nextIndex]);
    }

    /// <summary>
    ///     GM highlights this item (media.highlight) - a jump suggestion for everyone,
    ///     not mandatory; ignored if the item is (not yet) known here.
    /// </summary>
    private void ShowGalleryItem(string mediaId)
    {
        var entry = _gallery.FirstOrDefault(g => g.MediaId == mediaId);
        if (entry is null) return;

        _isShowingEventMedia = false;
        DisplayGalleryEntry(entry);
    }

    private void DisplayGalleryEntry(GalleryEntry entry)
    {
        _currentGalleryIndex = _gallery.IndexOf(entry);
        CurrentMediaFileName = Limit(entry.Name, 120);
        MediaErrorMessage = null;

        if (entry.Kind == MediaKind.Image)
        {
            StopVideo();
            CurrentMediaBitmap = entry.Bitmap;
            CurrentMediaKind = MediaKind.Image;
        }
        else if (entry.TempPath is not null && File.Exists(entry.TempPath) &&
                 VlcMediaService.TryGetLibVlc(out var libVlc) && libVlc is not null)
        {
            CurrentMediaBitmap = null;
            try
            {
                _currentMediaId = entry.MediaId;
                _currentMediaLoop = false;
                _currentVideoTempPath = entry.TempPath;
                if (MediaPlayer is null)
                {
                    MediaPlayer = new MediaPlayer(libVlc);
                    MediaPlayer.LengthChanged += OnVlcLengthChanged;
                    MediaPlayer.EndReached += OnVlcEndReached;
                }

                using var media = new Media(libVlc, entry.TempPath);
                MediaPlayer.Play(media);
                CurrentMediaKind = MediaKind.Video;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Gallery video could not be played: {TempPath}", entry.TempPath);
            }
        }

        MediaWindowShouldShow?.Invoke();
    }

    private void RetractGalleryItem(string mediaId)
    {
        var index = _gallery.FindIndex(g => g.MediaId == mediaId);
        if (index < 0) return;

        var entry = _gallery[index];
        _gallery.RemoveAt(index);
        DeleteFileQuietly(entry.TempPath);

        if (_currentGalleryIndex == index)
        {
            _currentGalleryIndex = -1;
            if (!_isShowingEventMedia) OnMediaCleared();
        }
        else if (_currentGalleryIndex > index)
        {
            _currentGalleryIndex--;
        }
    }

    private void ClearGallery()
    {
        foreach (var entry in _gallery) DeleteFileQuietly(entry.TempPath);
        _gallery.Clear();
        _currentGalleryIndex = -1;
        _isShowingEventMedia = false;
        _slideshowTimer?.Stop();
        _slideshowTimer = null;
    }

    /// <summary>
    ///     Deletes all still-present local temp files of this session (gallery + currently
    ///     shown ad-hoc video) and additionally cleans up orphaned temp files from an earlier
    ///     run that didn't end cleanly (same naming pattern, see BeginMediaStream) - only called
    ///     on explicit confirmation when closing the app (see ClientMainWindow.OnClosing), so
    ///     media leftovers don't permanently accumulate in the temp directory.
    /// </summary>
    public void DeleteAllTempFiles()
    {
        ClearGallery();

        if (_currentVideoTempPath is not null)
        {
            DeleteFileQuietly(_currentVideoTempPath);
            _currentVideoTempPath = null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "rpgtimetracker-media-*.*"))
                DeleteFileQuietly(file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Orphaned temp files could not be fully cleaned up");
        }
    }

    private void ApplySlideshowInterval(double seconds)
    {
        _slideshowTimer?.Stop();
        _slideshowTimer = null;
        if (seconds <= 0) return;

        _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _slideshowTimer.Tick += (_, _) => AdvanceSlideshowIfDue();
        _slideshowTimer.Start();
    }

    /// <summary>
    ///     Videos are never switched away automatically - the timer simply skips this tick
    ///     and checks again on the next one.
    /// </summary>
    private void AdvanceSlideshowIfDue()
    {
        if (_isShowingEventMedia || _gallery.Count == 0) return;
        if (_currentGalleryIndex >= 0 && _gallery[_currentGalleryIndex].Kind == MediaKind.Video) return;

        NavigateGalleryLocally(1);
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Limit(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string FormatGameTime(GameInstant time)
    {
        var date = CalendarService.Active.ToCalendarDate(time);
        return
            $"{date.WeekdayName}, {date.Day:00}.{date.MonthIndex + 1:00}.{date.Year:0000} — {date.Hour:00}:{date.Minute:00}:{date.Second:00}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}.{ts:hh\\:mm\\:ss}"
            : $"{ts:hh\\:mm\\:ss}";
    }

    [RelayCommand]
    private void ShowPlayerTimeline()
    {
        ShowPlayerCalendarView = false;
    }

    [RelayCommand]
    private void ShowPlayerCalendar()
    {
        if (!HasPlayerVisibleCalendarEntries)
            return;

        ShowPlayerCalendarView = true;
    }

    [RelayCommand]
    private void PreviousPlayerCalendarMonth()
    {
        PlayerCalendarMonth = AddCalendarMonths(PlayerCalendarMonth, -1);
        RefreshPlayerCalendar();
    }

    [RelayCommand]
    private void NextPlayerCalendarMonth()
    {
        PlayerCalendarMonth = AddCalendarMonths(PlayerCalendarMonth, 1);
        RefreshPlayerCalendar();
    }

    private static GameInstant AddCalendarMonths(GameInstant instant, int delta)
    {
        var calendar = CalendarService.Active;
        var date = calendar.ToCalendarDate(instant);
        var monthCount = calendar.Months.Count > 0 ? calendar.Months.Count : 12;
        var absoluteMonth = date.Year * monthCount + date.MonthIndex + delta;
        var year = FloorDivInt(absoluteMonth, monthCount);
        var monthIndex = ModInt(absoluteMonth, monthCount);
        return calendar.FromCalendarDate(year, monthIndex, 1, 0, 0, 0);
    }

    private static int FloorDivInt(int a, int b)
    {
        var q = a / b;
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) q--;
        return q;
    }

    private static int ModInt(int a, int b)
    {
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) r += b;
        return r;
    }

    // ==================== Gallery: session-own list of sent images/videos ====================
    // Each client navigates independently and purely locally through this list (arrow keys/click,
    // see Views/MediaWindow.axaml.cs) - without asking the host. A media.highlight from the host
    // is only a jump suggestion, not a lock: afterward the player can move away freely again.
    // Event-trigger media (AddToGallery=false) are NEVER part of this list and only block
    // navigation while they themselves are being shown (see _isShowingEventMedia).

    private sealed class GalleryEntry
    {
        public required string MediaId { get; init; }
        public required string Name { get; init; }
        public required MediaKind Kind { get; init; }
        public string? TempPath { get; init; }
        public Bitmap? Bitmap { get; init; }
    }

    private sealed class RemoteTimelineEntry
    {
        public RemoteTimelineEntry(Guid id, RemoteTimelineItemViewModel viewModel)
        {
            Id = id;
            ViewModel = viewModel;
        }

        public Guid Id { get; }
        public RemoteTimelineItemViewModel ViewModel { get; }
        public bool Blink { get; set; }
        public TimerItem? Timer { get; set; }
        public AlarmItem? Alarm { get; set; }
        public IntervalEventItem? Interval { get; set; }
    }
}