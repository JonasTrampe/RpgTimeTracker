using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.PlayerClient.ViewModels;

public partial class ClientMainWindowViewModel : ObservableObject, IDisposable, IPlayerDisplayContext
{
    // ==================== Sounds: unabhängig vom Bild/Video-Anzeige-Slot ====================
    // Sounds laufen NIE über CurrentMediaKind/MediaPlayer (Video-Feld) - kein Fenster, keine
    // Ersetzung eines gerade gezeigten Bilds/Videos, und beliebig viele parallel: jeder Sound
    // bekommt einen eigenen, kurzlebigen MediaPlayer statt den gemeinsam genutzten Video-Player.

    private readonly Dictionary<string, MediaPlayer> _activeSoundPlayers = new();
    private readonly List<CalendarEntryDefinition> _calendarEntries = new();
    private readonly PlayerTcpClientService _client = new();
    private readonly MdnsDiscoveryService _discovery = new();

    private readonly Dictionary<Guid, RemoteTimelineEntry> _entries = new();

    private readonly List<GalleryEntry> _gallery = new();

    /// <summary>
    ///     Läuft rein lokal (nicht netzgetrieben): der Server schickt Zeit nur bei Sprüngen,
    ///     dazwischen tickt diese Instanz mit der zuletzt bekannten Geschwindigkeit selbst weiter
    ///     - identische Ableitungslogik wie beim Host, siehe RpgTimeTracker.Shared.Services.
    /// </summary>
    private readonly GameClockService _localClock = new(DateTime.Now);

    /// <summary>
    ///     Verbleibende Wiedergaben je MediaId (-1 = endlos/Loop, sonst zählt bei jedem
    ///     natürlichen Ende runter - siehe OnSoundEndReached).
    /// </summary>
    private readonly Dictionary<string, int> _soundRepeatsRemaining = new();

    [ObservableProperty] private string _connectionStatus = "Nicht verbunden";
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
    private bool _isShowingEventMedia;
    [ObservableProperty] private string? _mediaErrorMessage;
    [ObservableProperty] private bool _mediaFullscreen;
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private string _pin = string.Empty;
    private bool _playbackStartedReported;
    [ObservableProperty] private DateTime _playerCalendarMonth = DateTime.Today;
    [ObservableProperty] private string _playerCalendarMonthLabel = string.Empty;
    [ObservableProperty] private DateTime _playerCalendarSelectedDate = DateTime.Today;
    [ObservableProperty] private string _playerCalendarSelectedDateLabel = string.Empty;
    [ObservableProperty] private string _playerHeaderSubtitle = "Remote Client";
    [ObservableProperty] private string _playerHeaderTitle = "Spieleranzeige";
    [ObservableProperty] private int _port = 48550;
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

        _localClock.Tick += (newTime, gameDelta) => OnLocalClockTick(newTime, gameDelta);

        _client.SessionSnapshotReceived += s => Dispatcher.UIThread.Post(() => OnSessionSnapshot(s));
        _client.ClockStarted += () => Dispatcher.UIThread.Post(() => _localClock.Start());
        _client.ClockStopped += () => Dispatcher.UIThread.Post(() => _localClock.Pause());
        _client.ClockSpeedChanged += speed => Dispatcher.UIThread.Post(() =>
        {
            _localClock.SpeedMultiplier = speed <= 0 ? 1.0 : speed;
            SpeedMultiplierDisplay = $"{_localClock.SpeedMultiplier:0.0}×";
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
        _client.StatusChanged += status => Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
            IsConnected = status.StartsWith("Verbunden", StringComparison.OrdinalIgnoreCase);
            if (IsConnected)
            {
                ShowConnectionSettings = false;
            }
            else
            {
                // Verbindung weg: lokale Uhr anhalten, damit Restzeiten nicht "phantomhaft"
                // ohne Server weiterlaufen, bis eine neue Verbindung wieder synchronisiert.
                _localClock.Pause();
                // Alle noch laufenden Sounds sofort stoppen - ohne Host-Verbindung soll das
                // Client-Gerät nicht unkontrolliert weiterspielen (z.B. eine Loop-Ambience).
                StopAllSounds();
                ClearGallery();
                _calendarEntries.Clear();
                PlayerCalendarDays.Clear();
                PlayerCalendarEntries.Clear();
                ShowPlayerCalendarView = false;
                OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
                OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
            }
        });
    }

    public bool HasMedia => CurrentMediaKind != MediaKind.None;
    public bool HasImageMedia => CurrentMediaKind == MediaKind.Image;
    public bool HasVideoMedia => CurrentMediaKind == MediaKind.Video;

    public ObservableCollection<RemoteTimelineItemViewModel> Items { get; } = new();
    public ObservableCollection<PlayerCalendarDayViewModel> PlayerCalendarDays { get; } = new();
    public ObservableCollection<PlayerCalendarEntryViewModel> PlayerCalendarEntries { get; } = new();

    public ObservableCollection<DiscoveredRpgTimeTrackerServer> DiscoveredServers { get; } = new();

    public string ConnectionToggleLabel => ShowConnectionSettings ? "Verbindung ausblenden" : "Verbindung anzeigen";
    public bool HasDiscoveredServers => DiscoveredServers.Count > 0;
    public bool HasNoDiscoveredServers => DiscoveredServers.Count == 0;

    public string MediaFullscreenLabel => MediaFullscreen ? "Medien-Vollbild aus" : "Medien-Vollbild an";

    /// <summary>Ob beim Schließen der App überhaupt etwas zum Aufräumen da ist - nur dann lohnt sich die Nachfrage.</summary>
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
    ///     Für PlayerTimelineListView (Shared) - dieselbe Collection-Instanz, keine Kopie, damit Live-Updates erhalten
    ///     bleiben.
    /// </summary>
    IEnumerable IPlayerDisplayContext.TimelineEntries => Items;

    IEnumerable IPlayerDisplayContext.CalendarMonthDays => PlayerCalendarDays;
    IEnumerable IPlayerDisplayContext.CalendarEntries => PlayerCalendarEntries;

    /// <summary>SL fordert an, die Anzeige (Medium falls offen, sonst Zeitliste) fullscreen umzuschalten.</summary>
    public event Action<bool>? RemoteFullscreenRequested;

    /// <summary>
    ///     Feuert unconditionally bei jedem neuen Medium (nicht nur beim Wechsel von "kein Medium"
    ///     zu "Medium") - eine reine HasMedia-Änderungsprüfung würde bei zwei Medien gleicher Art
    ///     hintereinander (z.B. Bild -> Bild) nicht auslösen, weil sich der bool-Wert nicht ändert.
    ///     Damit lässt sich ein vom Spieler zwischenzeitlich geschlossenes Medien-Fenster trotzdem
    ///     beim nächsten gesendeten Medium zuverlässig wieder öffnen.
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

    private void SaveClientSettings()
    {
        ClientSettingsService.SaveSettings(new ClientSettingsService.ClientSettingsDto
        {
            MediaFullscreen = MediaFullscreen,
            Pin = Pin
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
            Log.Warning(ex, "Verbindung zu {Host}:{Port} fehlgeschlagen", Host, Port);
            IsConnected = false;
            ShowConnectionSettings = true;
            ConnectionStatus = $"Verbindung fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        Log.Information("Verbindung manuell getrennt");
        _client.Disconnect();
        IsConnected = false;
        ShowConnectionSettings = true;
        ConnectionStatus = "Nicht verbunden";
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

            Log.Information("Server-Suche abgeschlossen: {ServerCount} gefunden", servers.Count);
            ConnectionStatus = servers.Count == 0
                ? "Kein Server gefunden. Prüfe: Server gestartet, gleiche LAN/VPN, Firewall für TCP 48550 und UDP 48551/5353."
                : $"{servers.Count} Server gefunden";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server-Suche fehlgeschlagen");
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
        Log.Information("session.snapshot verarbeitet: {ItemCount} Items, Uhr läuft={IsRunning}, Zeit={GameTime}",
            snapshot.Items.Count, snapshot.IsClockRunning, snapshot.CurrentGameTime);
        PlayerHeaderTitle = Limit(snapshot.PlayerHeaderTitle, 120);
        PlayerHeaderSubtitle = Limit(snapshot.PlayerHeaderSubtitle, 180);
        ApplyTheme(snapshot.Theme);

        _localClock.SpeedMultiplier = snapshot.SpeedMultiplier <= 0 ? 1.0 : snapshot.SpeedMultiplier;
        _localClock.SetTime(snapshot.CurrentGameTime);
        if (snapshot.IsClockRunning) _localClock.Start();
        else _localClock.Pause();
        SpeedMultiplierDisplay = $"{_localClock.SpeedMultiplier:0.0}×";
        CurrentGameTimeText = FormatGameTime(_localClock.CurrentTime);

        _entries.Clear();
        Items.Clear();
        foreach (var dto in snapshot.Items.Take(200)) ApplyUpsert(dto);

        _calendarEntries.Clear();
        _calendarEntries.AddRange(snapshot.CalendarEntries.Where(entry => entry.IsPlayerVisible));
        if (_calendarEntries.Count == 0)
            ShowPlayerCalendarView = false;

        PlayerCalendarMonth = new DateTime(_localClock.CurrentTime.Year, _localClock.CurrentTime.Month, 1);
        PlayerCalendarSelectedDate = _localClock.CurrentTime.Date;
        RefreshPlayerCalendar();
        OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
    }

    /// <summary>
    ///     Periodische Drift-Korrektur der lokal abgeleiteten Uhr + Selbstheilung, falls ein
    ///     clock.*-Delta wegen skipIfBusy (Medienübertragung lief gerade) verworfen wurde.
    /// </summary>
    private void OnClockHeartbeat(DateTime gameTime, double speedMultiplier, bool isRunning)
    {
        _localClock.SpeedMultiplier = speedMultiplier <= 0 ? 1.0 : speedMultiplier;
        SpeedMultiplierDisplay = $"{_localClock.SpeedMultiplier:0.0}×";
        _localClock.SetTime(gameTime);
        if (isRunning) _localClock.Start();
        else _localClock.Pause();
    }

    // Der Client muss dieselbe theme.json lokal besitzen (mitgelieferte SampleThemes oder
    // dieselbe %AppData%/RpgTimeTracker/Themes-Datei wie der Host), sonst bleibt das zuletzt
    // aktive Design bestehen (kein automatischer Rückfall, um kein optisch falsches Design zu
    // zeigen - siehe ThemeDefinitionLoader.Resolve).
    private void ApplyTheme(string? theme)
    {
        var newTheme = Limit(theme, 80);
        if (string.Equals(CurrentTheme, newTheme, StringComparison.Ordinal)) return;

        CurrentTheme = newTheme;

        var loaded = ThemeDefinitionLoader.Resolve(newTheme);
        if (loaded is not { } resolved)
        {
            Log.Warning("SL-Design {Theme} lokal nicht gefunden - Design bleibt unverändert", newTheme);
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
                entry.Alarm.TriggerAt = dto.TriggerAt;
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

    private void OnLocalClockTick(DateTime newTime, TimeSpan gameDelta)
    {
        CurrentGameTimeText = FormatGameTime(newTime);

        foreach (var entry in _entries.Values)
        {
            entry.Timer?.Advance(gameDelta);
            entry.Alarm?.SyncToTime(newTime);
            entry.Interval?.Advance(gameDelta);
            RefreshEntryDisplay(entry);
        }

        RefreshPlayerCalendarDayStates(newTime.Date);
    }

    private void RefreshPlayerCalendar()
    {
        PlayerCalendarMonthLabel = PlayerCalendarMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        PlayerCalendarSelectedDateLabel =
            PlayerCalendarSelectedDate.ToString("dddd, dd.MM.yyyy", CultureInfo.CurrentCulture);

        PlayerCalendarDays.Clear();
        var firstOfMonth = new DateTime(PlayerCalendarMonth.Year, PlayerCalendarMonth.Month, 1);
        var offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var gridStart = firstOfMonth.AddDays(-offset);
        for (var index = 0; index < 42; index++)
        {
            var day = gridStart.AddDays(index);
            var vm = new PlayerCalendarDayViewModel(day, SelectPlayerCalendarDate)
            {
                IsCurrentMonth = day.Month == PlayerCalendarMonth.Month,
                IsSelected = day.Date == PlayerCalendarSelectedDate.Date,
                IsToday = day.Date == _localClock.CurrentTime.Date,
                HasEntries = _calendarEntries.Any(entry => entry.TryGetOccurrenceOn(day, out _))
            };
            PlayerCalendarDays.Add(vm);
        }

        PlayerCalendarEntries.Clear();
        foreach (var item in _calendarEntries
                     .Select(entry => new
                     {
                         Entry = entry,
                         Occurs = entry.TryGetOccurrenceOn(PlayerCalendarSelectedDate, out var occurrence),
                         Occurrence = occurrence
                     })
                     .Where(x => x.Occurs)
                     .OrderBy(x => x.Occurrence))
            PlayerCalendarEntries.Add(new PlayerCalendarEntryViewModel
            {
                Title = Limit(item.Entry.Title, 120),
                Description = Limit(item.Entry.Description, 220),
                Icon = VisualItemHelper.NormalizeIcon(item.Entry.Icon),
                ColorHex = item.Entry.ColorHex,
                TimeText = item.Occurrence.ToString("HH:mm")
            });

        OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
    }

    private void RefreshPlayerCalendarDayStates(DateTime today)
    {
        foreach (var day in PlayerCalendarDays)
        {
            day.IsToday = day.Date == today.Date;
            day.IsSelected = day.Date == PlayerCalendarSelectedDate.Date;
            day.HasEntries = _calendarEntries.Any(entry => entry.TryGetOccurrenceOn(day.Date, out _));
        }

        PlayerCalendarSelectedDateLabel =
            PlayerCalendarSelectedDate.ToString("dddd, dd.MM.yyyy", CultureInfo.CurrentCulture);
    }

    private void SelectPlayerCalendarDate(DateTime date)
    {
        PlayerCalendarSelectedDate = date.Date;
        if (PlayerCalendarMonth.Month != date.Month || PlayerCalendarMonth.Year != date.Year)
            PlayerCalendarMonth = new DateTime(date.Year, date.Month, 1);

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
            vm.KindLabel = "Timer";
            vm.PrimaryValue = FormatTimeSpan(timer.Remaining);
            vm.DetailText = $"Rest: {FormatTimeSpan(timer.Remaining)} · Dauer: {FormatTimeSpan(timer.Duration)}";
            vm.StatusText = timer.IsCompleted ? "Abgelaufen" : timer.IsRunning ? "Läuft" : "Pausiert";
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
            vm.KindLabel = "Wecker";
            vm.PrimaryValue = alarm.IsOverdue(currentTime) || alarm.IsTriggered
                ? "Jetzt!"
                : FormatTimeSpan(alarm.TimeRemaining(currentTime));
            var repeatDisplay = alarm.RepeatInterval.HasValue ? FormatTimeSpan(alarm.RepeatInterval.Value) : "einmalig";
            vm.DetailText = $"{alarm.TriggerAt:dddd, dd.MM.yyyy HH:mm} · Wdh.: {repeatDisplay}";
            vm.StatusText = alarm.IsTriggered ? "Ausgelöst" : "Bereit";
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
            vm.KindLabel = "OnTime";
            vm.PrimaryValue = interval.IsActive
                ? $"aktiv: {FormatTimeSpan(interval.Remaining)}"
                : $"nächster: {FormatTimeSpan(interval.Remaining)}";
            var repeatDisplay = interval.MaxRepeats is > 0
                ? $"{interval.CurrentRepeatNumber}/{interval.MaxRepeats}"
                : $"{interval.CurrentRepeatNumber}/∞";
            vm.DetailText =
                $"alle {FormatTimeSpan(interval.Interval)} für {FormatTimeSpan(interval.ActiveDuration)} · {repeatDisplay}";
            vm.StatusText = interval.IsCompleted ? "Fertig" :
                interval.IsActive ? "Aktiv" :
                interval.IsRunning ? "Läuft" : "Pausiert";
            vm.IsActive = interval.IsActive;
            vm.IsCompleted = interval.IsCompleted;
            vm.HasProgress = true;
            vm.Progress = interval.Progress;
            vm.IsBlinkActive = entry.Blink && interval.IsActive && VisualItemHelper.BlinkFrame();
        }
    }

    // ==================== Medien ====================

    private void OnMediaBegin(MediaHeaderDto header, string tempPath)
    {
        if (header.Kind == MediaHeaderDto.MediaKindAudio)
        {
            // Sounds bekommen bewusst KEIN Medien-Fenster und ersetzen kein gerade gezeigtes
            // Bild/Video - siehe PlaySound-Kommentar. Deshalb auch kein CurrentMediaFileName/
            // MediaWindowShouldShow hier, das ist exklusiv für den Bild/Video-Anzeige-Slot.
            PlaySound(tempPath, header);
            return;
        }

        // Event-Trigger-Medien (AddToGallery=false) haben Vorrang: sie überschreiben die Anzeige
        // vorübergehend und sperren die lokale Galerie-Navigation, bis sie wieder schließen
        // (media.cleared - siehe OnMediaCleared) - siehe design-decisions.md.
        _isShowingEventMedia = !header.AddToGallery;
        CurrentMediaFileName = Limit(header.FileName, 120);

        if (header.Kind == MediaHeaderDto.MediaKindVideo)
        {
            // Erst das Fenster zeigen (Größe/Position an Hauptfenster angeglichen, ggf.
            // fullscreen, Topmost gesetzt), DANN die Wiedergabe starten - der native
            // Video-Rendering-Surface (VideoView ist ein eingebettetes natives Child-Fenster,
            // kein reines Avalonia-Control) wird sonst mitten in der Wiedergabe umgroßt, was bei
            // hochauflösenden Videos zu einer falschen Fenstergröße und/oder einem Video führt,
            // das hinter statt in der Zeitliste erscheint.
            MediaWindowShouldShow?.Invoke();
            StartVideo(tempPath, header.MediaId, header.Loop, header.FileName, header.AddToGallery);
        }
        // Für Bilder: OnMediaCompleted wartet ab, ein Teilbild lässt sich nicht dekodieren.
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
            Log.Error(ex, "Bild konnte nicht dekodiert werden: {FileName}", header.FileName);
            MediaErrorMessage = $"Bild konnte nicht angezeigt werden: {ex.Message}";
            CurrentMediaKind = MediaKind.None;
        }
        finally
        {
            // Das Bild liegt jetzt als Bitmap im Speicher (Galerie-Eintrag oder nicht) - die
            // Temp-Datei wird nie wieder gebraucht, anders als bei Video (siehe StartVideo).
            DeleteFileQuietly(tempPath);
        }
    }

    private void StartVideo(string tempPath, string mediaId, bool loop, string fileName, bool addToGallery)
    {
        CurrentMediaBitmap = null;
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            Log.Error("Video kann nicht abgespielt werden: LibVLC nicht verfügbar ({Error})",
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
                // Einmalig für die Lebensdauer des Players - nicht pro Video, da derselbe
                // MediaPlayer über StartVideo hinweg wiederverwendet wird.
                MediaPlayer.LengthChanged += OnVlcLengthChanged;
                MediaPlayer.EndReached += OnVlcEndReached;
            }

            using var media = new Media(libVlc, tempPath);
            MediaPlayer.Play(media);

            // Vorheriges Video nur löschen, wenn es NICHT (mehr) Teil der Galerie ist - Galerie-
            // Videos bleiben auf der Platte, bis sie explizit entfernt werden (RetractGalleryItem),
            // damit lokales Zurücknavigieren keine erneute Übertragung braucht.
            if (!_gallery.Any(g => g.TempPath == previousTempPath)) DeleteFileQuietly(previousTempPath);

            MediaErrorMessage = null;
            CurrentMediaKind = MediaKind.Video;

            if (addToGallery) AddOrUpdateGalleryEntry(mediaId, fileName, MediaKind.Video, tempPath, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Video konnte nicht abgespielt werden: {TempPath}", tempPath);
            MediaErrorMessage = $"Video konnte nicht abgespielt werden: {ex.Message}";
            CurrentMediaKind = MediaKind.None;
        }
    }

    private void PlaySound(string tempPath, MediaHeaderDto header)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            Log.Error("Sound kann nicht abgespielt werden: LibVLC nicht verfügbar ({Error})",
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
                Log.Information("Sound-Wiedergabe gestartet: {MediaId} ({DurationMs} ms)", mediaId, e.Length);
                _ = _client.SendMediaPlaybackStartedAsync(mediaId, TimeSpan.FromMilliseconds(e.Length));
            };
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => OnSoundEndReached(mediaId, tempPath));

            _activeSoundPlayers[mediaId] = player;
            _soundRepeatsRemaining[mediaId] = header.Loop ? -1 : Math.Max(1, header.RepeatCount);

            using var media = new Media(libVlc, tempPath);
            VlcMediaService.ApplySoundTrim(media, header.TrimStartMs, header.TrimEndMs);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound konnte nicht abgespielt werden: {TempPath}", tempPath);
            DeleteFileQuietly(tempPath);
        }
    }

    /// <summary>
    ///     SL beendet diesen Sound gezielt (media.stopSound) - unabhängig davon, ob er gerade
    ///     natürlich endet oder loopt.
    /// </summary>
    private void StopSound(string mediaId)
    {
        if (!_activeSoundPlayers.Remove(mediaId, out var player)) return;

        _soundRepeatsRemaining.Remove(mediaId);
        Log.Information("Sound {MediaId} auf SL-Wunsch gestoppt", mediaId);
        player.Stop();
        player.Dispose();
    }

    private void ApplySoundVolume(string mediaId, int volume)
    {
        if (!_activeSoundPlayers.TryGetValue(mediaId, out var player)) return;

        player.Volume = Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    ///     Stoppt ALLE gerade laufenden Sounds - z.B. wenn die Verbindung zum Host abbricht,
    ///     damit dieses Gerät nicht unkontrolliert (z.B. eine Loop-Ambience) weiterspielt.
    /// </summary>
    private void StopAllSounds()
    {
        foreach (var (mediaId, player) in _activeSoundPlayers)
        {
            Log.Information("Sound {MediaId} gestoppt (StopAllSounds)", mediaId);
            player.Stop();
            player.Dispose();
        }

        _activeSoundPlayers.Clear();
        _soundRepeatsRemaining.Clear();
    }

    /// <summary>
    ///     Endlos (Loop) oder noch Wiederholungen übrig: derselbe Sound-Player startet lokal
    ///     neu. Sonst: aufräumen + Host benachrichtigen (der Host trackt Sounds nicht mehr aktiv, aber
    ///     die Meldung schadet nicht und bleibt für evtl. spätere Auswertung konsistent mit dem Video-Pfad).
    /// </summary>
    private void OnSoundEndReached(string mediaId, string tempPath)
    {
        if (!_activeSoundPlayers.TryGetValue(mediaId, out var player)) return;

        var remaining = _soundRepeatsRemaining.GetValueOrDefault(mediaId, 0);
        if (remaining < 0 || remaining > 1)
        {
            Log.Debug("Sound {MediaId} zu Ende - wird neu gestartet (verbleibend: {Remaining})", mediaId, remaining);
            if (remaining > 0) _soundRepeatsRemaining[mediaId] = remaining - 1;
            player.Stop();
            player.Play();
            return;
        }

        Log.Information("Sound {MediaId} zu Ende", mediaId);
        _activeSoundPlayers.Remove(mediaId);
        _soundRepeatsRemaining.Remove(mediaId);
        player.Stop();
        player.Dispose();
        DeleteFileQuietly(tempPath);
        _ = _client.SendMediaPlaybackEndedAsync(mediaId);
    }

    /// <summary>Meldet dem Host einmalig die tatsächliche (von LibVLC ermittelte) Videodauer.</summary>
    private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_playbackStartedReported || e.Length <= 0) return;
        _playbackStartedReported = true;

        var mediaId = _currentMediaId;
        if (string.IsNullOrEmpty(mediaId)) return;
        Log.Information("Video-Wiedergabe gestartet: {MediaId} ({DurationMs} ms)", mediaId, e.Length);
        _ = _client.SendMediaPlaybackStartedAsync(mediaId, TimeSpan.FromMilliseconds(e.Length));
    }

    /// <summary>
    ///     Loop: lokal neu starten. Sonst: dem Host melden, der das Medium daraufhin schließt/die pausierte Uhr
    ///     fortsetzt.
    /// </summary>
    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        var loop = _currentMediaLoop;
        var mediaId = _currentMediaId;

        // Läuft auf einem LibVLC-internen Callback-Thread - laut LibVLC-Doku sollte man von dort
        // aus nicht direkt wieder in den Player hineinrufen (Play/Stop), daher auf den UI-Thread verschieben.
        Dispatcher.UIThread.Post(() =>
        {
            if (loop)
            {
                Log.Debug("Video {MediaId} zu Ende - Loop, wird neu gestartet", mediaId);
                MediaPlayer?.Stop();
                MediaPlayer?.Play();
                return;
            }

            if (!string.IsNullOrEmpty(mediaId))
            {
                Log.Information("Video {MediaId} zu Ende - melde Host", mediaId);
                _ = _client.SendMediaPlaybackEndedAsync(mediaId);
            }
        });
    }

    private void OnMediaCleared()
    {
        Log.Information("Medium geschlossen (media.cleared)");
        StopVideo();
        CurrentMediaKind = MediaKind.None;
        CurrentMediaFileName = null;
        CurrentMediaBitmap = null;
        MediaErrorMessage = null;
        // Entsperrt die lokale Galerie-Navigation, falls dies ein Event-Medium war (Vorrang jetzt
        // vorbei) - der Host schickt danach i.d.R. ein media.highlight, um die Galerie an der
        // zuletzt gezeigten Stelle fortzusetzen (siehe MainWindowViewModel.ResumeGalleryAfterEventMedia).
        _isShowingEventMedia = false;
        MediaWindowShouldHide?.Invoke();
    }

    private void StopVideo()
    {
        MediaPlayer?.Stop();

        // Galerie-Videos bleiben auf der Platte, bis sie explizit entfernt werden (RetractGalleryItem).
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
    ///     SL hebt dieses Element hervor (media.highlight) - ein Sprung-Vorschlag für alle,
    ///     kein Zwang; ignoriert, falls das Element hier (noch) nicht bekannt ist.
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
                Log.Error(ex, "Galerie-Video konnte nicht abgespielt werden: {TempPath}", entry.TempPath);
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
    ///     Löscht alle noch vorhandenen lokalen Temp-Dateien dieser Sitzung (Galerie + aktuell
    ///     gezeigtes Ad-hoc-Video) und räumt zusätzlich verwaiste Temp-Dateien eines früheren, nicht
    ///     sauber beendeten Laufs auf (gleiches Namensmuster, siehe BeginMediaStream) - wird nur auf
    ///     ausdrückliche Nachfrage beim Schließen der App aufgerufen (siehe ClientMainWindow.OnClosing),
    ///     damit im temporären Verzeichnis nicht dauerhaft Medien-Reste liegen bleiben.
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
            Log.Warning(ex, "Verwaiste Temp-Dateien konnten nicht vollständig aufgeräumt werden");
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
    ///     Videos werden nie automatisch weggeschaltet - der Timer überspringt diesen Tick
    ///     einfach und prüft beim nächsten erneut.
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

    private static string FormatGameTime(DateTime time)
    {
        return time.ToString("dddd, dd.MM.yyyy — HH:mm:ss");
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
        PlayerCalendarMonth = PlayerCalendarMonth.AddMonths(-1);
        RefreshPlayerCalendar();
    }

    [RelayCommand]
    private void NextPlayerCalendarMonth()
    {
        PlayerCalendarMonth = PlayerCalendarMonth.AddMonths(1);
        RefreshPlayerCalendar();
    }

    // ==================== Galerie: session-eigene Liste gesendeter Bilder/Videos ====================
    // Jeder Client navigiert unabhängig und rein lokal durch diese Liste (Pfeiltasten/Klick, siehe
    // Views/MediaWindow.axaml.cs) - ohne den Host zu fragen. Ein media.highlight vom Host ist nur
    // ein Sprung-Vorschlag, keine Sperre: danach kann sich der Spieler wieder frei wegbewegen.
    // Event-Trigger-Medien (AddToGallery=false) sind NIE Teil dieser Liste und blockieren die
    // Navigation nur, solange sie selbst angezeigt werden (siehe _isShowingEventMedia).

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