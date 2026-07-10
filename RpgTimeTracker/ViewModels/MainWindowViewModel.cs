using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    private const string LibraryManifestFileName = "bibliothek.json";
    private const string MapManifestEntryName = "map.json";

    private const int MaxActionStatusHistory = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions LibraryManifestJsonOptions = new() { WriteIndented = true };

    // ==================== Sounds: independent of the image/video "current medium" slot ====================
    // Sounds NEVER run via CurrentMediaKind/SetCurrentMediaStatus - that slot is exclusive to
    // image/video (always only one at a time, replaces the previous one on the next send). A
    // sound must neither replace a currently shown image/video nor itself be stopped by a new
    // image/video or another sound, and several should be able to run in parallel -
    // that's why each sound gets its own short-lived MediaPlayer instead of the
    // shared MediaPlayer field used for video. Sounds accordingly never appear in the
    // player window (neither locally at the GM nor at the client - see ClientMainWindowViewModel.PlaySound),
    // but only on the GM side in the "currently playing sounds" panel (ActivePlayingSounds).

    private readonly Dictionary<string, MediaPlayer> _activeLocalSoundPlayers = new();
    private readonly DispatcherTimer _blinkTimer;

    private readonly GameClockService _clock;

    /// <summary>
    ///     All available themes (bundled SampleThemes + GM's own, see ThemeDefinitionLoader),
    ///     keyed by the display name shown in ThemeOptions or by the theme id (for
    ///     persistence/ambience/network sync). There is no longer a separate list of "built-in" themes -
    ///     all go through the same JSON loading mechanism.
    /// </summary>
    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesByDisplayName = new();

    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesById = new();

    /// <summary>Items whose currently running occurrence has already been warned about.</summary>
    private readonly HashSet<Guid> _headsUpFired = [];

    // ==================== Heads-up warning ("something is about to happen") - purely GM-local, never sent to players ====================

    /// <summary>
    ///     Remaining time at the last tick, per item id - detects when an item was "re-armed"
    ///     (reset/new alarm cycle), to re-arm the heads-up warning for it.
    /// </summary>
    private readonly Dictionary<Guid, TimeSpan> _headsUpLastRemaining = new();

    /// <summary>
    ///     Whether an infinite repeat (SoundRepeatCount == 0) is currently running for an item, looked up here by id,
    ///     to cancel it when the item is reset.
    /// </summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _infiniteSoundLoops = new();

    /// <summary>
    ///     Analogous for the item's own "sound" selection (timer/alarm/interval field, see
    ///     PlaySound(Guid,...)): which sound library sound is currently playing for which item.
    /// </summary>
    private readonly Dictionary<Guid, string> _itemSoundMediaIds = new();

    /// <summary>
    ///     Most recently applied time jumps (LIFO) - allows undoing several jumps in a row, purely
    ///     local/session-based, not persisted. A new jump (JumpBy) clears the redo stack,
    ///     since a new jump invalidates the "future" of the previous redo history - classic
    ///     undo/redo behavior as in text/graphics editors.
    /// </summary>
    private readonly Stack<TimeSpan> _jumpUndoStack = new();

    /// <summary>Jumps undone via UndoJump, restorable via RedoJump (see _jumpUndoStack).</summary>
    private readonly Stack<TimeSpan> _jumpRedoStack = new();

    private readonly Dictionary<string, string> _localSoundCleanupPaths = new();

    /// <summary>
    ///     Plays a sent sound locally at the GM whenever the local player window is open (same
    ///     condition as ShouldShowMediaLocally for image/video, but here without UI binding,
    ///     since sounds are deliberately not displayed) - regardless of whether real player
    ///     clients are also connected, so the GM always hears what's playing.
    /// </summary>
    /// <summary>
    ///     Remaining playbacks per MediaId (-1 = endless/loop, otherwise counts down on every
    ///     natural end - see OnLocalSoundEnded).
    /// </summary>
    private readonly Dictionary<string, int> _localSoundRepeatsRemaining = new();

    private readonly TcpPlayerServerService _playerServer;

    /// <summary>
    ///     Associates a triggering TriggerMediaConfig (event medium) with the MediaId of the
    ///     currently running sound, so that StopMediaIfTriggeredBy also ends it when the timer/alarm/
    ///     interval is reset - analogous to the image/video slot, but for the decoupled sound path.
    /// </summary>
    private readonly Dictionary<TriggerMediaConfig, string> _triggerSoundMediaIds = new();

    private CancellationTokenSource? _actionStatusClearCts;

    /// <summary>Short, self-clearing success feedback (e.g. "timer created") - purely informational, not an error.</summary>
    [ObservableProperty] private string? _actionStatusMessage;

    /// <summary>
    ///     Which TriggerMediaConfig (if any) triggered the currently shown medium - see
    ///     StopMediaIfTriggeredBy.
    /// </summary>
    private TriggerMediaConfig? _activeTriggerMediaSource;

    /// <summary>
    ///     Switches the background of the currently active GM JSON theme based on time of day (06-18 = first
    ///     named background, otherwise the last one) - a no-op for built-in themes or themes with only one
    ///     background.
    /// </summary>
    [ObservableProperty] private bool _ambienceAutomationEnabled;

    /// <summary>
    ///     File name of the most recently set ambience background - prevents unnecessarily reloading the same image on
    ///     every tick.
    /// </summary>
    private string? _ambienceCurrentFileName;

    [ObservableProperty] private DateTime _calendarMonth;
    [ObservableProperty] private DateTime _calendarSelectedDate;
    private CancellationTokenSource? _calendarSyncCts;

    [ObservableProperty] private bool _canUndoJump;
    [ObservableProperty] private bool _canRedoJump;

    [ObservableProperty] private string? _clockErrorMessage;

    [ObservableProperty] private int _connectedClientCount;

    /// <summary>
    ///     Which gallery item was last shown/highlighted - for the automatic
    ///     resumption when an event medium ends (see StopMediaIfTriggeredBy).
    /// </summary>
    private string? _currentGalleryMediaId;

    [ObservableProperty] private string _currentGameTimeText = string.Empty;

    [ObservableProperty] private Bitmap? _currentMediaBitmap;

    [ObservableProperty] private string? _currentMediaFileName;

    // ==================== Media: status tracking + optional local preview ====================

    /// <summary>
    ///     Whether CurrentMediaLocalPath is a one-off cache copy (may be deleted)
    ///     or a persistent library file (must NOT be deleted).
    /// </summary>
    private bool _currentMediaIsOwnedCache;

    // ==================== Media (image/video for players) ====================
    // The player client displays media, and the Host additionally shows the same thing itself
    // (above the player window) whenever the local player window is open - regardless of
    // whether a real client is connected, so the GM can always see what's currently being shown
    // instead of only when testing solo. See ShouldShowMediaLocally.

    [ObservableProperty] private MediaKind _currentMediaKind = MediaKind.None;

    [ObservableProperty] private string? _currentMediaLocalPath;

    [ObservableProperty] private decimal _headsUpLeadMinutes;

    [ObservableProperty] private string? _headsUpMessage;

    private CancellationTokenSource? _headsUpMessageClearCts;

    /// <summary>GM-local heads-up warning before a timer/alarm/on-time entry triggers - never sent to players.</summary>
    [ObservableProperty] private bool _headsUpWarningEnabled;

    /// <summary>Writes to ThemeSettingsDto.LastSaveFilePath when the window closes (see TryAutoSaveOnClose).</summary>
    [ObservableProperty] private bool _autoSaveOnCloseEnabled;

    /// <summary>Loads ThemeSettingsDto.LastSaveFilePath on startup (see TryAutoLoadOnStartup).</summary>
    [ObservableProperty] private bool _autoLoadOnStartupEnabled;

    [ObservableProperty] private bool _isClockRunning;

    [ObservableProperty] private bool _isLocalPlayerFullscreen;

    [ObservableProperty] private bool _isNetworkServerRunning;

    [ObservableProperty] private bool _isPlayerWindowOpen;

    // Free-form time jump, e.g. "08:00:00" forward or "-1.00:00:00" back one day.
    [ObservableProperty] private string _jumpAmountText = "08:00:00";

    // Free-text field for manually setting the game time's date/time.
    [ObservableProperty] private string _manualDateTimeText;

    [ObservableProperty] private string? _mediaErrorMessage;

    [ObservableProperty] private MediaPlayer? _mediaPlayer;

    /// <summary>
    ///     LAN address under which the server is reachable - only set while it is running
    ///     (see ToggleNetworkServer). For the status bar, so the GM doesn't have to look it up themselves.
    /// </summary>
    [ObservableProperty] private string? _networkServerAddress;

    [ObservableProperty] private int _networkServerPort = TcpPlayerServerService.DefaultPort;

    [ObservableProperty] private string _networkServerStatus = LocalizationService.Get("MainWindowViewModel.Network.ServerOff");

    [ObservableProperty] private DateTimeOffset? _newAlarmDate;

    [ObservableProperty] private string _newAlarmDateTime;

    [ObservableProperty] private decimal _newAlarmRepeatHours;

    [ObservableProperty] private string _newAlarmRepeatInterval = string.Empty;

    [ObservableProperty] private decimal _newAlarmRepeatMinutes;

    [ObservableProperty] private decimal _newAlarmRepeatSeconds;

    [ObservableProperty] private TimeSpan? _newAlarmTime;

    [ObservableProperty] private string _newIntervalActiveDuration = "00:01:00";

    [ObservableProperty] private decimal _newIntervalActiveHours;

    [ObservableProperty] private decimal _newIntervalActiveMinutes = 1;

    [ObservableProperty] private decimal _newIntervalActiveSeconds;

    [ObservableProperty] private string _newIntervalInterval = "00:10:00";

    [ObservableProperty] private decimal _newIntervalIntervalHours;

    [ObservableProperty] private decimal _newIntervalIntervalMinutes = 10;

    [ObservableProperty] private decimal _newIntervalIntervalSeconds;

    [ObservableProperty] private string _newIntervalMaxRepeats = string.Empty;

    [ObservableProperty] private string _newMarkerName = LocalizationService.Get("MainWindowViewModel.Defaults.NewMarkerName");

    [ObservableProperty] private string _newMarkerTime = "06:00";

    [ObservableProperty] private string _newTimerDuration = "00:10:00";

    [ObservableProperty] private decimal _newTimerDurationHours;

    [ObservableProperty] private decimal _newTimerDurationMinutes = 10;

    [ObservableProperty] private decimal _newTimerDurationSeconds;

    private CancellationTokenSource? _pendingVideoFallbackCts;

    // ==================== Video-end tracking (loop vs. end+close, optional clock pause) ====================
    // Applies to EVERY sent video (ad-hoc, library, event trigger): a non-looping
    // video is automatically closed after it ends; if pauseClockUntilEnd is also set,
    // the game time remains paused until then. Whichever fires first decides: a client's
    // report (media.playbackEnded, see
    // TcpPlayerServerService.ClientReportedPlaybackEnded) OR the local host player's
    // MediaPlayer.EndReached (see CreateLocalMediaPlayer) - a duration estimated locally via
    // LibVLC serves only as a safety net, in case neither a client is connected nor local
    // playback is happening, or both reports get lost.

    private string? _pendingVideoMediaId;
    private bool _pendingVideoPauseClock;
    [ObservableProperty] private string _playerCalendarMonthLabel = string.Empty;
    [ObservableProperty] private string _playerCalendarSelectedDateLabel = string.Empty;

    [ObservableProperty] private string _playerHeaderSubtitle;

    [ObservableProperty] private string _playerHeaderTitle;

    // ==================== Session log (GM-local only, only on explicit opt-in) ====================

    [ObservableProperty] private bool _recordSessionLog;

    /// <summary>Whether remote clients should currently be in fullscreen.</summary>
    [ObservableProperty] private bool _remoteFullscreen;

    [ObservableProperty] private CalendarEntryViewModel? _selectedCalendarEntry;

    [ObservableProperty] private string _selectedThemeOption;

    /// <summary>Whether an ad-hoc sent video (not from the library) should loop at the end.</summary>
    [ObservableProperty] private bool _sendMediaLoop;

    /// <summary>
    ///     Whether an ad-hoc sent sound (not from the sound library) should loop at the end -
    ///     deliberately separate from SendMediaLoop, so the two tabs (library/sounds) don't
    ///     affect each other.
    /// </summary>
    [ObservableProperty] private bool _sendSoundLoop;

    /// <summary>Display name transmitted in the LAN/mDNS announcement (see ToggleNetworkServer).</summary>
    [ObservableProperty] private string _serverName = "RpgTimeTracker";

    /// <summary>
    ///     Optional PIN a client must send when connecting (session.hello) - empty
    ///     means "no PIN required" (default, backward-compatible). Checked by
    ///     TcpPlayerServerService.PerformHandshakeAsync against the value sent by the client.
    /// </summary>
    [ObservableProperty] private string _connectionPin = string.Empty;

    /// <summary>UI language, independent from the PlayerClient's own setting (see ClientMainWindowViewModel).</summary>
    [ObservableProperty] private string _selectedLanguageOption = "English";

    /// <summary>
    ///     Player-side fog render style (see issue #22, MapDisplayViewModel) - one global GM
    ///     preference in the Settings tab, pushed to all clients live (map.renderStyleChanged)
    ///     and on connect (session.snapshot).
    /// </summary>
    [ObservableProperty] private string _fogColorHex = "#0C0C0C";

    [ObservableProperty] private int _fogOpacityPercent = 100;
    [ObservableProperty] private double _fogBlurRadius;
    [ObservableProperty] private bool _fogBlurEnabled = true;

    [ObservableProperty] private bool _showPlayerCalendarView;

    /// <summary>
    ///     Seconds per image for automatic advancement across all clients (0 = manual).
    ///     Never applies to videos (see design-decisions.md).
    /// </summary>
    [ObservableProperty] private double _slideshowSecondsPerImage;

    private DispatcherTimer? _slideshowTimer;

    [ObservableProperty] private double _speedMultiplier = 1.0;

    [ObservableProperty] private decimal _speedMultiplierInput = 1.0m;

    /// <summary>
    ///     Only one test runs at a time - holds the reference so the player is not
    ///     prematurely garbage-collected (no dictionary entry needed like for real sounds).
    /// </summary>
    private MediaPlayer? _testSoundPlayer;

    public MainWindowViewModel()
    {
        // Reasonable fantasy start time, can be changed immediately.
        var start = DateTime.Now;
        _clock = new GameClockService(start);
        _clock.Tick += OnClockTick;
        _playerServer = new TcpPlayerServerService(BuildSessionSnapshot, BuildClockHeartbeat, () => ConnectionPin);
        // Time is only sent over the network on real jumps (not on every tick) -
        // the client continues calculating time locally from the last jump using the known speed,
        // see RpgTimeTracker.Shared.Services.GameClockService on the client side.
        _clock.Jumped += newTime => _ = _playerServer.PublishClockTimeJumpedAsync(newTime);
        _playerServer.ClientCountChanged += count => Dispatcher.UIThread.Post(() => ConnectedClientCount = count);
        _playerServer.ClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateConnectedClients(clients));
        // Decisive for video-end tracking (loop/close, optional clock pause) - see BeginVideoTracking.
        _playerServer.ClientReportedPlaybackEnded +=
            mediaId => Dispatcher.UIThread.Post(() => ResolvePendingVideo(mediaId));

        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _blinkTimer.Tick += (_, _) => RefreshBlinkStates();
        _blinkTimer.Start();
        _headsUpLeadMinutes = 2;
        _manualDateTimeText = start.ToString("yyyy-MM-dd HH:mm:ss");
        var defaultAlarm = start.AddHours(8);
        _newAlarmDateTime = defaultAlarm.ToString("yyyy-MM-dd HH:mm:ss");
        _newAlarmDate = new DateTimeOffset(defaultAlarm.Date);
        _newAlarmTime = defaultAlarm.TimeOfDay;
        CurrentGameTimeText = FormatGameTime(start);

        var settings = ThemeSettingsService.LoadSettings();
        _playerHeaderTitle = string.IsNullOrWhiteSpace(settings.PlayerHeaderTitle)
            ? LocalizationService.Get("MainWindowViewModel.Defaults.PlayerHeaderTitle")
            : settings.PlayerHeaderTitle;
        _playerHeaderSubtitle = string.IsNullOrWhiteSpace(settings.PlayerHeaderSubtitle)
            ? LocalizationService.Get("MainWindowViewModel.Defaults.PlayerHeaderSubtitle")
            : settings.PlayerHeaderSubtitle;
        _headsUpWarningEnabled = settings.HeadsUpWarningEnabled;
        _headsUpLeadMinutes = (decimal)settings.HeadsUpLeadMinutes;
        _serverName = string.IsNullOrWhiteSpace(settings.ServerName) ? "RpgTimeTracker" : settings.ServerName;
        _connectionPin = settings.ConnectionPin;
        _autoSaveOnCloseEnabled = settings.AutoSaveOnCloseEnabled;
        _autoLoadOnStartupEnabled = settings.AutoLoadOnStartupEnabled;
        _fogColorHex = string.IsNullOrWhiteSpace(settings.FogColorHex) ? "#0C0C0C" : settings.FogColorHex;
        _fogOpacityPercent = settings.FogOpacityPercent;
        _fogBlurRadius = settings.FogBlurRadius;
        _fogBlurEnabled = settings.FogBlurEnabled;
        MapDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(_fogColorHex, _fogOpacityPercent), _fogBlurRadius,
            _fogBlurEnabled);

        foreach (var entry in settings.MediaLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            if (!Enum.TryParse<MediaKind>(entry.Kind, out var kind) || kind == MediaKind.Audio) continue;

            MediaLibrary.Add(new MediaLibraryItemViewModel(
                entry.Name, entry.Path, kind, entry.MimeType, entry.Loop,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged));
        }

        foreach (var entry in settings.SoundLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            SoundLibrary.Add(CreateSoundLibraryItem(entry.Name, entry.Icon, entry.Path, entry.MimeType, entry.Loop,
                entry.Volume, entry.RepeatCount, entry.TrimStartMs / 1000.0, entry.TrimEndMs / 1000.0));
        }

        foreach (var mapEntry in settings.MapLibrary)
        {
            var map = new MapItemViewModel(mapEntry.Id, mapEntry.Name, RemoveMap, _ => SaveMapLibrarySettings());
            foreach (var floorEntry in mapEntry.Floors)
            {
                if (!File.Exists(floorEntry.ImagePath) || !File.Exists(floorEntry.FogPath)) continue;

                map.Floors.Add(new MapFloorItemViewModel(
                    floorEntry.Id, floorEntry.Name, floorEntry.ImagePath, floorEntry.FogPath,
                    floorEntry.CellSizePx, floorEntry.GridWidth, floorEntry.GridHeight,
                    LoadMapFloorThumbnail(floorEntry.ImagePath),
                    f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings()));
            }

            MapLibrary.Add(map);
        }

        // One-time migration of old data so that nothing is lost when switching to the separate
        // sound library: (a) sounds from the old "+ add sound" settings function,
        // (b) audio entries that had ended up in the image/video library before the split.
        var needsMigrationSave = false;
        if (!settings.LegacyCustomSoundsMigrated)
        {
            foreach (var legacy in settings.CustomSounds)
            {
                if (!File.Exists(legacy.Path) || SoundLibrary.Any(s => s.LocalPath == legacy.Path)) continue;
                SoundLibrary.Add(CreateSoundLibraryItem(legacy.Name, VisualItemHelper.IconTimer, legacy.Path, "audio/*",
                    false, 100));
                needsMigrationSave = true;
            }

            settings.LegacyCustomSoundsMigrated = true;
            needsMigrationSave = true;
        }

        foreach (var legacyAudio in settings.MediaLibrary.Where(e => e.Kind == nameof(MediaKind.Audio)))
        {
            if (!File.Exists(legacyAudio.Path) || SoundLibrary.Any(s => s.LocalPath == legacyAudio.Path)) continue;
            SoundLibrary.Add(CreateSoundLibraryItem(legacyAudio.Name, VisualItemHelper.IconTimer, legacyAudio.Path,
                legacyAudio.MimeType, legacyAudio.Loop, 100));
            needsMigrationSave = true;
        }

        SyncSoundServiceLibrary();

        if (needsMigrationSave)
        {
            settings.SoundLibrary = SoundLibrary
                .Select(s => new ThemeSettingsService.SoundLibraryEntryDto
                {
                    Name = s.Name, Icon = s.Icon, Path = s.LocalPath, MimeType = s.MimeType, Loop = s.Loop,
                    Volume = s.Volume, RepeatCount = s.RepeatCount, TrimStartMs = (long)(s.TrimStartSeconds * 1000),
                    TrimEndMs = (long)(s.TrimEndSeconds * 1000)
                })
                .ToList();
            settings.MediaLibrary = settings.MediaLibrary.Where(e => e.Kind != nameof(MediaKind.Audio)).ToList();
            ThemeSettingsService.SaveSettings(settings);
            Log.Information("Migrated old sound data into the new sound library ({Count} entries)",
                SoundLibrary.Count);
        }

        foreach (var loaded in ThemeDefinitionLoader.LoadAll())
        {
            // Bundled themes show their short, neutral name (PickerLabel, e.g.
            // "Shadowrun / Cyberpunk"); a GM's own theme gets a suffix hint so
            // the list still shows what was bundled with the app and what wasn't.
            // A bundled theme's PickerLabel can be overridden per-language via
            // "SampleTheme.<id>.PickerLabel" - falls back to the JSON value for any
            // theme not covered (future additions, GM's own themes).
            var jsonBaseName = loaded.Definition.PickerLabel ?? loaded.Definition.DisplayName;
            var baseName = loaded.IsSample &&
                           LocalizationService.TryGet($"SampleTheme.{loaded.Definition.Id}.PickerLabel", out var localized)
                ? localized
                : jsonBaseName;
            var label = loaded.IsSample ? baseName : $"{baseName} (custom theme)";
            ThemeOptions.Add(label);
            _themesByDisplayName[label] = loaded;
            _themesById[loaded.Definition.Id] = loaded;
        }

        var resolvedTheme = ThemeDefinitionLoader.Resolve(ThemeSettingsService.LoadLastThemeId())
                             ?? ThemeDefinitionLoader.Resolve("shadowrun");
        if (resolvedTheme is { } theme)
        {
            // Don't compare via ReferenceEquals: Resolve() internally calls LoadAll() again and
            // thereby creates fresh ThemeDefinitionDto instances, independent of the ones above in
            // _themesByDisplayName - the id value is the only stable comparison point.
            _selectedThemeOption = _themesByDisplayName
                .First(kv => kv.Value.Definition.Id == theme.Definition.Id).Key;
            CurrentThemeId = theme.Definition.Id;
        }
        else
        {
            // Should practically never happen (SampleThemes completely missing from the application directory) -
            // the field must still never remain uninitialized, though ThemeOptions is empty anyway in that case.
            _selectedThemeOption = string.Empty;
        }

        _ambienceAutomationEnabled = settings.AmbienceAutomationEnabled;
        _selectedLanguageOption = LanguageDisplayName(settings.Language);

        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerDawn"), new TimeSpan(6, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerNoon"), new TimeSpan(12, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerEvening"), new TimeSpan(18, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerMidnight"), TimeSpan.Zero);

        _calendarMonth = new DateTime(start.Year, start.Month, 1);
        _calendarSelectedDate = start.Date;
        RefreshCalendarViews();

        LocalizationService.LanguageChanged += OnLanguageChanged;

        TryAutoLoadOnStartup(settings);
    }

    /// <summary>
    ///     Refreshes every computed display property that calls LocalizationService.Get() (e.g.
    ///     ToggleClockLabel, ToggleNetworkServerLabel, ServerStatusSummary) - a language switch
    ///     alone doesn't raise property-changed on its own, since these properties only depend on
    ///     other observable state, not on the active language. Also refreshes every live child
    ///     item ViewModel (timers/alarms/intervals/timeline/library/sounds/gallery/calendar),
    ///     since each has its own Get()-based computed properties (status/kind labels) that are
    ///     equally unaffected by a language switch on their own.
    /// </summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);

        foreach (var timer in Timers) timer.RefreshLocalizedText();
        foreach (var alarm in Alarms) alarm.RefreshLocalizedText();
        foreach (var interval in IntervalEvents) interval.RefreshLocalizedText();
        foreach (var item in TimelineItems) item.RefreshLocalizedText();
        foreach (var entry in CalendarEntries) entry.RefreshLocalizedText();
        foreach (var media in MediaLibrary) media.RefreshLocalizedText();
        foreach (var sound in SoundLibrary) sound.RefreshLocalizedText();
        foreach (var sent in SentMediaItems) sent.RefreshLocalizedText();
        foreach (var client in ConnectedClientItems) client.RefreshLocalizedText();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ObservableCollection<AlarmItemViewModel> Alarms { get; } = [];
    public ObservableCollection<IntervalEventItemViewModel> IntervalEvents { get; } = [];

    /// <summary>Combined list for the GM window.</summary>
    public ObservableCollection<TimelineDisplayItemViewModel> TimelineItems { get; } = [];

    /// <summary>Filtered combined list for the player window.</summary>
    public ObservableCollection<TimelineDisplayItemViewModel> PlayerTimelineItems { get; } = [];

    public ObservableCollection<JumpMarkerItemViewModel> JumpMarkers { get; } = [];
    public ObservableCollection<CalendarEntryViewModel> CalendarEntries { get; } = [];
    public ObservableCollection<CalendarEntryViewModel> CalendarEntriesForSelectedDate { get; } = [];
    public ObservableCollection<PlayerCalendarDayViewModel> PlayerCalendarDays { get; } = [];
    public ObservableCollection<PlayerCalendarEntryViewModel> PlayerCalendarEntries { get; } = [];

    // Display names of the styles for the ComboBox; populated in the constructor from
    // ThemeDefinitionLoader.LoadAll() (see _themesByDisplayName) - no longer a fixed list, since there
    // are no more "built-in" themes separate from JSON themes.
    public ObservableCollection<string> ThemeOptions { get; } = [];

    /// <summary>Display names for the language ComboBox; native names on purpose, not translated by LocalizationService itself.</summary>
    public ObservableCollection<string> LanguageOptions { get; } = ["English", "Deutsch"];

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public IReadOnlyList<string> CalendarRecurrenceOptions => CalendarEntryViewModel.RecurrenceOptions;

    public bool HasAmbienceBackgrounds =>
        _themesById.TryGetValue(CurrentThemeId ?? string.Empty, out var loaded) &&
        loaded.Definition.Backgrounds.Count > 1;

    private string? CurrentThemeId { get; set; }

    public string ToggleNetworkServerLabel => IsNetworkServerRunning
        ? LocalizationService.Get("MainWindowViewModel.Network.StopServer")
        : LocalizationService.Get("MainWindowViewModel.Network.StartServer");

    /// <summary>Compact summary for the status bar at the bottom of the window.</summary>
    public string ServerStatusSummary => IsNetworkServerRunning
        ? string.Format(LocalizationService.Get("MainWindowViewModel.Network.ServerStatusSummary"),
            NetworkServerAddress ?? "?", NetworkServerPort, ConnectedClientCount)
        : LocalizationService.Get("MainWindowViewModel.Network.ServerStopped");

    public NewItemBasicsViewModel NewTimerBasics { get; } =
        new(LocalizationService.Get("MainWindowViewModel.Defaults.NewTimerName"), VisualItemHelper.IconTimer);

    public TriggerMediaConfig NewTimerTriggerMedia { get; } = new();

    public NewItemBasicsViewModel NewAlarmBasics { get; } =
        new(LocalizationService.Get("MainWindowViewModel.Defaults.NewAlarmName"), VisualItemHelper.IconAlarm);

    public TriggerMediaConfig NewAlarmTriggerMedia { get; } = new();

    public NewItemBasicsViewModel NewIntervalBasics { get; } =
        new(LocalizationService.Get("MainWindowViewModel.Defaults.NewIntervalName"), VisualItemHelper.IconOnTime,
            "#FFD45A", true);

    public TriggerMediaConfig NewIntervalTriggerMedia { get; } = new();

    public ObservableCollection<ConnectedClientItemViewModel> ConnectedClientItems { get; } = [];
    public bool HasNoConnectedClients => ConnectedClientItems.Count == 0;

    public bool HasMedia => CurrentMediaKind != MediaKind.None;
    public bool HasCalendarEntries => CalendarEntries.Count > 0;
    public bool HasNoCalendarEntries => CalendarEntries.Count == 0;
    public bool HasCalendarEntriesForSelectedDate => CalendarEntriesForSelectedDate.Count > 0;
    public bool HasSelectedCalendarEntry => SelectedCalendarEntry is not null;

    /// <summary>A shared fullscreen toggle makes sense as soon as something is controllable locally or remotely.</summary>
    public bool CanToggleDisplayFullscreen => IsPlayerWindowOpen || IsNetworkServerRunning;

    public bool IsDisplayFullscreenActive => RemoteFullscreen || IsLocalPlayerFullscreen;

    public string DisplayFullscreenLabel => IsDisplayFullscreenActive
        ? LocalizationService.Get("MainWindowViewModel.Display.ExitPlayerFullscreen")
        : LocalizationService.Get("MainWindowViewModel.Display.PlayerFullscreen");

    /// <summary>
    ///     The local player window is open - shown regardless of whether real clients are
    ///     connected too, so the GM can always see exactly what players are seeing while running
    ///     the session, not just when testing solo.
    /// </summary>
    public bool ShouldShowMediaLocally => HasMedia && IsPlayerWindowOpen;

    public bool ShowLocalImage => ShouldShowMediaLocally && CurrentMediaKind == MediaKind.Image;
    public bool ShowLocalVideo => ShouldShowMediaLocally && CurrentMediaKind == MediaKind.Video;

    public string ToggleClockLabel => IsClockRunning
        ? LocalizationService.Get("MainWindowViewModel.Clock.PauseButton")
        : LocalizationService.Get("MainWindowViewModel.Clock.StartButton");

    public string NextEventJumpLabel => GetNextEventDelta() is { } delta
        ? string.Format(LocalizationService.Get("MainWindowViewModel.Clock.NextEventJump"), FormatTimeSpan(delta))
        : LocalizationService.Get("MainWindowViewModel.Clock.NoNextEvent");

    public string ClockStatusText => IsClockRunning
        ? LocalizationService.Get("MainWindowViewModel.Clock.Running")
        : LocalizationService.Get("MainWindowViewModel.Clock.Paused");

    // ==================== Media library (preselected, show via double-click) ====================

    public ObservableCollection<MediaLibraryItemViewModel> MediaLibrary { get; } = [];

    public bool HasNoMediaLibraryItems => MediaLibrary.Count == 0;

    // ==================== Sound library (separate from the image/video library) ====================

    public ObservableCollection<SoundLibraryItemViewModel> SoundLibrary { get; } = [];

    public bool HasNoSoundLibraryItems => SoundLibrary.Count == 0;

    // ==================== Map library (fog-of-war maps, multiple floors each) ====================

    public ObservableCollection<MapItemViewModel> MapLibrary { get; } = [];

    public bool HasNoMaps => MapLibrary.Count == 0;

    [ObservableProperty] private MapItemViewModel? _selectedMap;

    /// <summary>Default grid cell size for a newly added floor - configurable per floor later
    ///     (e.g. via the SL Map Editor Window in a later milestone), not yet exposed in this UI.</summary>
    private const int DefaultMapCellSizePx = 16;

    [RelayCommand]
    private void AddMap()
    {
        var map = new MapItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewMapName"), RemoveMap,
            _ => SaveMapLibrarySettings());
        MapLibrary.Add(map);
        SelectedMap = map;
        SaveMapLibrarySettings();
    }

    private void RemoveMap(MapItemViewModel map)
    {
        MapLibrary.Remove(map);
        if (SelectedMap == map) SelectedMap = null;

        try
        {
            var mapDirectory = Path.Combine(ThemeSettingsService.MapLibraryDirectory, map.Id.ToString("N"));
            if (Directory.Exists(mapDirectory)) Directory.Delete(mapDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not delete map directory for {MapName} ({MapId})", map.Name, map.Id);
        }

        SaveMapLibrarySettings();
    }

    public async Task AddFloorToMapAsync(MapItemViewModel map, string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out _) || kind != MediaKind.Image)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMapFloorFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var mapDirectory = Path.Combine(ThemeSettingsService.MapLibraryDirectory, map.Id.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            var floorId = Guid.NewGuid();
            var imagePath = Path.Combine(mapDirectory, $"{floorId:N}{Path.GetExtension(sourcePath)}");
            await using (var source = File.OpenRead(sourcePath))
            await using (var target = File.Create(imagePath))
            {
                await source.CopyToAsync(target);
            }

            int gridWidth, gridHeight;
            await using (var dimensionStream = File.OpenRead(imagePath))
            {
                var bitmap = new Bitmap(dimensionStream);
                gridWidth = Math.Max(1, (bitmap.PixelSize.Width + DefaultMapCellSizePx - 1) / DefaultMapCellSizePx);
                gridHeight = Math.Max(1, (bitmap.PixelSize.Height + DefaultMapCellSizePx - 1) / DefaultMapCellSizePx);
            }

            var fogPath = Path.Combine(mapDirectory, $"{floorId:N}.fog");
            var startingFog = FogMask.CreateFullyHidden(gridWidth, gridHeight, DefaultMapCellSizePx);
            await File.WriteAllBytesAsync(fogPath, FogMaskSerializer.Serialize(startingFog));

            var floor = new MapFloorItemViewModel(
                floorId, Path.GetFileNameWithoutExtension(sourcePath), imagePath, fogPath,
                DefaultMapCellSizePx, gridWidth, gridHeight, LoadMapFloorThumbnail(imagePath),
                f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings());
            map.Floors.Add(floor);
            SaveMapLibrarySettings();
            Log.Information("Floor added to map {MapName}: {FloorName} ({GridWidth}x{GridHeight} cells)",
                map.Name, floor.Name, gridWidth, gridHeight);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedFloorToMap"), floor.Name, map.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Floor could not be added to map {MapName} ({SourcePath})", map.Name, sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    private void RemoveFloorFromMap(MapItemViewModel map, MapFloorItemViewModel floor)
    {
        map.Floors.Remove(floor);

        try
        {
            if (File.Exists(floor.ImagePath)) File.Delete(floor.ImagePath);
            if (File.Exists(floor.FogPath)) File.Delete(floor.FogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not delete floor files for {FloorName} ({FloorId})", floor.Name, floor.Id);
        }

        SaveMapLibrarySettings();
    }

    private static Bitmap? LoadMapFloorThumbnail(string imagePath)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            return Bitmap.DecodeToWidth(stream, 96);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning(ex, "Map floor thumbnail could not be loaded ({ImagePath})", imagePath);
            return null;
        }
    }

    private void SaveMapLibrarySettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.MapLibrary = MapLibrary.Select(map => new ThemeSettingsService.MapLibraryEntryDto
        {
            Id = map.Id,
            Name = map.Name,
            Floors = map.Floors.Select(floor => new ThemeSettingsService.MapFloorEntryDto
            {
                Id = floor.Id,
                Name = floor.Name,
                ImagePath = floor.ImagePath,
                FogPath = floor.FogPath,
                CellSizePx = floor.CellSizePx,
                GridWidth = floor.GridWidth,
                GridHeight = floor.GridHeight
            }).ToList()
        }).ToList();
        ThemeSettingsService.SaveSettings(settings);
    }

    // ==================== Map display (open to players, live fog editing) ====================
    // "Starting" fog lives on disk per floor (MapFloorItemViewModel.FogPath, the library
    // template); "current/live" fog is session-only, kept here in memory (not written back to
    // FogPath) and initialized from the starting fog the first time a floor is touched.

    [ObservableProperty] private MapItemViewModel? _openMap;
    [ObservableProperty] private MapFloorItemViewModel? _editingFloor;
    [ObservableProperty] private bool _isMapOpenToPlayers;

    private readonly Dictionary<Guid, FogMask> _liveFog = new();

    /// <summary>
    ///     Shared "what a player currently sees" display, reused for the local player-window
    ///     preview (PlayerWindow.axaml, see ShouldShowMapLocally) - the exact same component the
    ///     real PlayerClient uses (RpgTimeTracker.Shared.ViewModels.MapDisplayViewModel). Since
    ///     both this and GetLiveFog live in the same process, the Host feeds it the SAME FogMask
    ///     instances it paints into (see MapEditorWindow) rather than a serialized copy, and only
    ///     needs to say "something changed" (NotifyFogChanged) instead of resending cell data.
    /// </summary>
    public MapDisplayViewModel MapDisplay { get; } = new();

    /// <summary>The local player window is open - shown regardless of connected clients, same
    ///     as ShouldShowMediaLocally.</summary>
    public bool ShouldShowMapLocally => MapDisplay.IsShowingMap && IsPlayerWindowOpen;

    public bool HasNoFloorsInEditor => EditingFloor is null;

    private void ApplyAndBroadcastFogStyle()
    {
        MapDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(FogColorHex, FogOpacityPercent), FogBlurRadius,
            FogBlurEnabled);
        _ = _playerServer.PublishMapRenderStyleAsync(FogColorHex, FogOpacityPercent, FogBlurRadius, FogBlurEnabled);

        var settings = ThemeSettingsService.LoadSettings();
        settings.FogColorHex = FogColorHex;
        settings.FogOpacityPercent = FogOpacityPercent;
        settings.FogBlurRadius = FogBlurRadius;
        settings.FogBlurEnabled = FogBlurEnabled;
        ThemeSettingsService.SaveSettings(settings);
    }

    partial void OnFogColorHexChanged(string value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogOpacityPercentChanged(int value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogBlurRadiusChanged(double value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogBlurEnabledChanged(bool value)
    {
        ApplyAndBroadcastFogStyle();
    }

    /// <summary>The live (current) fog for a floor - loaded from its starting template on first
    ///     access, then mutated in place as the GM paints.</summary>
    public FogMask GetLiveFog(MapFloorItemViewModel floor)
    {
        if (_liveFog.TryGetValue(floor.Id, out var fog)) return fog;

        fog = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(File.ReadAllBytes(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);
        _liveFog[floor.Id] = fog;
        return fog;
    }

    [RelayCommand]
    private async Task OpenMapToPlayersAsync(MapItemViewModel map)
    {
        var floors = new List<TcpPlayerServerService.OpenMapFloor>();
        var displayFloors = new List<MapDisplayFloor>();
        foreach (var floor in map.Floors)
        {
            if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

            var startingFog = FogMaskSerializer.Deserialize(await File.ReadAllBytesAsync(floor.FogPath));
            var liveFog = GetLiveFog(floor);
            MediaTypeHelper.TryGetKind(floor.ImagePath, out _, out var mimeType);
            floors.Add(new TcpPlayerServerService.OpenMapFloor
            {
                FloorId = floor.Id,
                FloorName = floor.Name,
                ImageFileName = Path.GetFileName(floor.ImagePath),
                ImageMimeType = mimeType,
                ImageBytes = await File.ReadAllBytesAsync(floor.ImagePath),
                CellSizePx = floor.CellSizePx,
                GridWidth = floor.GridWidth,
                GridHeight = floor.GridHeight,
                StartingFog = startingFog,
                CurrentFog = liveFog
            });

            using var imageStream = File.OpenRead(floor.ImagePath);
            displayFloors.Add(new MapDisplayFloor
            {
                FloorId = floor.Id,
                Name = floor.Name,
                Image = new Bitmap(imageStream),
                CurrentFog = liveFog
            });
        }

        OpenMap = map;
        EditingFloor ??= map.Floors.FirstOrDefault();
        IsMapOpenToPlayers = true;
        await _playerServer.PublishMapShowAsync(map.Id, map.Name, floors);
        MapDisplay.ShowMap(map.Name, displayFloors);
        OnPropertyChanged(nameof(ShouldShowMapLocally));
        Log.Information("Map opened to players: {MapName} ({FloorCount} floors)", map.Name, floors.Count);
    }

    [RelayCommand]
    private async Task CloseMapToPlayersAsync()
    {
        IsMapOpenToPlayers = false;
        await _playerServer.PublishMapHideAsync();
        MapDisplay.HideMap();
        OnPropertyChanged(nameof(ShouldShowMapLocally));
        Log.Information("Map closed to players");
    }

    /// <summary>
    ///     Broadcasts a debounced batch of already-applied reveal/hide cells (the editor paints
    ///     the live FogMask returned by GetLiveFog directly, for immediate visual feedback - this
    ///     just tells connected players). Harmless to call for a floor that isn't the currently
    ///     open map: the server-side cache update and the broadcast are both no-ops for clients
    ///     that don't have that floor loaded.
    /// </summary>
    public Task BroadcastFogCellsAsync(Guid floorId, List<FogCellDto> cells)
    {
        return cells.Count == 0 ? Task.CompletedTask : _playerServer.PublishMapFogUpdateAsync(floorId, cells);
    }

    public async Task ResetFloorFogToStartingAsync(MapFloorItemViewModel floor)
    {
        var startingFog = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(await File.ReadAllBytesAsync(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);

        // Mutated in place (not replacing the dictionary entry) so the exact same FogMask
        // instance already shared with MapDisplay/MapEditorWindow stays valid - see GetLiveFog.
        var live = GetLiveFog(floor);
        live.RevealedBits = startingFog.RevealedBits;

        await _playerServer.PublishMapFogResetAsync(floor.Id);
        MapDisplay.NotifyFogChanged(floor.Id);
    }

    /// <summary>Exports a single map (its floors' images + starting fog) as a self-contained
    ///     <c>.rtt-map</c> zip - one map per file, unlike the whole-folder Media/Sound library
    ///     export/import (a map is a single unit a GM shares/backs up, not a batch of many).</summary>
    public byte[] ExportMapToZipBytes(MapItemViewModel map)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new MapManifestDto { Name = map.Name };
            foreach (var floor in map.Floors)
            {
                if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

                var imageFileName = $"{Guid.NewGuid():N}{Path.GetExtension(floor.ImagePath)}";
                var fogFileName = $"{Guid.NewGuid():N}.fog";
                WriteZipFileEntry(zip, imageFileName, floor.ImagePath);
                WriteZipFileEntry(zip, fogFileName, floor.FogPath);
                manifest.Floors.Add(new MapFloorManifestEntryDto
                {
                    Name = floor.Name,
                    ImageFileName = imageFileName,
                    FogFileName = fogFileName,
                    CellSizePx = floor.CellSizePx,
                    GridWidth = floor.GridWidth,
                    GridHeight = floor.GridHeight
                });
            }

            var manifestEntry = zip.CreateEntry(MapManifestEntryName);
            using var manifestStream = manifestEntry.Open();
            using var writer = new StreamWriter(manifestStream);
            writer.Write(JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
        }

        Log.Information("Map exported: {MapName} ({FloorCount} floors)", map.Name, map.Floors.Count);
        return stream.ToArray();
    }

    public void ImportMapFromZipBytes(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var manifestEntry = zip.GetEntry(MapManifestEntryName);
            if (manifestEntry is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidMapExportFound");
                return;
            }

            MapManifestDto? manifest;
            using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream))
                manifest = JsonSerializer.Deserialize<MapManifestDto>(reader.ReadToEnd());

            if (manifest is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.ManifestCouldNotBeRead");
                return;
            }

            var mapId = Guid.NewGuid();
            var map = new MapItemViewModel(mapId, manifest.Name, RemoveMap, _ => SaveMapLibrarySettings());
            var mapDirectory = Path.Combine(ThemeSettingsService.MapLibraryDirectory, mapId.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            foreach (var floorEntry in manifest.Floors)
            {
                var imageEntry = zip.GetEntry(floorEntry.ImageFileName);
                var fogEntry = zip.GetEntry(floorEntry.FogFileName);
                if (imageEntry is null || fogEntry is null) continue;

                var floorId = Guid.NewGuid();
                var imagePath = Path.Combine(mapDirectory, $"{floorId:N}{Path.GetExtension(floorEntry.ImageFileName)}");
                var fogPath = Path.Combine(mapDirectory, $"{floorId:N}.fog");
                using (var entryStream = imageEntry.Open())
                using (var target = File.Create(imagePath))
                    entryStream.CopyTo(target);
                using (var entryStream = fogEntry.Open())
                using (var target = File.Create(fogPath))
                    entryStream.CopyTo(target);

                map.Floors.Add(new MapFloorItemViewModel(
                    floorId, floorEntry.Name, imagePath, fogPath,
                    floorEntry.CellSizePx, floorEntry.GridWidth, floorEntry.GridHeight,
                    LoadMapFloorThumbnail(imagePath),
                    f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings()));
            }

            MapLibrary.Add(map);
            SaveMapLibrarySettings();
            Log.Information("Map imported: {MapName} ({FloorCount} floors)", map.Name, map.Floors.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MapImported"), map.Name));
        }
        catch (InvalidDataException)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidMapExportFound");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Map import failed");
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    // ==================== Full session export/import (state + all three libraries in one file) ====================

    /// <summary>
    ///     Bundles everything needed to fully restore or move a campaign: the game state
    ///     (same shape as ExportStateToJson/the .rtt-save format) plus the Media, Sound, and
    ///     Map libraries - into a single .rtt-session zip. Unlike the individual per-library
    ///     export (folder-based, for backing up/sharing just that one library) or .rtt-save
    ///     (state only, since libraries are meant to be durable/shared across campaigns), this
    ///     is the "give me one file with everything" option, e.g. for moving to a new machine.
    /// </summary>
    public byte[] ExportFullSessionToZipBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipTextEntry(zip, "state.json", ExportStateToJson());

            var mediaManifest = new LibraryManifestDto { LibraryType = "Media" };
            foreach (var item in MediaLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                WriteZipFileEntry(zip, $"media/{fileName}", item.LocalPath);
                mediaManifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name, FileName = fileName, MimeType = item.MimeType,
                    Kind = item.Kind.ToString(), Loop = item.Loop
                });
            }

            WriteZipTextEntry(zip, "media/manifest.json", JsonSerializer.Serialize(mediaManifest, LibraryManifestJsonOptions));

            var soundManifest = new LibraryManifestDto { LibraryType = "Sound" };
            foreach (var item in SoundLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                WriteZipFileEntry(zip, $"sound/{fileName}", item.LocalPath);
                soundManifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name, Icon = item.Icon, FileName = fileName, MimeType = item.MimeType, Loop = item.Loop,
                    Volume = item.Volume, RepeatCount = item.RepeatCount,
                    TrimStartMs = (long)(item.TrimStartSeconds * 1000), TrimEndMs = (long)(item.TrimEndSeconds * 1000)
                });
            }

            WriteZipTextEntry(zip, "sound/manifest.json", JsonSerializer.Serialize(soundManifest, LibraryManifestJsonOptions));

            var mapManifest = new MapLibraryManifestDto();
            foreach (var map in MapLibrary)
            {
                var mapEntry = new MapLibraryManifestEntryDto { Name = map.Name };
                foreach (var floor in map.Floors)
                {
                    if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

                    var imageFileName = $"{Guid.NewGuid():N}{Path.GetExtension(floor.ImagePath)}";
                    var fogFileName = $"{Guid.NewGuid():N}.fog";
                    WriteZipFileEntry(zip, $"maps/{imageFileName}", floor.ImagePath);
                    WriteZipFileEntry(zip, $"maps/{fogFileName}", floor.FogPath);
                    mapEntry.Floors.Add(new MapFloorManifestEntryDto
                    {
                        Name = floor.Name, ImageFileName = imageFileName, FogFileName = fogFileName,
                        CellSizePx = floor.CellSizePx, GridWidth = floor.GridWidth, GridHeight = floor.GridHeight
                    });
                }

                mapManifest.Maps.Add(mapEntry);
            }

            WriteZipTextEntry(zip, "maps/manifest.json", JsonSerializer.Serialize(mapManifest, LibraryManifestJsonOptions));
        }

        Log.Information("Full session exported: {MediaCount} media, {SoundCount} sounds, {MapCount} maps",
            MediaLibrary.Count, SoundLibrary.Count, MapLibrary.Count);
        return stream.ToArray();
    }

    /// <summary>
    ///     Restores game state (replacing it, like a normal load) and adds every library item
    ///     found in the bundle (appending, like a normal library import - existing library
    ///     items are left alone, so importing the same session twice will duplicate entries;
    ///     this matches the existing single-library import behavior rather than introducing a
    ///     new merge policy just for this feature).
    /// </summary>
    public void ImportFullSessionFromZipBytes(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var stateEntry = zip.GetEntry("state.json");
            if (stateEntry is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
                return;
            }

            using (var entryStream = stateEntry.Open())
            using (var reader = new StreamReader(entryStream, Encoding.UTF8))
            {
                ImportStateFromJson(reader.ReadToEnd());
            }

            var mediaImported = ImportMediaSectionFromZip(zip);
            var soundImported = ImportSoundSectionFromZip(zip);
            var mapsImported = ImportMapsSectionFromZip(zip);

            Log.Information("Full session imported: {MediaCount} media, {SoundCount} sounds, {MapCount} maps",
                mediaImported, soundImported, mapsImported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.FullSessionImported"),
                mediaImported, soundImported, mapsImported));
        }
        catch (InvalidDataException)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Full session import failed");
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    private int ImportMediaSectionFromZip(ZipArchive zip)
    {
        var manifest = ReadZipJsonEntry<LibraryManifestDto>(zip, "media/manifest.json");
        if (manifest is null) return 0;

        Directory.CreateDirectory(ThemeSettingsService.MediaLibraryDirectory);
        var imported = 0;
        foreach (var item in manifest.Items)
        {
            var fileEntry = zip.GetEntry($"media/{item.FileName}");
            if (fileEntry is null) continue;
            if (!Enum.TryParse<MediaKind>(item.Kind, out var kind) || kind == MediaKind.Audio) continue;

            var cachedPath = Path.Combine(ThemeSettingsService.MediaLibraryDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(item.FileName)}");
            using (var entryStream = fileEntry.Open())
            using (var target = File.Create(cachedPath))
            {
                entryStream.CopyTo(target);
            }

            MediaLibrary.Add(new MediaLibraryItemViewModel(
                item.Name, cachedPath, kind, item.MimeType, item.Loop,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged));
            imported++;
        }

        if (imported > 0)
        {
            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
        }

        return imported;
    }

    private int ImportSoundSectionFromZip(ZipArchive zip)
    {
        var manifest = ReadZipJsonEntry<LibraryManifestDto>(zip, "sound/manifest.json");
        if (manifest is null) return 0;

        Directory.CreateDirectory(ThemeSettingsService.SoundLibraryDirectory);
        var imported = 0;
        foreach (var item in manifest.Items)
        {
            var fileEntry = zip.GetEntry($"sound/{item.FileName}");
            if (fileEntry is null) continue;

            var cachedPath = Path.Combine(ThemeSettingsService.SoundLibraryDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(item.FileName)}");
            using (var entryStream = fileEntry.Open())
            using (var target = File.Create(cachedPath))
            {
                entryStream.CopyTo(target);
            }

            SoundLibrary.Add(CreateSoundLibraryItem(item.Name, item.Icon ?? VisualItemHelper.IconTimer, cachedPath,
                item.MimeType, item.Loop, item.Volume ?? 100, item.RepeatCount ?? 1,
                item.TrimStartMs / 1000.0, item.TrimEndMs / 1000.0));
            imported++;
        }

        if (imported > 0)
        {
            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
        }

        return imported;
    }

    private int ImportMapsSectionFromZip(ZipArchive zip)
    {
        var manifest = ReadZipJsonEntry<MapLibraryManifestDto>(zip, "maps/manifest.json");
        if (manifest is null) return 0;

        var imported = 0;
        foreach (var mapEntry in manifest.Maps)
        {
            var mapId = Guid.NewGuid();
            var map = new MapItemViewModel(mapId, mapEntry.Name, RemoveMap, _ => SaveMapLibrarySettings());
            var mapDirectory = Path.Combine(ThemeSettingsService.MapLibraryDirectory, mapId.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            foreach (var floorEntry in mapEntry.Floors)
            {
                var imageEntry = zip.GetEntry($"maps/{floorEntry.ImageFileName}");
                var fogEntry = zip.GetEntry($"maps/{floorEntry.FogFileName}");
                if (imageEntry is null || fogEntry is null) continue;

                var floorId = Guid.NewGuid();
                var imagePath = Path.Combine(mapDirectory, $"{floorId:N}{Path.GetExtension(floorEntry.ImageFileName)}");
                var fogPath = Path.Combine(mapDirectory, $"{floorId:N}.fog");
                using (var entryStream = imageEntry.Open())
                using (var target = File.Create(imagePath))
                {
                    entryStream.CopyTo(target);
                }

                using (var entryStream = fogEntry.Open())
                using (var target = File.Create(fogPath))
                {
                    entryStream.CopyTo(target);
                }

                map.Floors.Add(new MapFloorItemViewModel(
                    floorId, floorEntry.Name, imagePath, fogPath,
                    floorEntry.CellSizePx, floorEntry.GridWidth, floorEntry.GridHeight,
                    LoadMapFloorThumbnail(imagePath),
                    f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings()));
            }

            MapLibrary.Add(map);
            imported++;
        }

        if (imported > 0) SaveMapLibrarySettings();
        return imported;
    }

    private static void WriteZipTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteZipFileEntry(ZipArchive zip, string entryName, string sourceFilePath)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var sourceStream = File.OpenRead(sourceFilePath);
        sourceStream.CopyTo(entryStream);
    }

    private static T? ReadZipJsonEntry<T>(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return default;

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd());
    }

    // ==================== Gallery: session's own list of sent images/videos ====================
    // Unlike the single-slot model above (CurrentMediaKind), NOTHING is discarded here on the next
    // send - every image/video sent via ad-hoc/library stays in SentMediaItems
    // until it is explicitly removed. The GM and each player navigate independently and locally
    // through this list (see ClientMainWindowViewModel); "highlight" is only a jump suggestion,
    // not a requirement. Event-trigger media are NEVER part of it - they only temporarily
    // override the display (taking precedence), see StopMediaIfTriggeredBy for the resumption afterward.

    public ObservableCollection<SentMediaItemViewModel> SentMediaItems { get; } = [];
    public bool HasNoSentMediaItems => SentMediaItems.Count == 0;

    public ObservableCollection<string> SessionLogEntries { get; } = [];

    public bool HasSessionLogEntries => SessionLogEntries.Count > 0;

    /// <summary>
    ///     History of the most recent action notices (newest first) - for the notifications flyout in
    ///     the status bar. ActionStatusMessage remains the "just now" indicator (clears itself after
    ///     4s, see ClearActionStatusAfterDelayAsync), this history persists until it
    ///     exceeds the cap.
    /// </summary>
    public ObservableCollection<string> ActionStatusHistory { get; } = [];

    public bool HasNoActionStatusHistory => ActionStatusHistory.Count == 0;

    public ObservableCollection<ActiveSoundViewModel> ActivePlayingSounds { get; } = [];
    public bool HasNoActivePlayingSounds => ActivePlayingSounds.Count == 0;

    public MediaLibraryItemViewModel? SelectedCalendarTriggerMediaItem
    {
        get => SelectedCalendarEntry?.TriggerMedia.Kind is MediaKind.Image or MediaKind.Video
            ? MediaLibrary.FirstOrDefault(item =>
                string.Equals(item.LocalPath, SelectedCalendarEntry.TriggerMedia.Path,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        set
        {
            if (SelectedCalendarEntry is null)
                return;

            if (value is null)
            {
                if (SelectedCalendarEntry.TriggerMedia.Kind is MediaKind.Image or MediaKind.Video)
                    SelectedCalendarEntry.TriggerMedia.ClearCommand.Execute(null);
            }
            else
            {
                SelectedCalendarEntry.TriggerMedia.Path = value.LocalPath;
                SelectedCalendarEntry.TriggerMedia.FileName = value.Name;
                SelectedCalendarEntry.TriggerMedia.Kind = value.Kind;
                SelectedCalendarEntry.TriggerMedia.Loop = value.Loop;
            }

            RefreshCalendarViews();
            QueueCalendarFullResync();
        }
    }

    public SoundLibraryItemViewModel? SelectedCalendarTriggerSoundItem
    {
        get => SelectedCalendarEntry?.TriggerMedia.Kind == MediaKind.Audio
            ? SoundLibrary.FirstOrDefault(item =>
                string.Equals(item.LocalPath, SelectedCalendarEntry.TriggerMedia.Path,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        set
        {
            if (SelectedCalendarEntry is null)
                return;

            if (value is null)
            {
                if (SelectedCalendarEntry.TriggerMedia.Kind == MediaKind.Audio)
                    SelectedCalendarEntry.TriggerMedia.ClearCommand.Execute(null);
            }
            else
            {
                SelectedCalendarEntry.TriggerMedia.Path = value.LocalPath;
                SelectedCalendarEntry.TriggerMedia.FileName = value.Name;
                SelectedCalendarEntry.TriggerMedia.Kind = MediaKind.Audio;
                SelectedCalendarEntry.TriggerMedia.Loop = value.Loop;
                SelectedCalendarEntry.TriggerMedia.Fullscreen = false;
                SelectedCalendarEntry.TriggerMedia.PauseClockDuringVideo = false;
            }

            RefreshCalendarViews();
            QueueCalendarFullResync();
        }
    }

    public bool HasPlayerVisibleCalendarEntries => CalendarEntries.Any(entry =>
        entry.TryBuildDefinition(out var definition) && definition.IsPlayerVisible);

    public bool HasPlayerCalendarEntriesForSelectedDate => PlayerCalendarEntries.Count > 0;
    ICommand IPlayerDisplayContext.ShowPlayerTimelineCommand => ShowPlayerTimelineCommand;

    ICommand IPlayerDisplayContext.ShowPlayerCalendarCommand => ShowPlayerCalendarCommand;

    ICommand IPlayerDisplayContext.PreviousPlayerCalendarMonthCommand => PreviousPlayerCalendarMonthCommand;

    ICommand IPlayerDisplayContext.NextPlayerCalendarMonthCommand => NextPlayerCalendarMonthCommand;

    public bool ShowPlayerTimelineView => !ShowPlayerCalendarView;

    IEnumerable IPlayerDisplayContext.TimelineEntries => PlayerTimelineItems;
    IEnumerable IPlayerDisplayContext.CalendarMonthDays => PlayerCalendarDays;
    IEnumerable IPlayerDisplayContext.CalendarEntries => PlayerCalendarEntries;

    public string SpeedMultiplierDisplay => SpeedMultiplier.ToString("0.0", LocalizationService.Culture) + "×";

    public string SpeedLabel => string.Format(LocalizationService.Get("PlayerHeaderView.SpeedLabel"), SpeedMultiplierDisplay);

    partial void OnNetworkServerAddressChanged(string? value)
    {
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnIsPlayerWindowOpenChanged(bool value)
    {
        if (!value) IsLocalPlayerFullscreen = false;
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        RefreshLocalMediaPreview();
        OnPropertyChanged(nameof(ShouldShowMapLocally));
    }

    /// <summary>
    ///     GM requests that the local player window (if open) be switched to fullscreen - e.g. when an event medium
    ///     with "fullscreen" triggers.
    /// </summary>
    public event Action<bool>? LocalFullscreenRequested;

    partial void OnCurrentMediaKindChanged(MediaKind value)
    {
        OnPropertyChanged(nameof(HasMedia));
        RefreshLocalMediaPreview();
    }

    partial void OnRemoteFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDisplayFullscreenActive));
        OnPropertyChanged(nameof(DisplayFullscreenLabel));
    }

    partial void OnIsLocalPlayerFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDisplayFullscreenActive));
        OnPropertyChanged(nameof(DisplayFullscreenLabel));
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

    partial void OnSelectedCalendarEntryChanged(CalendarEntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedCalendarEntry));
        OnPropertyChanged(nameof(SelectedCalendarTriggerMediaItem));
        OnPropertyChanged(nameof(SelectedCalendarTriggerSoundItem));
    }

    partial void OnConnectedClientCountChanged(int value)
    {
        if (value == 0) ClearRemoteOnlyActiveSounds();
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnNetworkServerPortChanged(int value)
    {
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    [RelayCommand]
    private void ToggleDisplayFullscreen()
    {
        SetDisplayFullscreen(!IsDisplayFullscreenActive);
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
        CalendarMonth = CalendarMonth.AddMonths(-1);
        RefreshCalendarViews();
    }

    [RelayCommand]
    private void NextPlayerCalendarMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(1);
        RefreshCalendarViews();
    }

    /// <summary>
    ///     Unifies the fullscreen request for everything currently present: local
    ///     player window, remote clients, or both.
    /// </summary>
    private void SetDisplayFullscreen(bool value)
    {
        SetLocalPlayerFullscreen(value);
        SetRemotePlayerFullscreen(value);
    }

    private void SetLocalPlayerFullscreen(bool value)
    {
        if (!IsPlayerWindowOpen)
        {
            IsLocalPlayerFullscreen = false;
            return;
        }

        IsLocalPlayerFullscreen = value;
        LocalFullscreenRequested?.Invoke(value);
    }

    private void SetRemotePlayerFullscreen(bool value)
    {
        if (!IsNetworkServerRunning)
        {
            RemoteFullscreen = false;
            return;
        }

        RemoteFullscreen = value;
        _ = _playerServer.PublishDisplayFullscreenAsync(value);
    }

    public void ReportLocalPlayerFullscreen(bool fullscreen)
    {
        IsLocalPlayerFullscreen = IsPlayerWindowOpen && fullscreen;
    }

    /// <summary>
    ///     Decides based on ShouldShowMediaLocally whether the current medium is decoded/
    ///     played locally. Called explicitly instead of via a plain property diff, because
    ///     e.g. CurrentMediaKind doesn't "change" for two media of the same kind in a row
    ///     (image -> image), and the WPF/Avalonia default notification would then not fire.
    /// </summary>
    private void RefreshLocalMediaPreview()
    {
        OnPropertyChanged(nameof(ShouldShowMediaLocally));
        OnPropertyChanged(nameof(ShowLocalImage));
        OnPropertyChanged(nameof(ShowLocalVideo));

        if (!ShouldShowMediaLocally)
        {
            StopLocalVideo();
            CurrentMediaBitmap = null;
            return;
        }

        if (CurrentMediaKind == MediaKind.Image)
        {
            StopLocalVideo();
            try
            {
                using var stream = File.OpenRead(CurrentMediaLocalPath!);
                CurrentMediaBitmap = new Bitmap(stream);
            }
            catch
            {
                CurrentMediaBitmap = null;
            }

            return;
        }

        if (CurrentMediaKind == MediaKind.Video)
        {
            CurrentMediaBitmap = null;
            if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;

            try
            {
                MediaPlayer ??= CreateLocalMediaPlayer(libVlc);
                using var media = new Media(libVlc, CurrentMediaLocalPath!);
                MediaPlayer.Play(media);
            }
            catch
            {
                // Error display remains reserved for the media tab status (MediaErrorMessage).
            }
        }
    }

    private void StopLocalVideo()
    {
        MediaPlayer?.Stop();
    }

    /// <summary>
    ///     Resolves BeginVideoTracking/ResolvePendingVideo exactly at the actual end even
    ///     when the video is (also) running locally in the host player window, instead of relying
    ///     solely on the client report or the duration estimate as a fallback (see
    ///     the BeginVideoTracking comment). Both sources target the same mediaId value -
    ///     ResolvePendingVideo is already idempotent thanks to the _pendingVideoMediaId check,
    ///     regardless of which source fires first.
    /// </summary>
    private MediaPlayer CreateLocalMediaPlayer(LibVLC libVlc)
    {
        var player = new MediaPlayer(libVlc);
        player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_pendingVideoMediaId is { } mediaId) ResolvePendingVideo(mediaId);
        });
        return player;
    }

    private void AddTimelineItem(TimerItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(AlarmItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(IntervalEventItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(TimelineDisplayItemViewModel item)
    {
        TimelineItems.Add(item);
        if (item.IsPlayerVisible) PlayerTimelineItems.Add(item);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimelineDisplayItemViewModel.IsPlayerVisible) ||
                e.PropertyName == nameof(TimelineDisplayItemViewModel.IsSlOnly))
                SyncPlayerTimelineItems();

            OnPropertyChanged(nameof(NextEventJumpLabel));
        };
    }

    /// <summary>
    ///     Sends the current state of an item as timelineItem.upserted (or as
    ///     timelineItem.removed, if it is currently not player-visible). Triggered by the
    ///     StateChanged events of the timer/alarm/interval view models - NOT on every
    ///     clock tick, but only on real discrete state changes.
    /// </summary>
    private void PublishItemState(TimerItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(AlarmItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(IntervalEventItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(TimelineDisplayItemViewModel? item)
    {
        if (item is null) return;

        if (!item.IsPlayerVisible)
        {
            _ = _playerServer.PublishTimelineItemRemovedAsync(item.Id);
            return;
        }

        _ = _playerServer.PublishTimelineItemUpsertedAsync(item.ToRpcSnapshot());
    }

    private void SyncPlayerTimelineItems()
    {
        PlayerTimelineItems.Clear();
        foreach (var item in TimelineItems.Where(item => item.IsPlayerVisible)) PlayerTimelineItems.Add(item);
    }

    [RelayCommand]
    private void AddCalendarEntry()
    {
        var entry = new CalendarEntryViewModel(OnCalendarEntryChanged, RemoveCalendarEntry)
        {
            StartDateTimeText = CalendarSelectedDate.Date.AddHours(12)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        CalendarEntries.Add(entry);
        SelectedCalendarEntry = entry;
        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void RemoveCalendarEntry(CalendarEntryViewModel entry)
    {
        CalendarEntries.Remove(entry);
        if (ReferenceEquals(SelectedCalendarEntry, entry))
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();

        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void OnCalendarEntryChanged(CalendarEntryViewModel entry)
    {
        if (entry.TryBuildDefinition(out var definition))
        {
            SelectedCalendarEntry ??= entry;
            if (ReferenceEquals(SelectedCalendarEntry, entry) || CalendarEntries.Count == 1)
            {
                CalendarSelectedDate = definition.Start.Date;
                CalendarMonth = new DateTime(definition.Start.Year, definition.Start.Month, 1);
            }
        }

        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void RefreshCalendarViews()
    {
        CalendarMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        PlayerCalendarMonthLabel = CalendarMonth.ToString("MMMM yyyy", LocalizationService.Culture);
        PlayerCalendarSelectedDateLabel =
            CalendarSelectedDate.ToString("dddd, dd.MM.yyyy", LocalizationService.Culture);

        var validDefinitions = CalendarEntries
            .Select(item =>
                item.TryBuildDefinition(out var definition)
                    ? (Item: item, Definition: definition)
                    : ((CalendarEntryViewModel Item, CalendarEntryDefinition Definition)?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToList();

        CalendarEntriesForSelectedDate.Clear();
        foreach (var entry in validDefinitions
                     .Where(item => item.Definition.TryGetOccurrenceOn(CalendarSelectedDate, out _))
                     .OrderBy(item =>
                     {
                         item.Definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
                         return occurrence;
                     }))
            CalendarEntriesForSelectedDate.Add(entry.Item);

        if (SelectedCalendarEntry is not null && !CalendarEntriesForSelectedDate.Contains(SelectedCalendarEntry))
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();
        else if (SelectedCalendarEntry is null && CalendarEntriesForSelectedDate.Count > 0)
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();

        PlayerCalendarDays.Clear();
        var firstOfMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        var offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var gridStart = firstOfMonth.AddDays(-offset);
        for (var index = 0; index < 42; index++)
        {
            var day = gridStart.AddDays(index);
            var dayVm = new PlayerCalendarDayViewModel(day, SelectCalendarDate)
            {
                IsCurrentMonth = day.Month == CalendarMonth.Month,
                IsSelected = day.Date == CalendarSelectedDate.Date,
                IsToday = day.Date == _clock.CurrentTime.Date,
                HasEntries = validDefinitions.Any(item => item.Definition.TryGetOccurrenceOn(day, out _))
            };
            PlayerCalendarDays.Add(dayVm);
        }

        PlayerCalendarEntries.Clear();
        foreach (var definition in validDefinitions
                     .Select(item => item.Definition)
                     .Where(definition => definition.IsPlayerVisible)
                     .Where(definition => definition.TryGetOccurrenceOn(CalendarSelectedDate, out _))
                     .OrderBy(definition =>
                     {
                         definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
                         return occurrence;
                     }))
        {
            definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
            PlayerCalendarEntries.Add(new PlayerCalendarEntryViewModel
            {
                Title = definition.Title,
                Description = definition.Description,
                Icon = definition.Icon,
                ColorHex = definition.ColorHex,
                TimeText = occurrence.ToString("HH:mm")
            });
        }

        if (!HasPlayerVisibleCalendarEntries)
            ShowPlayerCalendarView = false;

        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        OnPropertyChanged(nameof(HasCalendarEntriesForSelectedDate));
        OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
        OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
        OnPropertyChanged(nameof(ShowPlayerTimelineView));
        OnPropertyChanged(nameof(SelectedCalendarTriggerMediaItem));
        OnPropertyChanged(nameof(SelectedCalendarTriggerSoundItem));
    }

    private void SelectCalendarDate(DateTime date)
    {
        CalendarSelectedDate = date.Date;
        if (CalendarMonth.Month != date.Month || CalendarMonth.Year != date.Year)
            CalendarMonth = new DateTime(date.Year, date.Month, 1);

        RefreshCalendarViews();
    }

    private void QueueCalendarFullResync()
    {
        _calendarSyncCts?.Cancel();
        _calendarSyncCts = new CancellationTokenSource();
        var token = _calendarSyncCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                {
                    var snapshot = await Dispatcher.UIThread.InvokeAsync(BuildSessionSnapshot);
                    await _playerServer.PublishFullResyncAsync(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void RemoveTimelineItem(Func<TimelineDisplayItemViewModel, bool> predicate)
    {
        var item = TimelineItems.FirstOrDefault(predicate);
        if (item is not null)
        {
            TimelineItems.Remove(item);
            PlayerTimelineItems.Remove(item);
        }
    }

    partial void OnSelectedThemeOptionChanged(string value)
    {
        _ambienceCurrentFileName = null;

        if (!_themesByDisplayName.TryGetValue(value, out var loaded)) return;

        ThemeService.Apply(loaded.Definition, loaded.FolderPath);
        ThemeSettingsService.SaveLastThemeId(loaded.Definition.Id);
        CurrentThemeId = loaded.Definition.Id;
        OnPropertyChanged(nameof(HasAmbienceBackgrounds));

        // Themes are pushed to clients via RPC as a plain theme id (see design-decisions.md) -
        // a client must have the same theme.json locally (bundled SampleThemes or the same
        // %AppData%/RpgTimeTracker/Themes file) to render it; without a matching local file
        // the client stays on its last active theme (see ClientMainWindowViewModel.ApplyTheme).
        _ = _playerServer.PublishThemeChangedAsync(GetCurrentThemeWireValue());
        UpdateAmbience();
    }

    /// <summary>The theme value sent over the network/embedded in session.snapshot - the theme id.</summary>
    private string GetCurrentThemeWireValue()
    {
        return _themesByDisplayName.TryGetValue(SelectedThemeOption, out var loaded) ? loaded.Definition.Id : "shadowrun";
    }

    /// <summary>
    ///     Switches WindowBackgroundImageBrush between the first and last named background
    ///     of the currently active theme, depending on whether it is currently day (06-18) or
    ///     night. A no-op as long as the active theme has only one background (this currently
    ///     applies to all bundled samples except "Medieval").
    /// </summary>
    private void UpdateAmbience()
    {
        if (!AmbienceAutomationEnabled) return;
        if (CurrentThemeId is null) return;
        if (!_themesById.TryGetValue(CurrentThemeId, out var loaded)) return;
        if (loaded.Definition.Backgrounds.Count < 2) return;

        var isDaytime = _clock.CurrentTime.Hour is >= 6 and < 18;
        var background = isDaytime ? loaded.Definition.Backgrounds[0] : loaded.Definition.Backgrounds[^1];
        if (background.FileName == _ambienceCurrentFileName) return;

        if (Application.Current is null) return;

        if (ThemeDefinitionLoader.TrySetImageBrush(Application.Current.Resources, "WindowBackgroundImageBrush",
                loaded.FolderPath, background.FileName, loaded.Definition.Id))
            _ambienceCurrentFileName = background.FileName;
    }

    partial void OnSpeedMultiplierChanged(double value)
    {
        var rounded = Math.Round(value <= 0 ? 0.1 : value, 1);
        if (Math.Abs(rounded - value) > 0.0001)
        {
            SpeedMultiplier = rounded;
            return;
        }

        _clock.SpeedMultiplier = rounded;
        var asDecimal = (decimal)rounded;
        if (SpeedMultiplierInput != asDecimal) SpeedMultiplierInput = asDecimal;
        OnPropertyChanged(nameof(SpeedMultiplierDisplay));
        OnPropertyChanged(nameof(SpeedLabel));
        _ = _playerServer.PublishClockSpeedChangedAsync(rounded);
    }

    partial void OnIsNetworkServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnSpeedMultiplierInputChanged(decimal value)
    {
        var rounded = Math.Round(value <= 0 ? 0.1m : value, 1);
        if (rounded != value)
        {
            SpeedMultiplierInput = rounded;
            return;
        }

        var asDouble = (double)rounded;
        if (Math.Abs(SpeedMultiplier - asDouble) > 0.0001) SpeedMultiplier = asDouble;
    }

    partial void OnPlayerHeaderTitleChanged(string value)
    {
        SaveUiSettings();
        _ = _playerServer.PublishHeaderChangedAsync(PlayerHeaderTitle, PlayerHeaderSubtitle);
    }

    partial void OnPlayerHeaderSubtitleChanged(string value)
    {
        SaveUiSettings();
        _ = _playerServer.PublishHeaderChangedAsync(PlayerHeaderTitle, PlayerHeaderSubtitle);
    }

    partial void OnHeadsUpWarningEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnHeadsUpLeadMinutesChanged(decimal value)
    {
        SaveUiSettings();
    }

    partial void OnAmbienceAutomationEnabledChanged(bool value)
    {
        SaveUiSettings();
        _ambienceCurrentFileName = null;
        UpdateAmbience();
    }

    partial void OnServerNameChanged(string value)
    {
        SaveUiSettings();
    }

    partial void OnConnectionPinChanged(string value)
    {
        SaveUiSettings();
    }

    partial void OnAutoSaveOnCloseEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnAutoLoadOnStartupEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnSelectedLanguageOptionChanged(string value)
    {
        LocalizationService.Apply(LanguageCode(value));
        SaveUiSettings();
    }

    private static string LanguageDisplayName(string code) => code == "de" ? "Deutsch" : "English";
    private static string LanguageCode(string display) => display == "Deutsch" ? "de" : "en";

    private void SaveUiSettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.PlayerHeaderTitle =
            string.IsNullOrWhiteSpace(PlayerHeaderTitle)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.PlayerHeaderTitle")
                : PlayerHeaderTitle;
        settings.PlayerHeaderSubtitle = PlayerHeaderSubtitle;
        settings.HeadsUpWarningEnabled = HeadsUpWarningEnabled;
        settings.HeadsUpLeadMinutes = (double)HeadsUpLeadMinutes;
        settings.AmbienceAutomationEnabled = AmbienceAutomationEnabled;
        settings.ServerName = string.IsNullOrWhiteSpace(ServerName) ? "RpgTimeTracker" : ServerName;
        settings.ConnectionPin = ConnectionPin;
        settings.Language = LanguageCode(SelectedLanguageOption);
        settings.AutoSaveOnCloseEnabled = AutoSaveOnCloseEnabled;
        settings.AutoLoadOnStartupEnabled = AutoLoadOnStartupEnabled;
        ThemeSettingsService.SaveSettings(settings);
    }

    // ==================== Send media (image/video) ====================

    /// <summary>
    ///     Reads the selected file, shows it locally in the player window, and distributes it
    ///     over the network to all connected player clients.
    /// </summary>
    public async Task SendMediaFromPathAsync(string sourcePath)
    {
        Log.Information("Sending ad-hoc medium: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            Log.Warning("Ad-hoc medium rejected: unsupported format ({SourcePath})", sourcePath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMediaFormat");
            return;
        }

        var prepared = await ReadAndCacheAdHocFileAsync(sourcePath, ThemeSettingsService.MediaCacheDirectory);
        if (prepared is null) return;
        var (bytes, cachedPath) = prepared.Value;

        var fileName = Path.GetFileName(sourcePath);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = kind.ToString(),
            FileName = fileName,
            MimeType = mimeType,
            Loop = SendMediaLoop,
            AddToGallery = true
        };

        // isOwnedCache: false - the cache copy is now kept for the gallery (browsable),
        // instead of being automatically deleted on the next ad-hoc send. Cleanup is handled by
        // RetractSentMediaItem when this gallery entry is explicitly removed.
        SetCurrentMediaStatus(kind, cachedPath, fileName, false);
        AddSentMediaItem(header, cachedPath, true);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.MediaSentAdHoc"), fileName));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaSent"), fileName));

        await _playerServer.PublishMediaAsync(header, bytes);
        BeginVideoTracking(header, cachedPath, false);
    }

    /// <summary>
    ///     Reads+validates an ad-hoc chosen file and creates a GUID-named working copy
    ///     in the given cache directory (shared logic for image/video and sound ad-hoc sending).
    ///     Sets MediaErrorMessage on errors and returns null.
    /// </summary>
    private async Task<(byte[] Bytes, string CachedPath)?> ReadAndCacheAdHocFileAsync(string sourcePath,
        string cacheDirectory)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                Log.Warning("Ad-hoc file rejected: not found ({SourcePath})", sourcePath);
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc file could not be read ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return null;
        }

        if (info.Length > TcpPlayerServerService.MaxMediaBytes)
        {
            Log.Warning("Ad-hoc file rejected: {SizeMb} MB exceeds limit ({SourcePath})",
                info.Length / (1024 * 1024), sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileTooLarge"),
                info.Length / (1024 * 1024), TcpPlayerServerService.MaxMediaBytes / (1024 * 1024));
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc file could not be read ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return null;
        }

        string cachedPath;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            cachedPath = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            await File.WriteAllBytesAsync(cachedPath, bytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ad-hoc file could not be cached ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeCached"), ex.Message);
            return null;
        }

        return (bytes, cachedPath);
    }

    /// <summary>
    ///     Ad-hoc sound sending (sounds tab, without adding it to the library) - runs
    ///     entirely through SendSoundAsync, see its comment.
    /// </summary>
    public async Task SendSoundFromPathAsync(string sourcePath)
    {
        Log.Information("Sending ad-hoc sound: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            Log.Warning("Ad-hoc sound rejected: unsupported format ({SourcePath})", sourcePath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedSoundFormat");
            return;
        }

        var prepared = await ReadAndCacheAdHocFileAsync(sourcePath, ThemeSettingsService.MediaCacheDirectory);
        if (prepared is null) return;
        var (bytes, cachedPath) = prepared.Value;

        var fileName = Path.GetFileName(sourcePath);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = kind.ToString(),
            FileName = fileName,
            MimeType = mimeType,
            Loop = SendSoundLoop,
            Volume = 100
        };

        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.SoundSentAdHoc"), fileName));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundSent"), fileName));
        await SendSoundAsync(header, bytes, cachedPath, true);
    }

    [RelayCommand]
    private void ClearMedia()
    {
        CancelPendingVideoTracking();
        _activeTriggerMediaSource = null;

        var previousPath = CurrentMediaLocalPath;
        var previousWasOwned = _currentMediaIsOwnedCache;
        CurrentMediaKind = MediaKind.None;
        CurrentMediaLocalPath = null;
        CurrentMediaFileName = null;
        MediaErrorMessage = null;
        _currentMediaIsOwnedCache = false;

        if (previousWasOwned) DeleteFileQuietly(previousPath);
        _ = _playerServer.PublishMediaClearAsync();

        // Fullscreen is deliberately NOT touched when a medium is closed/reset - this
        // previously unintentionally ended a fullscreen turned on via trigger or manually (e.g.
        // "show list permanently fullscreen to the player") just because some image/video was
        // closed. Fullscreen now only changes via the explicit fullscreen action.
    }

    /// <summary>Adds a file to the library WITHOUT displaying/sending it immediately.</summary>
    public async Task AddMediaToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMediaFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            Directory.CreateDirectory(ThemeSettingsService.MediaLibraryDirectory);
            var cachedPath = Path.Combine(ThemeSettingsService.MediaLibraryDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            await using (var source = File.OpenRead(sourcePath))
            await using (var target = File.Create(cachedPath))
            {
                await source.CopyToAsync(target);
            }

            var item = new MediaLibraryItemViewModel(
                Path.GetFileNameWithoutExtension(sourcePath), cachedPath, kind, mimeType, false,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged);
            MediaLibrary.Add(item);
            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
            MediaErrorMessage = null;
            Log.Information("Medium added to library: {Name} ({Kind})", item.Name, item.Kind);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedToMediaLibrary"), item.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medium could not be added to library ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    /// <summary>
    ///     Set by the view (see MainWindow.axaml.cs), since a warning/confirmation needs a window,
    ///     which the view model itself doesn't know about - analogous to the LibraryPanelView funcs above.
    ///     Without a handler (e.g. during testing) an assigned medium is NEVER silently deleted.
    /// </summary>
    public Func<MediaLibraryItemViewModel, IReadOnlyList<string>, Task<TriggerMediaDeleteChoice>>?
        ConfirmTriggerMediaDeleteAsync { get; set; }

    private void RemoveMediaLibraryItem(MediaLibraryItemViewModel item)
    {
        _ = RemoveMediaLibraryItemAsync(item);
    }

    private async Task RemoveMediaLibraryItemAsync(MediaLibraryItemViewModel item)
    {
        var usedBy = FindTriggerMediaUsageLabels(item.LocalPath);
        if (usedBy.Count > 0)
        {
            var choice = ConfirmTriggerMediaDeleteAsync is null
                ? TriggerMediaDeleteChoice.Cancel
                : await ConfirmTriggerMediaDeleteAsync(item, usedBy).ConfigureAwait(true);

            switch (choice)
            {
                case TriggerMediaDeleteChoice.Cancel:
                    return;
                case TriggerMediaDeleteChoice.RemoveFromItemsAndDelete:
                    ClearTriggerMediaReferences(item.LocalPath);
                    break;
                case TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly:
                    // File deliberately stays on disk - the affected items still reference
                    // it directly (see the TriggerMediaConfig comment: no own copy
                    // mechanism), only the library entry disappears.
                    MediaLibrary.Remove(item);
                    OnPropertyChanged(nameof(HasNoMediaLibraryItems));
                    SaveMediaLibrarySettings();
                    RefreshCalendarViews();
                    Log.Information(
                        "Medium removed from library only, file kept (still used by {Count} items): {Name}",
                        usedBy.Count, item.Name);
                    return;
            }
        }

        MediaLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoMediaLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        SaveMediaLibrarySettings();
        RefreshCalendarViews();
        Log.Information("Medium removed from library: {Name}", item.Name);
    }

    /// <summary>
    ///     Checks whether localPath is currently assigned as an event medium - across all timers/alarms/
    ///     intervals as well as calendar entries, exclusively via TriggerMedia.Path (NOT via
    ///     the item's name, which is freely changeable and has nothing to do with the file's identity).
    ///     Returns a display label per location found (for the warning), no object references.
    /// </summary>
    private List<string> FindTriggerMediaUsageLabels(string localPath)
    {
        var labels = new List<string>();

        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        labels.AddRange(Timers.Where(t => Matches(t.TriggerMedia))
            .Select(t => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.TimerUsage"), t.Name)));
        labels.AddRange(Alarms.Where(a => Matches(a.TriggerMedia))
            .Select(a => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.AlarmUsage"), a.Name)));
        labels.AddRange(IntervalEvents.Where(i => Matches(i.TriggerMedia))
            .Select(i => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.IntervalUsage"), i.Name)));
        labels.AddRange(CalendarEntries.Where(c => Matches(c.TriggerMedia))
            .Select(c => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.CalendarEntryUsage"), c.Title)));

        return labels;
    }

    /// <summary>Resets TriggerMedia on all items that currently have localPath assigned (see FindTriggerMediaUsageLabels).</summary>
    private void ClearTriggerMediaReferences(string localPath)
    {
        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        foreach (var t in Timers.Where(t => Matches(t.TriggerMedia))) t.TriggerMedia.ClearCommand.Execute(null);
        foreach (var a in Alarms.Where(a => Matches(a.TriggerMedia))) a.TriggerMedia.ClearCommand.Execute(null);
        foreach (var i in IntervalEvents.Where(i => Matches(i.TriggerMedia))) i.TriggerMedia.ClearCommand.Execute(null);
        foreach (var c in CalendarEntries.Where(c => Matches(c.TriggerMedia))) c.TriggerMedia.ClearCommand.Execute(null);
    }

    /// <summary>Adds an audio file to the sound library WITHOUT sending it immediately.</summary>
    public async Task AddSoundToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedSoundFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var cachedPath = await CopyIntoSoundLibraryAsync(sourcePath);
            var item = CreateSoundLibraryItem(Path.GetFileNameWithoutExtension(sourcePath), VisualItemHelper.IconTimer,
                cachedPath, mimeType, false, 100);
            SoundLibrary.Add(item);
            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
            MediaErrorMessage = null;
            Log.Information("Sound added to library: {Name}", item.Name);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedToSoundLibrary"), item.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound could not be added to library ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.SoundCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    private static async Task<string> CopyIntoSoundLibraryAsync(string sourcePath)
    {
        Directory.CreateDirectory(ThemeSettingsService.SoundLibraryDirectory);
        var cachedPath = Path.Combine(ThemeSettingsService.SoundLibraryDirectory,
            $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        await using var source = File.OpenRead(sourcePath);
        await using var target = File.Create(cachedPath);
        await source.CopyToAsync(target);

        return cachedPath;
    }

    private SoundLibraryItemViewModel CreateSoundLibraryItem(
        string name, string icon, string localPath, string mimeType, bool loop, int volume,
        int repeatCount = 1, double trimStartSeconds = 0, double trimEndSeconds = 0)
    {
        return new SoundLibraryItemViewModel(name, icon, localPath, mimeType, loop, volume, repeatCount,
            trimStartSeconds, trimEndSeconds,
            RemoveSoundLibraryItem,
            PlaySoundLibraryItem,
            TestSoundLibraryItem,
            OnSoundLibraryItemChanged);
    }

    private void RemoveSoundLibraryItem(SoundLibraryItemViewModel item)
    {
        SoundLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoSoundLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        SyncSoundServiceLibrary();
        SaveSoundLibrarySettings();
        RefreshCalendarViews();
        Log.Information("Sound removed from library: {Name}", item.Name);
    }

    /// <summary>Single-click handler: immediately sends this library sound to all players (see SendSoundAsync).</summary>
    private async void PlaySoundLibraryItem(SoundLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Library sound not found: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.LibrarySoundNotFound");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Library sound could not be read: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return;
        }

        Log.Information("Sending library sound: {Name}", item.Name);

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = nameof(MediaKind.Audio),
            FileName = item.Name,
            MimeType = item.MimeType,
            Loop = item.Loop,
            Volume = item.Volume,
            RepeatCount = item.RepeatCount,
            TrimStartMs = (long)(item.TrimStartSeconds * 1000),
            TrimEndMs = (long)(item.TrimEndSeconds * 1000)
        };

        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.SoundSentLibrary"), item.Name));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundSent"), item.Name));
        await SendSoundAsync(header, bytes, item.LocalPath, false);
    }

    /// <summary>
    ///     "Test" button: plays the sound ONLY locally at the GM (never sent, no
    ///     ActivePlayingSounds entry) - to check the configured volume beforehand.
    /// </summary>
    private void TestSoundLibraryItem(SoundLibraryItemViewModel item)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;
        if (!File.Exists(item.LocalPath)) return;

        try
        {
            _testSoundPlayer?.Stop();
            _testSoundPlayer?.Dispose();

            var player = new MediaPlayer(libVlc);
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_testSoundPlayer, player)) return;
                player.Stop();
                player.Dispose();
                _testSoundPlayer = null;
            });
            _testSoundPlayer = player;

            using var media = new Media(libVlc, item.LocalPath);
            VlcMediaService.ApplySoundTrim(media, (long)(item.TrimStartSeconds * 1000),
                (long)(item.TrimEndSeconds * 1000));
            player.Play(media);
            player.Volume = Math.Clamp(item.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound test failed: {Name}", item.Name);
        }
    }

    private void OnSoundLibraryItemChanged(SoundLibraryItemViewModel item)
    {
        SyncSoundServiceLibrary();
        SaveSoundLibrarySettings();
        RefreshCalendarViews();
    }

    private void SyncSoundServiceLibrary()
    {
        SoundService.SyncLibrarySounds(SoundLibrary.Select(s => (s.Name, s.LocalPath)));
    }

    private void SaveSoundLibrarySettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.SoundLibrary = SoundLibrary
            .Select(s => new ThemeSettingsService.SoundLibraryEntryDto
            {
                Name = s.Name,
                Icon = s.Icon,
                Path = s.LocalPath,
                MimeType = s.MimeType,
                Loop = s.Loop,
                Volume = s.Volume,
                RepeatCount = s.RepeatCount,
                TrimStartMs = (long)(s.TrimStartSeconds * 1000),
                TrimEndMs = (long)(s.TrimEndSeconds * 1000)
            })
            .ToList();
        ThemeSettingsService.SaveSettings(settings);
    }

    public async Task ExportMediaLibraryAsync(string targetFolder)
    {
        try
        {
            var manifest = new LibraryManifestDto { LibraryType = "Media" };
            foreach (var item in MediaLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                File.Copy(item.LocalPath, Path.Combine(targetFolder, fileName), true);
                manifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name,
                    FileName = fileName,
                    MimeType = item.MimeType,
                    Kind = item.Kind.ToString(),
                    Loop = item.Loop
                });
            }

            await File.WriteAllTextAsync(Path.Combine(targetFolder, LibraryManifestFileName),
                JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
            Log.Information("Media library exported to {Folder} ({Count} files)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaLibraryExported"), manifest.Items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Media library export failed ({Folder})", targetFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    public async Task ImportMediaLibraryAsync(string sourceFolder)
    {
        try
        {
            var manifest = await ReadLibraryManifestAsync(sourceFolder);
            if (manifest is null) return;

            Directory.CreateDirectory(ThemeSettingsService.MediaLibraryDirectory);
            var imported = 0;
            foreach (var entry in manifest.Items)
            {
                var sourceFile = Path.Combine(sourceFolder, entry.FileName);
                if (!File.Exists(sourceFile)) continue;
                if (!Enum.TryParse<MediaKind>(entry.Kind, out var kind) || kind == MediaKind.Audio) continue;

                var cachedPath = Path.Combine(ThemeSettingsService.MediaLibraryDirectory,
                    $"{Guid.NewGuid():N}{Path.GetExtension(entry.FileName)}");
                File.Copy(sourceFile, cachedPath, true);
                MediaLibrary.Add(new MediaLibraryItemViewModel(
                    entry.Name, cachedPath, kind, entry.MimeType, entry.Loop,
                    RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged));
                imported++;
            }

            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
            Log.Information("Media library imported from {Folder} ({Count} files)", sourceFolder, imported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaImported"), imported));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Media library import failed ({Folder})", sourceFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    public async Task ExportSoundLibraryAsync(string targetFolder)
    {
        try
        {
            var manifest = new LibraryManifestDto { LibraryType = "Sound" };
            foreach (var item in SoundLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                File.Copy(item.LocalPath, Path.Combine(targetFolder, fileName), true);
                manifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name,
                    Icon = item.Icon,
                    FileName = fileName,
                    MimeType = item.MimeType,
                    Loop = item.Loop,
                    Volume = item.Volume,
                    RepeatCount = item.RepeatCount,
                    TrimStartMs = (long)(item.TrimStartSeconds * 1000),
                    TrimEndMs = (long)(item.TrimEndSeconds * 1000)
                });
            }

            await File.WriteAllTextAsync(Path.Combine(targetFolder, LibraryManifestFileName),
                JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
            Log.Information("Sound library exported to {Folder} ({Count} files)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundLibraryExported"), manifest.Items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound library export failed ({Folder})", targetFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    public async Task ImportSoundLibraryAsync(string sourceFolder)
    {
        try
        {
            var manifest = await ReadLibraryManifestAsync(sourceFolder);
            if (manifest is null) return;

            Directory.CreateDirectory(ThemeSettingsService.SoundLibraryDirectory);
            var imported = 0;
            foreach (var entry in manifest.Items)
            {
                var sourceFile = Path.Combine(sourceFolder, entry.FileName);
                if (!File.Exists(sourceFile)) continue;

                var cachedPath = Path.Combine(ThemeSettingsService.SoundLibraryDirectory,
                    $"{Guid.NewGuid():N}{Path.GetExtension(entry.FileName)}");
                File.Copy(sourceFile, cachedPath, true);
                SoundLibrary.Add(CreateSoundLibraryItem(entry.Name, entry.Icon ?? VisualItemHelper.IconTimer,
                    cachedPath,
                    entry.MimeType, entry.Loop, entry.Volume ?? 100,
                    entry.RepeatCount ?? 1, entry.TrimStartMs / 1000.0, entry.TrimEndMs / 1000.0));
                imported++;
            }

            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
            Log.Information("Sound library imported from {Folder} ({Count} files)", sourceFolder, imported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundsImported"), imported));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound library import failed ({Folder})", sourceFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    private async Task<LibraryManifestDto?> ReadLibraryManifestAsync(string sourceFolder)
    {
        var manifestPath = Path.Combine(sourceFolder, LibraryManifestFileName);
        if (!File.Exists(manifestPath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidLibraryExportFound");
            return null;
        }

        var manifest = JsonSerializer.Deserialize<LibraryManifestDto>(await File.ReadAllTextAsync(manifestPath));
        if (manifest is null) MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.ManifestCouldNotBeRead");
        return manifest;
    }

    /// <summary>Double-click handler: immediately shows and sends a medium already stored in the library.</summary>
    private async void ShowMediaLibraryItem(MediaLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Library medium not found: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.LibraryMediumNotFound");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Library medium could not be read: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return;
        }

        Log.Information("Sending library medium: {Name} ({Kind})", item.Name, item.Kind);

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = item.Kind.ToString(),
            FileName = item.Name,
            MimeType = item.MimeType,
            Loop = item.Loop,
            AddToGallery = true
        };

        // isOwnedCache: false - this is the persistent library file, which must not be
        // deleted like a one-off cache on the next media change.
        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        AddSentMediaItem(header, item.LocalPath, false);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.MediaSentLibrary"), item.Name));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaSent"), item.Name));

        await _playerServer.PublishMediaAsync(header, bytes);
        BeginVideoTracking(header, item.LocalPath, false);
    }

    private void OnMediaLibraryItemChanged(MediaLibraryItemViewModel item)
    {
        if (string.Equals(CurrentMediaLocalPath, item.LocalPath, StringComparison.OrdinalIgnoreCase))
            CurrentMediaFileName = item.Name;
        SaveMediaLibrarySettings();
        RefreshCalendarViews();
    }

    private void SaveMediaLibrarySettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.MediaLibrary = MediaLibrary
            .Select(i => new ThemeSettingsService.MediaLibraryEntryDto
            {
                Name = i.Name,
                Path = i.LocalPath,
                Kind = i.Kind.ToString(),
                MimeType = i.MimeType,
                Loop = i.Loop
            })
            .ToList();
        ThemeSettingsService.SaveSettings(settings);
    }

    private void SetCurrentMediaStatus(MediaKind kind, string localPath, string fileName, bool isOwnedCache)
    {
        var previousPath = CurrentMediaLocalPath;
        var previousWasOwned = _currentMediaIsOwnedCache;

        CurrentMediaFileName = fileName;
        CurrentMediaLocalPath = localPath;
        MediaErrorMessage = null;
        CurrentMediaKind = kind;
        _currentMediaIsOwnedCache = isOwnedCache;

        // Every newly shown medium replaces any previous event medium - the trigger
        // (if SendTriggerMediaAsync sets _activeTriggerMediaSource again right after) would otherwise
        // keep applying to a completely different medium, e.g. one shown via ad-hoc sending.
        _activeTriggerMediaSource = null;

        if (previousWasOwned) DeleteFileQuietly(previousPath);

        // Explicit instead of only via OnCurrentMediaKindChanged: for two media of the same kind
        // in a row (e.g. image -> image), CurrentMediaKind doesn't "change", but the path
        // does - without this explicit call, the local preview would remain stuck.
        RefreshLocalMediaPreview();
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            Log.Error("Failed to delete file: \"{Path}\" with error: {Error}", path, e.Message);
        }
    }

    partial void OnSlideshowSecondsPerImageChanged(double value)
    {
        _ = _playerServer.PublishSlideshowIntervalAsync(value);
        RestartSlideshowTimer();
    }

    /// <summary>
    ///     The GM's own forward/back mechanism is always the global "highlight" broadcast
    ///     (see HighlightSentMediaItem) - unlike players, the GM has no separate purely
    ///     local navigation; arrow keys in the host player window use the same commands.
    /// </summary>
    private void RestartSlideshowTimer()
    {
        _slideshowTimer?.Stop();
        _slideshowTimer = null;

        if (SlideshowSecondsPerImage <= 0) return;

        _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SlideshowSecondsPerImage) };
        _slideshowTimer.Tick += (_, _) => AdvanceSlideshowIfDue();
        _slideshowTimer.Start();
    }

    /// <summary>
    ///     Videos are never automatically advanced away - they have their own natural
    ///     duration; the timer simply skips this tick and checks again next time.
    /// </summary>
    private void AdvanceSlideshowIfDue()
    {
        if (SentMediaItems.Count == 0) return;

        var current = _currentGalleryMediaId is null
            ? null
            : SentMediaItems.FirstOrDefault(i => i.MediaId == _currentGalleryMediaId);
        if (current is not null && current.Kind == MediaKind.Video) return;

        NextGalleryItemCommand.Execute(null);
    }

    [RelayCommand]
    private void NextGalleryItem()
    {
        NavigateGallery(1);
    }

    [RelayCommand]
    private void PreviousGalleryItem()
    {
        NavigateGallery(-1);
    }

    private void NavigateGallery(int direction)
    {
        if (SentMediaItems.Count == 0) return;

        var items = SentMediaItems;
        var currentIndex = _currentGalleryMediaId is null
            ? -1
            : items.ToList().FindIndex(i => i.MediaId == _currentGalleryMediaId);
        var nextIndex = ((currentIndex < 0 ? 0 : currentIndex) + direction + items.Count) % items.Count;
        HighlightSentMediaItem(items[nextIndex]);
    }

    private void AddSentMediaItem(MediaHeaderDto header, string localPath, bool isOwnedCache)
    {
        if (!Enum.TryParse<MediaKind>(header.Kind, out var kind)) return;

        var sourceItem = FindMediaLibraryItemByPath(localPath);
        var item = new SentMediaItemViewModel(header.MediaId, header.FileName, kind, localPath, header.MimeType,
            isOwnedCache,
            HighlightSentMediaItem,
            RetractSentMediaItem,
            sourceItem);
        SentMediaItems.Add(item);
        OnPropertyChanged(nameof(HasNoSentMediaItems));
        _currentGalleryMediaId = header.MediaId;
    }

    /// <summary>
    ///     GM click on a gallery item: jumps at the GM and all players (locally, non-
    ///     blocking) to this image/video, without transmitting it over the network again.
    /// </summary>
    private void HighlightSentMediaItem(SentMediaItemViewModel item)
    {
        _currentGalleryMediaId = item.MediaId;
        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        _ = _playerServer.PublishHighlightAsync(item.MediaId);
    }

    private void RetractSentMediaItem(SentMediaItemViewModel item)
    {
        item.Dispose();
        SentMediaItems.Remove(item);
        OnPropertyChanged(nameof(HasNoSentMediaItems));

        if (_currentGalleryMediaId == item.MediaId)
        {
            _currentGalleryMediaId = null;
            if (CurrentMediaLocalPath == item.LocalPath) ClearMediaCommand.Execute(null);
        }

        if (item.IsOwnedCache) DeleteFileQuietly(item.LocalPath);
        _ = _playerServer.PublishRetractAsync(item.MediaId);
    }

    partial void OnIsClockRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleClockLabel));
        OnPropertyChanged(nameof(NextEventJumpLabel));
        OnPropertyChanged(nameof(ClockStatusText));
    }

    [RelayCommand]
    private void ToggleNetworkServer()
    {
        if (IsNetworkServerRunning)
        {
            _playerServer.Stop();
            IsNetworkServerRunning = false;
            NetworkServerStatus = LocalizationService.Get("MainWindowViewModel.Network.ServerOff");
            NetworkServerAddress = null;
            RemoteFullscreen = false;
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
            OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
            ClearRemoteOnlyActiveSounds();
            return;
        }

        try
        {
            var port = NetworkServerPort is > 0 and <= 65535
                ? NetworkServerPort
                : TcpPlayerServerService.DefaultPort;

            NetworkServerPort = port;
            var announcedName = string.IsNullOrWhiteSpace(ServerName) ? "RpgTimeTracker" : ServerName.Trim();
            if (announcedName.Length > 60) announcedName = announcedName[..60];
            _playerServer.Start(port, announcedName);
            IsNetworkServerRunning = true;
            OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
            NetworkServerAddress = PlayerMdnsAnnouncer.GetBestLocalIPv4()?.ToString();
            NetworkServerStatus = string.Format(LocalizationService.Get("MainWindowViewModel.Network.ServerActiveOnPort"), port);
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Network server could not be started on port {Port}", NetworkServerPort);
            IsNetworkServerRunning = false;
            NetworkServerStatus = string.Format(LocalizationService.Get("MainWindowViewModel.Network.ServerStartFailed"), ex.Message);
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        }
    }

    [RelayCommand]
    private void ToggleClock()
    {
        if (_clock.IsRunning)
        {
            _clock.Pause();
            IsClockRunning = _clock.IsRunning;
            Log.Information("Game time paused at {GameTime}", _clock.CurrentTime);
            _ = _playerServer.PublishClockStoppedAsync();
        }
        else
        {
            _clock.Start();
            IsClockRunning = _clock.IsRunning;
            Log.Information("Game time started at {GameTime} (speed {Speed}x)", _clock.CurrentTime,
                SpeedMultiplier);
            _ = _playerServer.PublishClockStartedAsync();
        }
    }

    [RelayCommand]
    private void ApplyManualDateTime()
    {
        if (!DateTime.TryParse(ManualDateTimeText, out var parsed))
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidDateTime");
            return;
        }

        ClockErrorMessage = null;
        var delta = parsed - _clock.CurrentTime;
        if (delta == TimeSpan.Zero) return;

        Log.Information("Game time manually set: {OldTime} -> {NewTime}", _clock.CurrentTime, parsed);
        _clock.SetTime(parsed);
        PushJump(delta);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.GameTimeManuallySet"), FormatGameTime(parsed)));
    }


    [RelayCommand]
    private void JumpToNextEvent()
    {
        var next = GetNextEventDelta();
        if (next is null || next.Value <= TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoNextEventFound");
            return;
        }

        ClockErrorMessage = null;
        JumpBy(next.Value);
    }

    private TimeSpan? GetNextEventDelta()
    {
        var candidates = Timers
            .Select(t => t.TimeUntilNextEvent)
            .Concat(Alarms.Select(a => a.TimeUntilNextEvent))
            .Concat(IntervalEvents.Select(i => i.TimeUntilNextEvent))
            .Where(delta => delta.HasValue && delta.Value > TimeSpan.Zero)
            .Select(delta => delta!.Value)
            .ToList();

        return candidates.Count == 0 ? null : candidates.Min();
    }

    // ==================== Time jump (forward & backward) ====================

    [RelayCommand]
    private void JumpForward()
    {
        if (!TimeSpan.TryParse(JumpAmountText, out var parsed) || parsed == TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidTimeJump");
            return;
        }

        ClockErrorMessage = null;
        JumpBy(parsed);
    }

    [RelayCommand]
    private void JumpBackward()
    {
        if (!TimeSpan.TryParse(JumpAmountText, out var parsed) || parsed == TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidTimeJump");
            return;
        }

        ClockErrorMessage = null;
        JumpBy(-parsed);
    }

    // Called by the quick-select buttons with a TimeSpan string as CommandParameter.
    [RelayCommand]
    private void QuickJump(string amount)
    {
        if (TimeSpan.TryParse(amount, out var parsed)) JumpBy(parsed);
    }

    private void JumpBy(TimeSpan delta)
    {
        Log.Information("Time jump of {Delta} from {OldTime}", delta, _clock.CurrentTime);
        _clock.Jump(delta);
        PushJump(delta);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.TimeJump"),
            delta < TimeSpan.Zero ? "-" : "+", FormatTimeSpan(delta.Duration())));
    }

    /// <summary>Records an already applied jump on the undo stack and clears the redo stack (see there).</summary>
    private void PushJump(TimeSpan delta)
    {
        _jumpUndoStack.Push(delta);
        _jumpRedoStack.Clear();
        CanUndoJump = true;
        CanRedoJump = false;
    }

    [RelayCommand]
    private void UndoJump()
    {
        if (_jumpUndoStack.Count == 0) return;

        var delta = _jumpUndoStack.Pop();
        Log.Information("Time jump undone: counter-jump of {Delta} from {OldTime}", -delta,
            _clock.CurrentTime);
        _clock.Jump(-delta);
        _jumpRedoStack.Push(delta);
        CanUndoJump = _jumpUndoStack.Count > 0;
        CanRedoJump = true;
        RecordSessionEvent(LocalizationService.Get("MainWindowViewModel.Events.TimeJumpUndone"));
    }

    [RelayCommand]
    private void RedoJump()
    {
        if (_jumpRedoStack.Count == 0) return;

        var delta = _jumpRedoStack.Pop();
        Log.Information("Time jump redone: repeated jump of {Delta} from {OldTime}", delta,
            _clock.CurrentTime);
        _clock.Jump(delta);
        _jumpUndoStack.Push(delta);
        CanUndoJump = true;
        CanRedoJump = _jumpRedoStack.Count > 0;
        RecordSessionEvent(LocalizationService.Get("MainWindowViewModel.Events.TimeJumpRedone"));
    }

    // ==================== Jump markers ====================

    private void AddDefaultMarker(string name, TimeSpan timeOfDay)
    {
        var model = new JumpMarker { Name = name, TimeOfDay = timeOfDay };
        JumpMarkers.Add(new JumpMarkerItemViewModel(model, () => _clock.CurrentTime, JumpBy, RemoveMarker));
    }

    [RelayCommand]
    private void AddMarker()
    {
        if (!TimeSpan.TryParse(NewMarkerTime, out var parsed) || parsed < TimeSpan.Zero ||
            parsed >= TimeSpan.FromDays(1))
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidMarkerTime");
            return;
        }

        ClockErrorMessage = null;

        var model = new JumpMarker
        {
            Name = string.IsNullOrWhiteSpace(NewMarkerName)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.MarkerName")
                : NewMarkerName,
            TimeOfDay = parsed
        };
        JumpMarkers.Add(new JumpMarkerItemViewModel(model, () => _clock.CurrentTime, JumpBy, RemoveMarker));
        NewMarkerName = LocalizationService.Get("MainWindowViewModel.Defaults.NewMarkerName");
    }

    private void RemoveMarker(JumpMarkerItemViewModel vm)
    {
        JumpMarkers.Remove(vm);
    }

    private static TimeSpan ComposeNewTimeSpan(decimal hours, decimal minutes, decimal seconds)
    {
        var h = Math.Max(0, (int)hours);
        var m = Math.Clamp((int)minutes, 0, 59);
        var s = Math.Clamp((int)seconds, 0, 59);
        return new TimeSpan(h, m, s);
    }

    private static string FormatShortTimeSpan(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}.{ts:hh\\:mm\\:ss}"
            : $"{ts:hh\\:mm\\:ss}";
    }

    // ==================== Timer ====================

    [RelayCommand]
    private void AddTimer()
    {
        if (!TimeSpan.TryParse(NewTimerDuration, out var duration) || duration <= TimeSpan.Zero)
            duration = ComposeNewTimeSpan(NewTimerDurationHours, NewTimerDurationMinutes, NewTimerDurationSeconds);

        if (duration <= TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidTimerDuration");
            return;
        }

        ClockErrorMessage = null;

        var model = new TimerItem
        {
            Name = string.IsNullOrWhiteSpace(NewTimerBasics.Name)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.TimerName")
                : NewTimerBasics.Name,
            Icon = VisualItemHelper.NormalizeIcon(NewTimerBasics.Icon),
            Duration = duration,
            Sound = string.IsNullOrWhiteSpace(NewTimerBasics.Sound) ? SoundService.Pling : NewTimerBasics.Sound,
            SoundRepeatCount = NewTimerBasics.SoundRepeatCount < 0 ? 0 : NewTimerBasics.SoundRepeatCount,
            ColorHex = NewTimerBasics.ColorHex,
            Blink = NewTimerBasics.Blink,
            IsPlayerVisible = NewTimerBasics.IsPlayerVisible
        };
        var vm = new TimerItemViewModel(model, RemoveTimer);
        NewTimerTriggerMedia.CopyTo(vm.TriggerMedia);
        vm.StateChanged += () => PublishItemState(vm);
        vm.MediaResetRequested += () => StopMediaIfTriggeredBy(vm.TriggerMedia);
        vm.MediaResetRequested += () => StopInfiniteSoundLoop(vm.Id);
        Timers.Add(vm);
        AddTimelineItem(vm);
        PublishItemState(vm);
        Log.Information("Timer created: {Name} (duration {Duration}, Id={Id})", vm.Name, duration, vm.Id);
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.TimerCreated"), vm.Name));

        NewTimerBasics.ResetToDefaults();
        NewTimerDuration = "00:10:00";
        NewTimerDurationHours = 0;
        NewTimerDurationMinutes = 10;
        NewTimerDurationSeconds = 0;
        NewTimerTriggerMedia.ClearCommand.Execute(null);
    }

    private void RemoveTimer(TimerItemViewModel vm)
    {
        Timers.Remove(vm);
        RemoveTimelineItem(item => item.Wraps(vm));
        _ = _playerServer.PublishTimelineItemRemovedAsync(vm.Id);
        StopInfiniteSoundLoop(vm.Id);
        Log.Information("Timer removed: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Interval/on-time objects ====================

    [RelayCommand]
    private void AddIntervalEvent()
    {
        if (!TimeSpan.TryParse(NewIntervalInterval, out var interval) || interval <= TimeSpan.Zero)
            interval = ComposeNewTimeSpan(NewIntervalIntervalHours, NewIntervalIntervalMinutes,
                NewIntervalIntervalSeconds);

        if (interval <= TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidInterval");
            return;
        }

        if (!TimeSpan.TryParse(NewIntervalActiveDuration, out var activeDuration) || activeDuration <= TimeSpan.Zero)
            activeDuration =
                ComposeNewTimeSpan(NewIntervalActiveHours, NewIntervalActiveMinutes, NewIntervalActiveSeconds);

        if (activeDuration <= TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidActiveDuration");
            return;
        }

        if (activeDuration > interval)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.ActiveDurationExceedsInterval");
            return;
        }

        int? maxRepeats = null;
        if (!string.IsNullOrWhiteSpace(NewIntervalMaxRepeats))
        {
            if (!int.TryParse(NewIntervalMaxRepeats, out var parsedMax) || parsedMax < 0)
            {
                ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidMaxRepeats");
                return;
            }

            maxRepeats = parsedMax == 0 ? null : parsedMax;
        }

        ClockErrorMessage = null;

        var model = new IntervalEventItem
        {
            Name = string.IsNullOrWhiteSpace(NewIntervalBasics.Name)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.IntervalName")
                : NewIntervalBasics.Name,
            Icon = VisualItemHelper.NormalizeIcon(NewIntervalBasics.Icon),
            Interval = interval,
            ActiveDuration = activeDuration,
            MaxRepeats = maxRepeats,
            Sound = string.IsNullOrWhiteSpace(NewIntervalBasics.Sound) ? SoundService.Pling : NewIntervalBasics.Sound,
            SoundRepeatCount = NewIntervalBasics.SoundRepeatCount < 0 ? 0 : NewIntervalBasics.SoundRepeatCount,
            ColorHex = string.IsNullOrWhiteSpace(NewIntervalBasics.ColorHex) ? "#FFD45A" : NewIntervalBasics.ColorHex,
            Blink = NewIntervalBasics.Blink,
            IsPlayerVisible = NewIntervalBasics.IsPlayerVisible
        };

        var vm = new IntervalEventItemViewModel(model, RemoveIntervalEvent);
        NewIntervalTriggerMedia.CopyTo(vm.TriggerMedia);
        vm.StateChanged += () => PublishItemState(vm);
        vm.ResetRequested += () => StopInfiniteSoundLoop(vm.Id);
        IntervalEvents.Add(vm);
        AddTimelineItem(vm);
        PublishItemState(vm);
        Log.Information("OnTime interval created: {Name} (every {Interval}, active for {Active}, Id={Id})", vm.Name,
            interval, activeDuration, vm.Id);
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.IntervalCreated"), vm.Name));

        NewIntervalBasics.ResetToDefaults();
        NewIntervalInterval = "00:10:00";
        NewIntervalIntervalHours = 0;
        NewIntervalIntervalMinutes = 10;
        NewIntervalIntervalSeconds = 0;
        NewIntervalActiveDuration = "00:01:00";
        NewIntervalActiveHours = 0;
        NewIntervalActiveMinutes = 1;
        NewIntervalActiveSeconds = 0;
        NewIntervalMaxRepeats = string.Empty;
        NewIntervalTriggerMedia.ClearCommand.Execute(null);
    }

    private void RemoveIntervalEvent(IntervalEventItemViewModel vm)
    {
        IntervalEvents.Remove(vm);
        RemoveTimelineItem(item => item.Wraps(vm));
        _ = _playerServer.PublishTimelineItemRemovedAsync(vm.Id);
        StopInfiniteSoundLoop(vm.Id);
        Log.Information("OnTime interval removed: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Alarm ====================

    [RelayCommand]
    private void AddAlarm()
    {
        if (!DateTime.TryParse(NewAlarmDateTime, out var triggerAt))
        {
            if (NewAlarmDate.HasValue)
            {
                triggerAt = NewAlarmDate.Value.Date.Add(NewAlarmTime ?? TimeSpan.Zero);
                NewAlarmDateTime = triggerAt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidAlarmDate");
                return;
            }
        }

        TimeSpan? repeat = null;
        if (!string.IsNullOrWhiteSpace(NewAlarmRepeatInterval))
        {
            if (!TimeSpan.TryParse(NewAlarmRepeatInterval, out var parsedRepeat) || parsedRepeat <= TimeSpan.Zero)
            {
                ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidRepeatInterval");
                return;
            }

            repeat = parsedRepeat;
        }
        else
        {
            var pickedRepeat = ComposeNewTimeSpan(NewAlarmRepeatHours, NewAlarmRepeatMinutes, NewAlarmRepeatSeconds);
            if (pickedRepeat > TimeSpan.Zero)
            {
                repeat = pickedRepeat;
                NewAlarmRepeatInterval = FormatShortTimeSpan(pickedRepeat);
            }
        }

        ClockErrorMessage = null;

        var model = new AlarmItem
        {
            Name = string.IsNullOrWhiteSpace(NewAlarmBasics.Name)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.AlarmName")
                : NewAlarmBasics.Name,
            Icon = VisualItemHelper.NormalizeIcon(NewAlarmBasics.Icon),
            TriggerAt = triggerAt,
            RepeatInterval = repeat,
            Sound = string.IsNullOrWhiteSpace(NewAlarmBasics.Sound) ? SoundService.Pling : NewAlarmBasics.Sound,
            SoundRepeatCount = NewAlarmBasics.SoundRepeatCount < 0 ? 0 : NewAlarmBasics.SoundRepeatCount,
            ColorHex = NewAlarmBasics.ColorHex,
            Blink = NewAlarmBasics.Blink,
            IsPlayerVisible = NewAlarmBasics.IsPlayerVisible
        };
        var vm = new AlarmItemViewModel(model, _clock.CurrentTime, RemoveAlarm);
        NewAlarmTriggerMedia.CopyTo(vm.TriggerMedia);
        vm.StateChanged += () => PublishItemState(vm);
        vm.ResetRequested += () => StopInfiniteSoundLoop(vm.Id);
        Alarms.Add(vm);
        AddTimelineItem(vm);
        PublishItemState(vm);
        Log.Information("Alarm created: {Name} (target time {TriggerAt}, Id={Id})", vm.Name, triggerAt, vm.Id);
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AlarmCreated"), vm.Name));

        var nextAlarm = _clock.CurrentTime.AddHours(8);
        NewAlarmDate = new DateTimeOffset(nextAlarm.Date);
        NewAlarmTime = nextAlarm.TimeOfDay;
        NewAlarmDateTime = nextAlarm.ToString("yyyy-MM-dd HH:mm:ss");
        NewAlarmRepeatHours = 0;
        NewAlarmRepeatMinutes = 0;
        NewAlarmRepeatSeconds = 0;
        NewAlarmBasics.ResetToDefaults();
        NewAlarmTriggerMedia.ClearCommand.Execute(null);
    }

    private void RemoveAlarm(AlarmItemViewModel vm)
    {
        Alarms.Remove(vm);
        RemoveTimelineItem(item => item.Wraps(vm));
        _ = _playerServer.PublishTimelineItemRemovedAsync(vm.Id);
        StopInfiniteSoundLoop(vm.Id);
        Log.Information("Alarm removed: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Clock tick (normal & time jump) ====================

    private void OnClockTick(DateTime newTime, TimeSpan gameDelta)
    {
        var previousTime = newTime - gameDelta;
        CurrentGameTimeText = FormatGameTime(newTime);
        OnPropertyChanged(nameof(NextEventJumpLabel));
        ManualDateTimeText = newTime.ToString("yyyy-MM-dd HH:mm:ss");
        UpdateAmbience();

        // gameDelta can also be negative here (backward jump) - timers
        // only react when actively running; alarms sync against the
        // absolute point in time (which can also "disarm" them).
        foreach (var timer in Timers)
        {
            if (timer.Advance(gameDelta))
            {
                PlaySound(timer.Id, timer.SoundToPlay, timer.SoundRepeatCountToPlay);
                TriggerEventMedia(timer.TriggerMedia);
                RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.TimerExpired"), timer.Name));
            }

            CheckHeadsUpWarning(timer.Id, timer.Name,
                LocalizationService.Get("MainWindowViewModel.Labels.KindTimer"), timer.TimeUntilNextEvent);
        }

        foreach (var alarm in Alarms)
        {
            if (alarm.Sync(newTime))
            {
                PlaySound(alarm.Id, alarm.SoundToPlay, alarm.SoundRepeatCountToPlay);
                TriggerEventMedia(alarm.TriggerMedia);
                RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.AlarmTriggered"), alarm.Name));
            }

            CheckHeadsUpWarning(alarm.Id, alarm.Name,
                LocalizationService.Get("MainWindowViewModel.Labels.KindAlarm"), alarm.TimeUntilNextEvent);
        }

        foreach (var intervalEvent in IntervalEvents)
        {
            if (intervalEvent.Advance(gameDelta))
            {
                PlaySound(intervalEvent.Id, intervalEvent.SoundToPlay, intervalEvent.SoundRepeatCountToPlay);
                TriggerEventMedia(intervalEvent.TriggerMedia);
                RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.IntervalActive"), intervalEvent.Name));
            }

            CheckHeadsUpWarning(intervalEvent.Id, intervalEvent.Name,
                LocalizationService.Get("MainWindowViewModel.Labels.KindOnTime"), intervalEvent.TimeUntilNextEvent);
        }

        foreach (var item in TimelineItems) item.RefreshAll();
        SyncPlayerTimelineItems();
        TriggerCalendarEntries(previousTime, newTime);
        RefreshCalendarViews();
    }

    private void TriggerCalendarEntries(DateTime previousTime, DateTime currentTime)
    {
        if (currentTime < previousTime)
            return;

        foreach (var entry in CalendarEntries)
        {
            if (!entry.TryBuildDefinition(out var definition))
                continue;

            var nextOccurrence = definition.GetNextOccurrenceAtOrAfter(previousTime);
            if (nextOccurrence is null || nextOccurrence > currentTime)
                continue;

            TriggerCalendarMedia(definition);
            RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.CalendarEntryTriggered"), definition.Title));
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.CalendarEntry"), definition.Title));
        }
    }

    private void TriggerCalendarMedia(CalendarEntryDefinition definition)
    {
        if (!definition.HasTrigger || string.IsNullOrWhiteSpace(definition.TriggerPath))
            return;

        if (definition.TriggerKind == MediaKind.Audio)
        {
            var soundItem = SoundLibrary.FirstOrDefault(item =>
                string.Equals(item.LocalPath, definition.TriggerPath, StringComparison.OrdinalIgnoreCase));
            if (soundItem is not null)
            {
                PlaySoundLibraryItem(soundItem);
                return;
            }

            _ = SendSoundFromPathAsync(definition.TriggerPath);
            return;
        }

        var config = new TriggerMediaConfig
        {
            Path = definition.TriggerPath,
            FileName = definition.TriggerFileName,
            Kind = definition.TriggerKind,
            Fullscreen = definition.TriggerFullscreen,
            PauseClockDuringVideo = definition.TriggerPauseClockDuringVideo,
            Loop = definition.TriggerLoop
        };
        TriggerEventMedia(config);
    }

    private void PlaySound(Guid itemId, string? soundName, int repeatCount)
    {
        StopInfiniteSoundLoop(itemId);

        // A sound library entry (instead of a built-in like Pling) is sent to all players
        // instead of only being played locally at the GM - see PlayLibrarySoundForItemAsync.
        if (SoundService.TryGetLibrarySoundPath(soundName, out var libraryPath))
        {
            _ = PlayLibrarySoundForItemAsync(itemId, soundName!, libraryPath, repeatCount <= 0);
            return;
        }

        if (repeatCount <= 0)
        {
            var cts = new CancellationTokenSource();
            _infiniteSoundLoops[itemId] = cts;
            SoundService.Play(soundName, repeatCount, cts.Token);
            return;
        }

        SoundService.Play(soundName, repeatCount);
    }

    /// <summary>
    ///     Sends a sound library sound triggered by a timer/alarm/interval to all
    ///     players (and locally at the GM if applicable, see SendSoundAsync). Loop=true (SoundRepeatCount==0)
    ///     corresponds to the "endless" semantics of the built-in sounds; a fixed repeat count &gt; 0
    ///     deliberately sends the sound only ONCE (no replicating the built-in repeat-with-pause
    ///     behavior for potentially longer library sounds).
    /// </summary>
    private async Task PlayLibrarySoundForItemAsync(Guid itemId, string name, string localPath, bool loop)
    {
        if (!File.Exists(localPath))
        {
            Log.Warning("Item sound (library) not found: {Name} ({LocalPath})", name, localPath);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(localPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Item sound (library) could not be read: {Name}", name);
            return;
        }

        var soundItem = SoundLibrary.FirstOrDefault(s => s.LocalPath == localPath);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = MediaKind.Audio.ToString(),
            FileName = name,
            MimeType = soundItem?.MimeType ?? "audio/*",
            Loop = loop,
            Volume = soundItem?.Volume ?? 100,
            TrimStartMs = (long)((soundItem?.TrimStartSeconds ?? 0) * 1000),
            TrimEndMs = (long)((soundItem?.TrimEndSeconds ?? 0) * 1000)
        };

        _itemSoundMediaIds[itemId] = header.MediaId;
        await SendSoundAsync(header, bytes, localPath, false);
    }

    /// <summary>
    ///     Cancels a running infinite sound repeat (built-in) or a running
    ///     sound library sound for this item, if present - otherwise a no-op.
    /// </summary>
    private void StopInfiniteSoundLoop(Guid itemId)
    {
        if (_infiniteSoundLoops.Remove(itemId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_itemSoundMediaIds.Remove(itemId, out var mediaId)) StopSound(mediaId);
    }

    private void RecordSessionEvent(string text)
    {
        if (!RecordSessionLog) return;

        SessionLogEntries.Add($"[{FormatGameTime(_clock.CurrentTime)}] {text}");
        OnPropertyChanged(nameof(HasSessionLogEntries));
    }

    [RelayCommand]
    private void ClearSessionLog()
    {
        SessionLogEntries.Clear();
        OnPropertyChanged(nameof(HasSessionLogEntries));
    }

    /// <summary>
    ///     Builds the export text; the actual saving (target selectable) is handled by the code-behind, see
    ///     OnExportSessionLogClick.
    /// </summary>
    public string BuildSessionLogExportText()
    {
        return string.Join(Environment.NewLine, SessionLogEntries);
    }

    private void CheckHeadsUpWarning(Guid id, string name, string kindLabel, TimeSpan? remaining)
    {
        if (remaining is null)
        {
            _headsUpLastRemaining.Remove(id);
            _headsUpFired.Remove(id);
            return;
        }

        var value = remaining.Value;

        // Larger than at the last tick (or seen for the first time) means: newly started/reset/
        // a new alarm cycle - the heads-up warning for THIS occurrence may fire again.
        if (!_headsUpLastRemaining.TryGetValue(id, out var previous) || value > previous) _headsUpFired.Remove(id);
        _headsUpLastRemaining[id] = value;

        if (!HeadsUpWarningEnabled) return;

        var leadTime = TimeSpan.FromMinutes((double)Math.Max(0, HeadsUpLeadMinutes));
        if (leadTime <= TimeSpan.Zero) return;

        if (_headsUpFired.Contains(id)) return;
        if (value <= TimeSpan.Zero || value > leadTime) return;

        _headsUpFired.Add(id);
        ShowHeadsUpMessage(string.Format(LocalizationService.Get("MainWindowViewModel.Status.HeadsUpWarning"),
            kindLabel, name, FormatTimeSpan(value)));
    }

    private void ShowHeadsUpMessage(string message)
    {
        Log.Information("Heads-up warning: {Message}", message);
        HeadsUpMessage = message;

        _headsUpMessageClearCts?.Cancel();
        var cts = new CancellationTokenSource();
        _headsUpMessageClearCts = cts;
        _ = ClearHeadsUpMessageAfterDelayAsync(message, cts.Token);
    }

    private async Task ClearHeadsUpMessageAfterDelayAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (HeadsUpMessage == message) HeadsUpMessage = null;
    }

    /// <summary>
    ///     For file dialog actions in the code-behind (save/load) that don't have access to the private
    ///     ShowActionStatus.
    /// </summary>
    public void NotifyActionStatus(string message)
    {
        ShowActionStatus(message);
    }

    /// <summary>
    ///     Short success notice for completed actions (e.g. "timer created", "medium
    ///     sent") - errors still go through ClockErrorMessage/MediaErrorMessage. Appears in
    ///     the status bar (notifications flyout), no longer as its own reflow-prone line.
    /// </summary>
    private void ShowActionStatus(string message)
    {
        ActionStatusMessage = message;

        ActionStatusHistory.Insert(0, message);
        while (ActionStatusHistory.Count > MaxActionStatusHistory)
            ActionStatusHistory.RemoveAt(ActionStatusHistory.Count - 1);
        OnPropertyChanged(nameof(HasNoActionStatusHistory));

        _actionStatusClearCts?.Cancel();
        var cts = new CancellationTokenSource();
        _actionStatusClearCts = cts;
        _ = ClearActionStatusAfterDelayAsync(message, cts.Token);
    }

    private async Task ClearActionStatusAfterDelayAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ActionStatusMessage == message) ActionStatusMessage = null;
    }

    // ==================== Event medium (timer/alarm/interval trigger image/video) ====================

    private void TriggerEventMedia(TriggerMediaConfig config)
    {
        if (!config.HasMedia || string.IsNullOrEmpty(config.Path) || !File.Exists(config.Path)) return;

        _ = SendTriggerMediaAsync(config);
    }

    private async Task SendTriggerMediaAsync(TriggerMediaConfig config)
    {
        var path = config.Path!;
        Log.Information(
            "Event medium triggered: {Path} (Loop={Loop}, Fullscreen={Fullscreen}, PauseClock={PauseClock})",
            path, config.Loop, config.Fullscreen, config.PauseClockDuringVideo);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.EventMediumTriggered"), config.FileName));

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Event medium could not be read ({Path})", path);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.EventMediumCouldNotBeSent"), ex.Message);
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(config.FileName) ? Path.GetFileName(path) : config.FileName;
        MediaTypeHelper.TryGetKind(path, out _, out var mimeType);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = config.Kind.ToString(),
            FileName = fileName,
            MimeType = mimeType,
            Loop = config.Loop
        };

        if (config.Kind == MediaKind.Audio)
        {
            // Sounds are decoupled from the "current medium" slot (see the SendSoundAsync comment) -
            // no SetCurrentMediaStatus/BeginVideoTracking, otherwise a sound trigger would
            // replace a currently shown image/video or cut off its end tracking. Instead
            // bound to this event via _triggerSoundMediaIds, so StopMediaIfTriggeredBy also ends it
            // when the triggering timer/alarm/interval is reset.
            _triggerSoundMediaIds[config] = header.MediaId;
            await SendSoundAsync(header, bytes, path, false);
        }
        else
        {
            SetCurrentMediaStatus(config.Kind, path, fileName, false);
            _activeTriggerMediaSource = config;

            await _playerServer.PublishMediaAsync(header, bytes);
            BeginVideoTracking(header, path, config.PauseClockDuringVideo);
        }

        if (config.Fullscreen) SetDisplayFullscreen(true);
    }

    /// <summary>
    ///     Central send path for EVERY sound (ad-hoc, library, event trigger, item sound): sends it
    ///     over the network, optionally previews it locally at the GM, and tracks it in the "currently
    ///     playing" panel. localPath/deleteLocalAfterPlayback only control the local GM preview (see
    ///     PlayLocalSoundIfNeeded); the network send always runs independently of that.
    /// </summary>
    private async Task SendSoundAsync(MediaHeaderDto header, byte[] bytes, string localPath,
        bool deleteLocalAfterPlayback)
    {
        await _playerServer.PublishMediaAsync(header, bytes);
        PlayLocalSoundIfNeeded(header, localPath, deleteLocalAfterPlayback);
        AddActiveSound(header, localPath);
    }

    private void AddActiveSound(MediaHeaderDto header, string localPath)
    {
        var sourceItem = FindSoundLibraryItemByPath(localPath);
        var entry = new ActiveSoundViewModel(
            header.MediaId,
            header.FileName,
            sourceItem?.Icon ?? VisualItemHelper.IconTimer,
            header.Loop,
            header.Volume,
            sound => StopSound(sound.MediaId),
            (sound, volume) =>
            {
                if (_activeLocalSoundPlayers.TryGetValue(sound.MediaId, out var localPlayer))
                    localPlayer.Volume = volume;
                _ = _playerServer.PublishSetSoundVolumeAsync(sound.MediaId, volume);
            },
            sourceItem);

        ActivePlayingSounds.Add(entry);
        OnPropertyChanged(nameof(HasNoActivePlayingSounds));
    }

    /// <summary>
    ///     Removes a sound from the panel + all event/item associations - the common point
    ///     for every way a sound can end (natural end, manual stop, "stop all").
    /// </summary>
    private void RemoveActiveSound(string mediaId)
    {
        for (var i = ActivePlayingSounds.Count - 1; i >= 0; i--)
            if (ActivePlayingSounds[i].MediaId == mediaId)
            {
                ActivePlayingSounds[i].Dispose();
                ActivePlayingSounds.RemoveAt(i);
            }

        OnPropertyChanged(nameof(HasNoActivePlayingSounds));

        foreach (var key in _triggerSoundMediaIds.Where(kv => kv.Value == mediaId).Select(kv => kv.Key).ToList())
            _triggerSoundMediaIds.Remove(key);
        foreach (var key in _itemSoundMediaIds.Where(kv => kv.Value == mediaId).Select(kv => kv.Key).ToList())
            _itemSoundMediaIds.Remove(key);
    }

    private MediaLibraryItemViewModel? FindMediaLibraryItemByPath(string localPath)
    {
        return MediaLibrary.FirstOrDefault(item =>
            string.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
    }

    private SoundLibraryItemViewModel? FindSoundLibraryItemByPath(string localPath)
    {
        return SoundLibrary.FirstOrDefault(item =>
            string.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     GM-side stop of a single sound: ends it on all player clients
    ///     (media.stopSound) AND any local GM preview, then cleans up the panel.
    /// </summary>
    private void StopSound(string mediaId)
    {
        _ = _playerServer.PublishStopSoundAsync(mediaId);

        if (_activeLocalSoundPlayers.Remove(mediaId, out var player))
        {
            player.Stop();
            player.Dispose();
            _localSoundRepeatsRemaining.Remove(mediaId);
            if (_localSoundCleanupPaths.Remove(mediaId, out var path)) DeleteFileQuietly(path);
        }

        RemoveActiveSound(mediaId);
    }

    [RelayCommand]
    private void StopAllSounds()
    {
        foreach (var sound in ActivePlayingSounds.ToList()) StopSound(sound.MediaId);
    }

    private void PlayLocalSoundIfNeeded(MediaHeaderDto header, string localPath, bool deleteAfterPlayback)
    {
        // Plays locally whenever the player window is open, same as local media/map preview -
        // regardless of connected clients, so the GM hears what players hear.
        if (!IsPlayerWindowOpen)
        {
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
            return;
        }

        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
            return;
        }

        try
        {
            var player = new MediaPlayer(libVlc);
            var mediaId = header.MediaId;
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => OnLocalSoundEnded(mediaId));

            _activeLocalSoundPlayers[mediaId] = player;
            _localSoundRepeatsRemaining[mediaId] = header.Loop ? -1 : Math.Max(1, header.RepeatCount);
            if (deleteAfterPlayback) _localSoundCleanupPaths[mediaId] = localPath;

            using var media = new Media(libVlc, localPath);
            VlcMediaService.ApplySoundTrim(media, header.TrimStartMs, header.TrimEndMs);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound could not be played locally: {Path}", localPath);
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
        }
    }

    private void OnLocalSoundEnded(string mediaId)
    {
        if (!_activeLocalSoundPlayers.TryGetValue(mediaId, out var player)) return;

        var remaining = _localSoundRepeatsRemaining.GetValueOrDefault(mediaId, 0);
        if (remaining < 0 || remaining > 1)
        {
            if (remaining > 0) _localSoundRepeatsRemaining[mediaId] = remaining - 1;
            player.Stop();
            player.Play();
            return;
        }

        _activeLocalSoundPlayers.Remove(mediaId);
        _localSoundRepeatsRemaining.Remove(mediaId);
        player.Stop();
        player.Dispose();

        if (_localSoundCleanupPaths.Remove(mediaId, out var path)) DeleteFileQuietly(path);

        // Only relevant if the sound was NOT also running remotely (otherwise the
        // ClientReportedPlaybackEnded report already removes the panel entry) - RemoveActiveSound
        // is idempotent, so a duplicate call doesn't hurt.
        RemoveActiveSound(mediaId);
    }

    /// <summary>
    ///     When the last player client loses connection (or the server stops), all
    ///     purely remotely running sounds become unreachable - the client does stop them itself
    ///     (see ClientMainWindowViewModel.StopAllSounds), but the GM panel must reflect that.
    /// </summary>
    private void ClearRemoteOnlyActiveSounds()
    {
        foreach (var sound in ActivePlayingSounds.Where(s => !_activeLocalSoundPlayers.ContainsKey(s.MediaId)).ToList())
            RemoveActiveSound(sound.MediaId);
    }

    /// <summary>
    ///     Closes the currently shown media display, but ONLY if it was triggered by exactly this
    ///     timer/alarm/interval - e.g. when a timer whose expiry just showed an
    ///     image/video is reset. A medium shown via ad-hoc sending or from
    ///     the library remains unaffected by this.
    /// </summary>
    private void StopMediaIfTriggeredBy(TriggerMediaConfig config)
    {
        if (ReferenceEquals(_activeTriggerMediaSource, config))
        {
            ClearMediaCommand.Execute(null);
            ResumeGalleryAfterEventMedia();
        }

        if (_triggerSoundMediaIds.Remove(config, out var mediaId)) StopSound(mediaId);
    }

    /// <summary>
    ///     After closing an event medium (timer/alarm/interval reset, see above):
    ///     automatically shows the most recently active gallery item again instead of leaving the
    ///     screen empty - event media only temporarily interrupt the gallery (taking precedence), see
    ///     design-decisions.md. A no-op if the item has since been removed from the gallery.
    /// </summary>
    private void ResumeGalleryAfterEventMedia()
    {
        if (_currentGalleryMediaId is null) return;

        var item = SentMediaItems.FirstOrDefault(i => i.MediaId == _currentGalleryMediaId);
        if (item is null) return;

        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        _ = _playerServer.PublishHighlightAsync(item.MediaId);
    }

    private void BeginVideoTracking(MediaHeaderDto header, string localPath, bool pauseClockUntilEnd)
    {
        CancelPendingVideoTracking();

        // VIDEO ONLY: sounds are handled completely independently of the image/video "current
        // medium" slot (see PlayLocalSoundIfNeeded) - otherwise a sound ending here would
        // incorrectly trigger ClearMediaCommand and close a currently shown image/video.
        if (header.Kind != MediaHeaderDto.MediaKindVideo || header.Loop) return;

        _pendingVideoMediaId = header.MediaId;
        _pendingVideoPauseClock = pauseClockUntilEnd;

        if (pauseClockUntilEnd && _clock.IsRunning)
        {
            _clock.Pause();
            IsClockRunning = _clock.IsRunning;
            _ = _playerServer.PublishClockStoppedAsync();
        }

        var cts = new CancellationTokenSource();
        _pendingVideoFallbackCts = cts;
        _ = RunFallbackTimeoutAsync(header.MediaId, localPath, cts.Token);
    }

    private void CancelPendingVideoTracking()
    {
        _pendingVideoFallbackCts?.Cancel();
        _pendingVideoFallbackCts?.Dispose();
        _pendingVideoFallbackCts = null;
        _pendingVideoMediaId = null;
    }

    /// <summary>
    ///     Safety net: if no client sends a real playbackEnded report, don't stay paused/open
    ///     forever.
    /// </summary>
    private async Task RunFallbackTimeoutAsync(string mediaId, string localPath, CancellationToken token)
    {
        var duration = await TryGetMediaDurationAsync(localPath) ?? TimeSpan.FromMinutes(10);
        // Buffer beyond the estimated duration so that in the normal case the real
        // client report wins first (it also accounts for network/buffering time).
        var fallback = duration + TimeSpan.FromSeconds(15);

        try
        {
            await Task.Delay(fallback, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ResolvePendingVideo(mediaId);
    }

    private void UpdateConnectedClients(IReadOnlyList<ConnectedClientInfo> clients)
    {
        ConnectedClientItems.Clear();
        foreach (var info in clients)
            ConnectedClientItems.Add(new ConnectedClientItemViewModel(info, DisconnectClientRequested));
        OnPropertyChanged(nameof(HasNoConnectedClients));
    }

    private void DisconnectClientRequested(ConnectedClientItemViewModel item)
    {
        _playerServer.DisconnectClient(item.RemoteEndpoint);
    }

    private void ResolvePendingVideo(string mediaId)
    {
        if (_pendingVideoMediaId != mediaId) return;

        var pauseClock = _pendingVideoPauseClock;
        CancelPendingVideoTracking();

        if (pauseClock && !_clock.IsRunning)
        {
            _clock.Start();
            IsClockRunning = _clock.IsRunning;
            _ = _playerServer.PublishClockStartedAsync();
        }

        ClearMediaCommand.Execute(null);
    }

    private static async Task<TimeSpan?> TryGetMediaDurationAsync(string path)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return null;

        try
        {
            using var media = new Media(libVlc, path);
            var parsed = await media.Parse();
            return parsed == MediaParsedStatus.Done && media.Duration > 0
                ? TimeSpan.FromMilliseconds(media.Duration)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // TimelineItems wraps every timer/alarm/interval 1:1 (see AddTimelineItem) and
    // already cascades RefreshBlinkState() to the respective wrapped VM - a separate
    // iteration over Timers/Alarms/IntervalEvents would update every object twice.
    private void RefreshBlinkStates()
    {
        foreach (var item in TimelineItems) item.RefreshBlinkState();
    }

    private static string FormatGameTime(DateTime time)
    {
        return time.ToString("dddd, dd.MM.yyyy — HH:mm:ss", LocalizationService.Culture);
    }

    // ==================== Save & load ====================

    /// <summary>
    ///     Called once from the constructor. Loads ThemeSettingsDto.LastSaveFilePath instead of
    ///     starting with a blank state, if the user opted into it - silently does nothing if the
    ///     toggle is off, no path is known yet (never saved/loaded manually), or the file has
    ///     since been moved/deleted, since a missing auto-load source shouldn't block startup.
    /// </summary>
    private void TryAutoLoadOnStartup(ThemeSettingsService.ThemeSettingsDto settings)
    {
        if (!settings.AutoLoadOnStartupEnabled) return;
        if (string.IsNullOrWhiteSpace(settings.LastSaveFilePath) || !File.Exists(settings.LastSaveFilePath)) return;

        try
        {
            ImportStateFromZipOrJson(File.ReadAllBytes(settings.LastSaveFilePath));
            Log.Information("Auto-loaded game state from {Path}", settings.LastSaveFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Auto-load failed to read {Path}", settings.LastSaveFilePath);
        }
    }

    /// <summary>
    ///     Called from MainWindow.OnClosing. Writes to ThemeSettingsDto.LastSaveFilePath if the
    ///     user opted into it and that path is known (i.e. at least one manual save/load already
    ///     happened) - silently does nothing otherwise, since there's no dialog to fall back to
    ///     while the app is closing.
    /// </summary>
    public void TryAutoSaveOnClose()
    {
        var settings = ThemeSettingsService.LoadSettings();
        if (!settings.AutoSaveOnCloseEnabled || string.IsNullOrWhiteSpace(settings.LastSaveFilePath)) return;

        try
        {
            File.WriteAllBytes(settings.LastSaveFilePath, ExportStateToZipBytes());
            Log.Information("Auto-saved game state to {Path}", settings.LastSaveFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Auto-save failed to write {Path}", settings.LastSaveFilePath);
        }
    }

    /// <summary>Creates the complete state as JSON text (for the file export).</summary>
    public string ExportStateToJson()
    {
        var dto = new AppStateDto
        {
            CurrentGameTime = _clock.CurrentTime,
            SpeedMultiplier = SpeedMultiplier,
            IsClockRunning = IsClockRunning,
            Timers = Timers.Select(t => t.ToDto()).ToList(),
            Alarms = Alarms.Select(a => a.ToDto()).ToList(),
            IntervalEvents = IntervalEvents.Select(i => i.ToDto()).ToList(),
            JumpMarkers = JumpMarkers.Select(m => m.ToDto()).ToList(),
            CalendarEntries = CalendarEntries
                .Select(item => item.TryBuildDefinition(out var definition) ? definition : null)
                .Where(definition => definition is not null)
                .Cast<CalendarEntryDefinition>()
                .ToList(),
            Theme = GetCurrentThemeWireValue(),
            Sound = SoundService.Pling
        };
        Log.Information("Game state exported: {TimerCount} timers, {AlarmCount} alarms, {IntervalCount} intervals",
            dto.Timers.Count, dto.Alarms.Count, dto.IntervalEvents.Count);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    ///     Creates the complete state as a .rtt-save zip container (state.json inside a
    ///     zip archive, rather than a bare JSON file) - see SaveFileArchive.Wrap and
    ///     ImportStateFromZipOrJson for the corresponding read side/upgrade path.
    /// </summary>
    public byte[] ExportStateToZipBytes()
    {
        return SaveFileArchive.Wrap(ExportStateToJson());
    }

    /// <summary>Restores state from a .rtt-save zip container, or an old plain-JSON save
    ///     (see SaveFileArchive.Unwrap for the upgrade-path fallback).</summary>
    public void ImportStateFromZipOrJson(byte[] data)
    {
        ImportStateFromJson(SaveFileArchive.Unwrap(data));
    }

    /// <summary>Restores the complete state from previously exported JSON text.</summary>
    public void ImportStateFromJson(string json)
    {
        AppStateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppStateDto>(json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Game state could not be parsed");
            ClockErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return;
        }

        if (dto is null)
        {
            Log.Warning("Game state import rejected: file does not contain a valid game state");
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidGameState");
            return;
        }

        Log.Information("Loading game state: {TimerCount} timers, {AlarmCount} alarms, {IntervalCount} intervals",
            dto.Timers.Count, dto.Alarms.Count, dto.IntervalEvents.Count);

        _clock.Pause();

        Timers.Clear();
        Alarms.Clear();
        IntervalEvents.Clear();
        TimelineItems.Clear();
        PlayerTimelineItems.Clear();
        JumpMarkers.Clear();
        CalendarEntries.Clear();
        CalendarEntriesForSelectedDate.Clear();
        PlayerCalendarEntries.Clear();
        PlayerCalendarDays.Clear();

        _clock.SetTime(dto.CurrentGameTime);
        // Undoing jumps from a previous session/file would no longer make sense after loading
        // (it refers to a different timer/alarm state) - undo/redo
        // history is deliberately purely session-local, see the _jumpUndoStack comment.
        _jumpUndoStack.Clear();
        _jumpRedoStack.Clear();
        CanUndoJump = false;
        CanRedoJump = false;
        CurrentGameTimeText = FormatGameTime(dto.CurrentGameTime);
        ManualDateTimeText = dto.CurrentGameTime.ToString("yyyy-MM-dd HH:mm:ss");
        SpeedMultiplier = dto.SpeedMultiplier <= 0 ? 1.0 : Math.Round(dto.SpeedMultiplier, 1);
        SpeedMultiplierInput = (decimal)SpeedMultiplier;

        foreach (var t in dto.Timers)
        {
            var timer = TimerItemViewModel.FromDto(t, RemoveTimer);
            timer.StateChanged += () => PublishItemState(timer);
            timer.MediaResetRequested += () => StopMediaIfTriggeredBy(timer.TriggerMedia);
            timer.MediaResetRequested += () => StopInfiniteSoundLoop(timer.Id);
            Timers.Add(timer);
            AddTimelineItem(timer);
        }

        foreach (var a in dto.Alarms)
        {
            var alarm = AlarmItemViewModel.FromDto(a, dto.CurrentGameTime, RemoveAlarm);
            alarm.StateChanged += () => PublishItemState(alarm);
            alarm.ResetRequested += () => StopInfiniteSoundLoop(alarm.Id);
            Alarms.Add(alarm);
            AddTimelineItem(alarm);
        }

        foreach (var i in dto.IntervalEvents)
        {
            var intervalEvent = IntervalEventItemViewModel.FromDto(i, RemoveIntervalEvent);
            intervalEvent.StateChanged += () => PublishItemState(intervalEvent);
            intervalEvent.ResetRequested += () => StopInfiniteSoundLoop(intervalEvent.Id);
            IntervalEvents.Add(intervalEvent);
            AddTimelineItem(intervalEvent);
        }

        foreach (var m in dto.JumpMarkers)
            JumpMarkers.Add(JumpMarkerItemViewModel.FromDto(m, () => _clock.CurrentTime, JumpBy, RemoveMarker));

        foreach (var calendarEntry in dto.CalendarEntries)
            CalendarEntries.Add(CalendarEntryViewModel.FromDefinition(calendarEntry, OnCalendarEntryChanged,
                RemoveCalendarEntry));

        CalendarMonth = new DateTime(dto.CurrentGameTime.Year, dto.CurrentGameTime.Month, 1);
        CalendarSelectedDate = dto.CurrentGameTime.Date;
        RefreshCalendarViews();

        if (dto.IsClockRunning) _clock.Start();
        IsClockRunning = _clock.IsRunning;
        ClockErrorMessage = null;

        if (ThemeDefinitionLoader.Resolve(dto.Theme) is { } resolvedTheme &&
            _themesByDisplayName.FirstOrDefault(kv => kv.Value.Definition.Id == resolvedTheme.Definition.Id)
                is { Key: not null } match)
        {
            SelectedThemeOption = match.Key;
            ThemeSettingsService.SaveLastThemeId(resolvedTheme.Definition.Id);
        }

        // Loading is a complete replacement of the state (incl. deleting old items) - for this
        // a single full resync to all clients is simpler and more robust than sending a
        // separate delta for every individual change.
        _ = _playerServer.PublishFullResyncAsync(BuildSessionSnapshot());
    }

    public string ExportCalendarToJson()
    {
        var dto = new CalendarExportDto
        {
            Entries = CalendarEntries
                .Select(item => item.TryBuildDefinition(out var definition) ? definition : null)
                .Where(definition => definition is not null)
                .Cast<CalendarEntryDefinition>()
                .ToList()
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public void ImportCalendarFromJson(string json)
    {
        CalendarExportDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CalendarExportDto>(json);
        }
        catch (Exception ex)
        {
            ClockErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.CalendarCouldNotBeRead"), ex.Message);
            return;
        }

        if (dto is null)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidCalendar");
            return;
        }

        CalendarEntries.Clear();
        foreach (var entry in dto.Entries)
            CalendarEntries.Add(
                CalendarEntryViewModel.FromDefinition(entry, OnCalendarEntryChanged, RemoveCalendarEntry));

        CalendarMonth = new DateTime(_clock.CurrentTime.Year, _clock.CurrentTime.Month, 1);
        CalendarSelectedDate = _clock.CurrentTime.Date;
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    /// <summary>Full state for newly connecting clients or an explicit full resync (load).</summary>
    private SessionSnapshotParams BuildSessionSnapshot()
    {
        return new SessionSnapshotParams
        {
            CurrentGameTime = _clock.CurrentTime,
            SpeedMultiplier = SpeedMultiplier,
            IsClockRunning = IsClockRunning,
            PlayerHeaderTitle = PlayerHeaderTitle,
            PlayerHeaderSubtitle = PlayerHeaderSubtitle,
            Theme = GetCurrentThemeWireValue(),
            FogColorHex = FogColorHex,
            FogOpacityPercent = FogOpacityPercent,
            FogBlurRadius = FogBlurRadius,
            FogBlurEnabled = FogBlurEnabled,
            CalendarEntries = CalendarEntries
                .Select(item => item.TryBuildDefinition(out var definition) ? definition : null)
                .Where(definition => definition is not null && definition.IsPlayerVisible)
                .Cast<CalendarEntryDefinition>()
                .ToList(),
            Items = PlayerTimelineItems
                .Take(200)
                .Select(item => item.ToRpcSnapshot())
                .ToList()
        };
    }

    /// <summary>
    ///     Accompanies the periodic heartbeat: corrects drift of the locally derived
    ///     client clock and heals a clock.* event possibly dropped due to skipIfBusy.
    /// </summary>
    private ClockHeartbeatParams BuildClockHeartbeat()
    {
        return new ClockHeartbeatParams
        {
            CurrentGameTime = _clock.CurrentTime,
            SpeedMultiplier = SpeedMultiplier,
            IsClockRunning = IsClockRunning
        };
    }

    private sealed class LibraryManifestEntryDto
    {
        public string Name { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;

        public string? Icon { get; init; } = string.Empty;
        public string? Kind { get; init; }
        public bool Loop { get; init; }
        public int? Volume { get; init; }
        public int? RepeatCount { get; init; }
        public long TrimStartMs { get; init; }
        public long TrimEndMs { get; init; }
    }

    private sealed class LibraryManifestDto
    {
        public string LibraryType { get; init; } = string.Empty;
        public List<LibraryManifestEntryDto> Items { get; init; } = [];
    }

    /// <summary>
    ///     Manifest for a single exported map (<c>.rtt-map</c> zip) - a map is exported/imported
    ///     one at a time (unlike the whole-folder Media/Sound library export/import), since it's
    ///     a single self-contained unit a GM shares/backs up, not a batch of many.
    /// </summary>
    private sealed class MapFloorManifestEntryDto
    {
        public string Name { get; init; } = string.Empty;
        public string ImageFileName { get; init; } = string.Empty;
        public string FogFileName { get; init; } = string.Empty;
        public int CellSizePx { get; init; }
        public int GridWidth { get; init; }
        public int GridHeight { get; init; }
    }

    private sealed class MapManifestDto
    {
        public string Name { get; init; } = string.Empty;
        public List<MapFloorManifestEntryDto> Floors { get; init; } = [];
    }

    /// <summary>
    ///     Manifest for the "maps" section of a full-session export - unlike MapManifestDto (one
    ///     map per .rtt-map file), a session bundle needs all maps in the library at once.
    /// </summary>
    private sealed class MapLibraryManifestEntryDto
    {
        public string Name { get; init; } = string.Empty;
        public List<MapFloorManifestEntryDto> Floors { get; init; } = [];
    }

    private sealed class MapLibraryManifestDto
    {
        public List<MapLibraryManifestEntryDto> Maps { get; init; } = [];
    }

    private sealed class CalendarExportDto
    {
        public int Version { get; init; } = 1;
        public List<CalendarEntryDefinition> Entries { get; init; } = [];
    }
}