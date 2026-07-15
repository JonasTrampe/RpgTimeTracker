using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
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

    /// <summary>
    ///     Header+bytes (+ start time/duration once known) of every currently active sound,
    ///     keyed by MediaId - kept only for as long as the sound is active (added in
    ///     AddActiveSound, removed in RemoveActiveSound), so a client whose Sound routing is
    ///     re-enabled can be resent everything currently playing (see ClientSoundRoutingEnabled)
    ///     instead of staying silent until the next brand-new sound happens to play. StartedAtUtc/
    ///     DurationMs feed the estimated mid-playback seek for sounds longer than
    ///     SoundSeekThresholdMs (see ResendActiveSoundsToClient) - DurationMs is null until the
    ///     async parse in UpdateActiveSoundDurationAsync resolves.
    /// </summary>
    private readonly Dictionary<string, (MediaHeaderDto Header, byte[] FileBytes, DateTime StartedAtUtc, long?
        DurationMs)> _activeSoundData = new();

    private readonly DispatcherTimer _blinkTimer;

    /// <summary>
    ///     All available calendars (bundled PredefinedCalendars + GM's own, see
    ///     CalendarDefinitionLoader), keyed by their Name (a calendar has no separate id, unlike a
    ///     theme) - used both for the Settings picker and to persist/apply the GM's choice.
    /// </summary>
    private readonly Dictionary<string, CalendarDefinitionLoader.LoadedCalendar> _calendarsByName = new();

    private readonly GameClockService _clock;

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

    /// <summary>Jumps undone via UndoJump, restorable via RedoJump (see _jumpUndoStack).</summary>
    private readonly Stack<TimeSpan> _jumpRedoStack = new();

    /// <summary>
    ///     Most recently applied time jumps (LIFO) - allows undoing several jumps in a row, purely
    ///     local/session-based, not persisted. A new jump (JumpBy) clears the redo stack,
    ///     since a new jump invalidates the "future" of the previous redo history - classic
    ///     undo/redo behavior as in text/graphics editors.
    /// </summary>
    private readonly Stack<TimeSpan> _jumpUndoStack = new();

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

    private readonly SessionService _sessionService = new();

    /// <summary>
    ///     All available themes (bundled SampleThemes + GM's own, see ThemeDefinitionLoader),
    ///     keyed by the display name shown in ThemeOptions or by the theme id (for
    ///     persistence/ambience/network sync). There is no longer a separate list of "built-in" themes -
    ///     all go through the same JSON loading mechanism.
    /// </summary>
    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesByDisplayName = new();

    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesById = new();

    /// <summary>
    ///     The active Theme's character-status vocabulary (see ThemeDefinitionDto.Statuses),
    ///     falling back to StatusDefinitionCatalog's built-in defaults - bound by the Characters
    ///     tab's per-Variant Status picker. Refreshed on RefreshActiveStatusOptions (theme change,
    ///     and once at startup).
    /// </summary>
    public ObservableCollection<StatusDefinitionDto> ActiveStatusOptions { get; } = [];

    private ThemeDefinitionDto? GetActiveThemeDefinition()
    {
        return _themesByDisplayName.TryGetValue(SelectedThemeOption, out var loaded) ? loaded.Definition : null;
    }

    private void RefreshActiveStatusOptions()
    {
        ActiveStatusOptions.Clear();
        foreach (var status in StatusDefinitionCatalog.GetActiveStatuses(GetActiveThemeDefinition()))
            ActiveStatusOptions.Add(status);
    }

    /// <summary>
    ///     Associates a triggering TriggerMediaConfig (event medium) with the MediaId of the
    ///     currently running sound, so that StopMediaIfTriggeredBy also ends it when the timer/alarm/
    ///     interval is reset - analogous to the image/video slot, but for the decoupled sound path.
    /// </summary>
    private readonly Dictionary<TriggerMediaConfig, string> _triggerSoundMediaIds = new();

    /// <summary>
    ///     Generalized "who references this item" lookup - see LibraryUsageRegistry. Finders/
    ///     clear-actions are registered once, in the constructor.
    /// </summary>
    private readonly LibraryUsageRegistry _usageRegistry = new();

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

    /// <summary>Loads ThemeSettingsDto.LastSaveFilePath on startup (see TryAutoLoadOnStartup).</summary>
    [ObservableProperty] private bool _autoLoadOnStartupEnabled;

    /// <summary>Writes to ThemeSettingsDto.LastSaveFilePath when the window closes (see TryAutoSaveOnClose).</summary>
    [ObservableProperty] private bool _autoSaveOnCloseEnabled;

    [ObservableProperty] private GameInstant _calendarMonth;
    [ObservableProperty] private GameInstant _calendarSelectedDate;
    private CancellationTokenSource? _calendarSyncCts;
    [ObservableProperty] private bool _canRedoJump;

    [ObservableProperty] private bool _canUndoJump;

    [ObservableProperty] private string? _clockErrorMessage;

    [ObservableProperty] private int _connectedClientCount;

    /// <summary>
    ///     Optional PIN a client must send when connecting (session.hello) - empty
    ///     means "no PIN required" (default, backward-compatible). Checked by
    ///     TcpPlayerServerService.PerformHandshakeAsync against the value sent by the client.
    /// </summary>
    [ObservableProperty] private string _connectionPin = string.Empty;

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

    /// <summary>
    ///     The currently playing track's header/localPath/start time - kept only while a
    ///     playlist is playing (set in PlayCurrentPlaylistTrackAsync, cleared in
    ///     StopPlaylistPlayback), so re-enabling PlayMusicLocally can resume the local preview
    ///     with an estimated seek instead of staying silent until the next track (see
    ///     OnPlayMusicLocallyChanged) - mirrors TcpPlayerServerService._currentMusicTrack, which
    ///     does the same thing for remote clients.
    /// </summary>
    private (MediaHeaderDto Header, string LocalPath, DateTime StartedAtUtc)? _currentMusicPlayback;

    [ObservableProperty] private bool _fogBlurEnabled = true;
    [ObservableProperty] private double _fogBlurRadius;

    /// <summary>
    ///     Player-side fog render style (see issue #22, MapDisplayViewModel) - one global GM
    ///     preference in the Settings tab, pushed to all clients live (map.renderStyleChanged)
    ///     and on connect (session.snapshot).
    /// </summary>
    [ObservableProperty] private string _fogColorHex = "#0C0C0C";

    [ObservableProperty] private int _fogOpacityPercent = 100;

    [ObservableProperty] private decimal _headsUpLeadMinutes;

    [ObservableProperty] private string? _headsUpMessage;

    private CancellationTokenSource? _headsUpMessageClearCts;

    /// <summary>GM-local heads-up warning before a timer/alarm/on-time entry triggers - never sent to players.</summary>
    [ObservableProperty] private bool _headsUpWarningEnabled;

    [ObservableProperty] private bool _isClockRunning;

    [ObservableProperty] private bool _isLocalPlayerFullscreen;

    [ObservableProperty] private bool _isNetworkServerRunning;

    [ObservableProperty] private bool _isPlayerWindowOpen;

    // Free-form time jump, e.g. "08:00:00" forward or "-1.00:00:00" back one day.
    [ObservableProperty] private string _jumpAmountText = "08:00:00";

    /// <summary>
    ///     The Host's own local playback of whatever the playlist sequencer currently has playing
    ///     - separate again from _testMusicPlayer (testing a library track) and _testSoundPlayer,
    ///     mirroring how CreateLocalMediaPlayer/PlayLocalSoundIfNeeded give the GM the same audio
    ///     players hear, gated on IsPlayerWindowOpen (see PlayLocalMusicIfNeeded).
    /// </summary>
    private MediaPlayer? _localMusicPlayer;

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

    [ObservableProperty]
    private string _networkServerStatus = LocalizationService.Get("MainWindowViewModel.Network.ServerOff");

    [ObservableProperty] private string _newAlarmDateTime;

    [ObservableProperty] private decimal _newAlarmRepeatHours;

    [ObservableProperty] private string _newAlarmRepeatInterval = string.Empty;

    [ObservableProperty] private decimal _newAlarmRepeatMinutes;

    [ObservableProperty] private decimal _newAlarmRepeatSeconds;

    /// <summary>See NewTimerTargetSceneId's doc comment - same purpose for a new Alarm.</summary>
    [ObservableProperty] private Guid? _newAlarmTargetSceneId;

    [ObservableProperty] private string _newIntervalActiveDuration = "00:01:00";

    [ObservableProperty] private decimal _newIntervalActiveHours;

    [ObservableProperty] private decimal _newIntervalActiveMinutes = 1;

    [ObservableProperty] private decimal _newIntervalActiveSeconds;

    [ObservableProperty] private string _newIntervalInterval = "00:10:00";

    [ObservableProperty] private decimal _newIntervalIntervalHours;

    [ObservableProperty] private decimal _newIntervalIntervalMinutes = 10;

    [ObservableProperty] private decimal _newIntervalIntervalSeconds;

    [ObservableProperty] private string _newIntervalMaxRepeats = string.Empty;

    /// <summary>See NewTimerTargetSceneId's doc comment - same purpose for a new IntervalEvent.</summary>
    [ObservableProperty] private Guid? _newIntervalTargetSceneId;

    [ObservableProperty]
    private string _newMarkerName = LocalizationService.Get("MainWindowViewModel.Defaults.NewMarkerName");

    [ObservableProperty] private string _newMarkerTime = "06:00";

    [ObservableProperty] private string _newTimerDuration = "00:10:00";

    [ObservableProperty] private decimal _newTimerDurationHours;

    [ObservableProperty] private decimal _newTimerDurationMinutes = 10;

    [ObservableProperty] private decimal _newTimerDurationSeconds;

    /// <summary>
    ///     Phase 4 of the Scenes/Tags/Calendars project: if set when this new Timer is
    ///     created, its TargetSceneId is seeded from here (see AddTimer) - reset to null once
    ///     consumed, same one-shot-picker convention as PendingSoundToAdd et al.
    /// </summary>
    [ObservableProperty] private Guid? _newTimerTargetSceneId;

    // ==================== Music-track-end tracking (mirrors video-end tracking above) ====================
    // Same "first of {client report, local-preview EndReached, duration-estimate fallback} wins,
    // idempotent" design as _pendingVideoMediaId/ResolvePendingVideo - see BeginMusicTracking.
    private CancellationTokenSource? _pendingMusicFallbackCts;
    private string? _pendingMusicMediaId;

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

    /// <summary>
    ///     Ordered playback sequence for CurrentPlaylist - indices into its Tracks list,
    ///     shuffled once per PlayPlaylist call when CurrentPlaylist.Shuffle is set (see
    ///     BuildPlaybackOrder). _playbackPosition indexes into THIS list, not directly into Tracks.
    /// </summary>
    private List<int> _playbackOrder = [];

    private int _playbackPosition;
    [ObservableProperty] private string _playerCalendarMonthLabel = string.Empty;
    [ObservableProperty] private string _playerCalendarSelectedDateLabel = string.Empty;

    [ObservableProperty] private string _playerHeaderSubtitle;

    [ObservableProperty] private string _playerHeaderTitle;

    /// <summary>
    ///     Whether the Host's own local preview plays Music/Sound - the "Host (local)" row
    ///     in the Music tab's routing list, parallel to the per-client MusicEnabled/SoundEnabled
    ///     flags on TcpPlayerServerService. Session-scoped (not persisted), default true.
    /// </summary>
    [ObservableProperty] private bool _playMusicLocally = true;

    [ObservableProperty] private bool _playSoundLocally = true;

    // ==================== Session log (GM-local only, only on explicit opt-in) ====================

    [ObservableProperty] private bool _recordSessionLog;

    /// <summary>Whether remote clients should currently be in fullscreen.</summary>
    [ObservableProperty] private bool _remoteFullscreen;

    [ObservableProperty] private CalendarEntryViewModel? _selectedCalendarEntry;

    [ObservableProperty] private string _selectedCalendarOption = string.Empty;

    /// <summary>UI language, independent from the PlayerClient's own setting (see ClientMainWindowViewModel).</summary>
    [ObservableProperty] private string _selectedLanguageOption = "English";

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

    [ObservableProperty] private bool _showPlayerCalendarView;

    /// <summary>
    ///     Seconds per image for automatic advancement across all clients (0 = manual).
    ///     Never applies to videos (see design-decisions.md).
    /// </summary>
    [ObservableProperty] private double _slideshowSecondsPerImage;

    private DispatcherTimer? _slideshowTimer;

    /// <summary>
    ///     Minimum sound duration (ms) worth estimating a mid-playback seek for when
    ///     resending it to a client whose Sound routing was just re-enabled - see
    ///     ResendActiveSoundsToClient. Persisted, GM-configurable (Settings tab).
    /// </summary>
    [ObservableProperty] private int _soundSeekThresholdMs = 10_000;

    [ObservableProperty] private double _speedMultiplier = 1.0;

    [ObservableProperty] private decimal _speedMultiplierInput = 1.0m;

    /// <summary>
    ///     Same purpose as _testSoundPlayer, kept separate so testing a music track never
    ///     stops/collides with a sound library test playing at the same time.
    /// </summary>
    private MediaPlayer? _testMusicPlayer;

    /// <summary>
    ///     Only one test runs at a time - holds the reference so the player is not
    ///     prematurely garbage-collected (no dictionary entry needed like for real sounds).
    /// </summary>
    private MediaPlayer? _testSoundPlayer;

    public MainWindowViewModel()
    {
        _usageRegistry.Register(FindTriggerMediaUsagesById);
        _usageRegistry.RegisterClearAction(ClearTriggerMediaReferencesById);
        _usageRegistry.Register(FindPlaylistUsagesById);
        _usageRegistry.RegisterClearAction(ClearPlaylistReferencesById);
        _usageRegistry.Register(FindNpcUsagesById);
        _usageRegistry.RegisterClearAction(ClearNpcReferencesById);
        _usageRegistry.Register(FindSceneUsagesById);
        _usageRegistry.RegisterClearAction(ClearSceneReferencesById);
        _usageRegistry.Register(FindPointOfInterestUsagesById);
        _usageRegistry.RegisterClearAction(ClearPointOfInterestReferencesById);
        _usageRegistry.Register(FindTagUsagesById);
        _usageRegistry.RegisterClearAction(ClearTagReferencesById);
        _usageRegistry.Register(FindTimerTagUsagesById);
        _usageRegistry.RegisterClearAction(ClearTimerTagReferencesById);
        InitializeTimelineTagFiltering();

        // Restore the GM's last chosen calendar before anything else touches CalendarService.Active,
        // since the "start" default below and every date display already depend on it.
        var initialCalendar = CalendarDefinitionLoader.Resolve(ThemeSettingsService.LoadLastCalendarName())
                              ?? CalendarDefinitionLoader.Resolve("Gregorian");
        if (initialCalendar is { } resolvedInitialCalendar) CalendarService.Active = resolvedInitialCalendar.Definition;

        // Reasonable fantasy start time, can be changed immediately - real wall-clock "now" has no
        // meaning under an arbitrary game-calendar epoch (see CalendarEntryViewModel's identical default).
        var start = CalendarService.Active.FromCalendarDate(1, 0, 1, 12, 0, 0);
        _clock = new GameClockService(start);
        _clock.Tick += OnClockTick;
        _playerServer = new TcpPlayerServerService(BuildSessionSnapshot, BuildClockHeartbeat, () => ConnectionPin);
        // Time is only sent over the network on real jumps (not on every tick) -
        // the client continues calculating time locally from the last jump using the known speed,
        // see RpgTimeTracker.Shared.Services.GameClockService on the client side.
        _clock.Jumped += newTime => _ = _playerServer.PublishClockTimeJumpedAsync(newTime);
        _playerServer.ClientCountChanged += count => Dispatcher.UIThread.Post(() => ConnectedClientCount = count);
        _playerServer.ClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateConnectedClients(clients));
        // Shared by video AND sound (the client reuses media.playbackEnded for both, see
        // ClientMainWindowViewModel.OnSoundEndReached) - ResolvePendingVideo only acts if this is
        // the currently-tracked video, but a sound that finished purely on remote clients (no
        // local Host preview to fire OnLocalSoundEnded, e.g. player window closed or Sound-locally
        // disabled) would otherwise never leave ActivePlayingSounds - RemoveActiveSound is a no-op
        // if mediaId isn't an active sound, so calling it unconditionally here is safe.
        _playerServer.ClientReportedPlaybackEnded += mediaId => Dispatcher.UIThread.Post(() =>
        {
            ResolvePendingVideo(mediaId);
            RemoveActiveSound(mediaId);
        });
        // Decisive for the playlist sequencer's "when does the current track end" tracking - see
        // BeginMusicTracking.
        _playerServer.ClientReportedMusicTrackEnded +=
            mediaId => Dispatcher.UIThread.Post(() => ResolvePendingMusicTrack(mediaId));
        // The server only knows a client's Sound routing was turned off, not what's currently
        // playing - only this ViewModel's ActivePlayingSounds does, so it owns "stop each active
        // sound on that one client" rather than leaving them running until they end on their own.
        _playerServer.ClientSoundRoutingDisabled +=
            remoteEndpoint => Dispatcher.UIThread.Post(() => StopAllSoundsForClient(remoteEndpoint));
        // Mirror image: re-enabling shouldn't leave the client silent until the next brand-new
        // sound happens to play - resend everything currently active to just that client.
        _playerServer.ClientSoundRoutingEnabled +=
            remoteEndpoint => Dispatcher.UIThread.Post(() => ResendActiveSoundsToClient(remoteEndpoint));

        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _blinkTimer.Tick += (_, _) => RefreshBlinkStates();
        _blinkTimer.Start();
        _headsUpLeadMinutes = 2;
        _manualDateTimeText = CalendarService.Active.FormatDateTimeText(start);
        var defaultAlarm = start.Add(TimeSpan.FromHours(1));
        _newAlarmDateTime = CalendarService.Active.FormatDateTimeText(defaultAlarm);
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
        _soundSeekThresholdMs = settings.SoundSeekThresholdMs;
        _serverName = string.IsNullOrWhiteSpace(settings.ServerName) ? "RpgTimeTracker" : settings.ServerName;
        _connectionPin = settings.ConnectionPin;
        _autoSaveOnCloseEnabled = settings.AutoSaveOnCloseEnabled;
        _autoLoadOnStartupEnabled = settings.AutoLoadOnStartupEnabled;
        _fogColorHex = string.IsNullOrWhiteSpace(settings.FogColorHex) ? "#0C0C0C" : settings.FogColorHex;
        _fogOpacityPercent = settings.FogOpacityPercent;
        _fogBlurRadius = settings.FogBlurRadius;
        _fogBlurEnabled = settings.FogBlurEnabled;
        MapDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(_fogColorHex, _fogOpacityPercent),
            _fogBlurRadius,
            _fogBlurEnabled);

        foreach (var entry in settings.MediaLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            if (!Enum.TryParse<MediaKind>(entry.Kind, out var kind) || kind == MediaKind.Audio) continue;

            MediaLibrary.Add(new MediaLibraryItemViewModel(
                entry.Id, entry.Name, entry.Path, kind, entry.MimeType, entry.Loop,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged, entry.TagIds));
        }

        foreach (var entry in settings.SoundLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            SoundLibrary.Add(CreateSoundLibraryItem(entry.Id, entry.Name, entry.Icon, entry.Path, entry.MimeType,
                entry.Loop,
                entry.Volume, entry.RepeatCount, entry.TrimStartMs / 1000.0, entry.TrimEndMs / 1000.0, entry.TagIds));
        }

        foreach (var musicEntry in settings.MusicLibrary)
        {
            if (!File.Exists(musicEntry.Path)) continue;
            MusicLibrary.Add(CreateMusicLibraryItem(musicEntry.Id, musicEntry.Name, musicEntry.Icon,
                musicEntry.Path, musicEntry.MimeType, musicEntry.Volume, musicEntry.TagIds));
        }

        foreach (var playlistEntry in settings.Playlists)
        {
            var playlist = new PlaylistViewModel(playlistEntry.Id, playlistEntry.Name,
                playlistEntry.LoopPlaylist, playlistEntry.Shuffle, RemovePlaylist, _ => SavePlaylistsSettings());
            foreach (var trackId in playlistEntry.TrackIds)
            {
                // A track deleted from the Music Library after being added to this playlist is
                // silently dropped here rather than kept as a dangling reference - the next save
                // then persists the playlist without it (see RemoveMusicLibraryItem, which does
                // the same cleanup immediately for playlists already loaded in this session).
                var track = MusicLibrary.FirstOrDefault(m => m.Id == trackId);
                if (track is not null) playlist.LoadTrack(track);
            }

            Playlists.Add(playlist);
        }

        foreach (var mapEntry in settings.MapLibrary)
        {
            var map = new MapItemViewModel(mapEntry.Id, mapEntry.Name, mapEntry.DefaultCellSizePx, RemoveMap,
                OnMapLibraryItemChanged, mapEntry.FogColorHex, mapEntry.FogOpacityPercent,
                mapEntry.FogBlurRadius, mapEntry.FogBlurEnabled, mapEntry.FormatVersion, tagIds: mapEntry.TagIds);
            foreach (var floorEntry in mapEntry.Floors.OrderBy(f => f.Order))
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

        foreach (var npcEntry in settings.NpcLibrary)
        {
            var npc = FromNpcLibraryEntryDto(npcEntry, LibraryScope.Shared);
            if (npc is not null) NpcLibrary.Add(npc);
        }

        foreach (var sceneEntry in settings.SceneLibrary)
            SceneLibrary.Add(FromSceneLibraryEntryDto(sceneEntry, LibraryScope.Shared));

        foreach (var poiEntry in settings.PointOfInterestLibrary)
            PointOfInterestLibrary.Add(FromPointOfInterestLibraryEntryDto(poiEntry, LibraryScope.Shared));

        LoadTags(settings.Tags);
        LoadTimerTags(settings.TimerTags);

        // One-time migration of old data so that nothing is lost when switching to the separate
        // sound library: (a) sounds from the old "+ add sound" settings function,
        // (b) audio entries that had ended up in the image/video library before the split.
        var needsMigrationSave = false;
        if (!settings.LegacyCustomSoundsMigrated)
        {
            foreach (var legacy in settings.CustomSounds)
            {
                if (!File.Exists(legacy.Path) || SoundLibrary.Any(s => s.LocalPath == legacy.Path)) continue;
                SoundLibrary.Add(CreateSoundLibraryItem(Guid.NewGuid(), legacy.Name, VisualItemHelper.IconTimer,
                    legacy.Path, "audio/*",
                    false, 100));
                needsMigrationSave = true;
            }

            settings.LegacyCustomSoundsMigrated = true;
            needsMigrationSave = true;
        }

        foreach (var legacyAudio in settings.MediaLibrary.Where(e => e.Kind == nameof(MediaKind.Audio)))
        {
            if (!File.Exists(legacyAudio.Path) || SoundLibrary.Any(s => s.LocalPath == legacyAudio.Path)) continue;
            SoundLibrary.Add(CreateSoundLibraryItem(Guid.NewGuid(), legacyAudio.Name, VisualItemHelper.IconTimer,
                legacyAudio.Path,
                legacyAudio.MimeType, legacyAudio.Loop, 100));
            needsMigrationSave = true;
        }

        SyncSoundServiceLibrary();

        if (needsMigrationSave)
        {
            settings.SoundLibrary = SoundLibrary
                .Select(s => new SoundLibraryEntryDto
                {
                    Id = s.Id, Name = s.Name, Icon = s.Icon, Path = s.LocalPath, MimeType = s.MimeType, Loop = s.Loop,
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
                           LocalizationService.TryGet($"SampleTheme.{loaded.Definition.Id}.PickerLabel",
                               out var localized)
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

        RefreshActiveStatusOptions();

        _selectedCalendarOption = PopulateCalendarOptions();

        _ambienceAutomationEnabled = settings.AmbienceAutomationEnabled;
        _selectedLanguageOption = LanguageDisplayName(settings.Language);

        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerDawn"), new TimeSpan(6, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerNoon"), new TimeSpan(12, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerEvening"), new TimeSpan(18, 0, 0));
        AddDefaultMarker(LocalizationService.Get("MainWindowViewModel.Defaults.MarkerMidnight"), TimeSpan.Zero);

        _calendarMonth = CalendarMonthStart(start);
        _calendarSelectedDate = CalendarService.Active.DayStart(start);
        RefreshCalendarViews();

        LocalizationService.LanguageChanged += OnLanguageChanged;

        TryAutoLoadOnStartup(settings);
    }

    /// <summary>
    ///     Whether a Session (a folder-scoped campaign, see SessionService) is currently
    ///     open. With none open, every library item is Shared and the app behaves exactly as it
    ///     did before this concept existed.
    /// </summary>
    public bool IsSessionOpen => _sessionService.IsSessionOpen;

    public string? CurrentSessionPath => _sessionService.CurrentSessionPath;

    /// <summary>
    ///     Just the folder name, for a compact status display - the full path is available
    ///     via CurrentSessionPath (e.g. for a tooltip).
    /// </summary>
    public string CurrentSessionName => CurrentSessionPath is null
        ? string.Empty
        : Path.GetFileName(CurrentSessionPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    ///     Set by the view (see MainWindow.axaml.cs) to show a Shared/"this session only" chooser
    ///     - analogous to ConfirmTriggerMediaDeleteAsync below. Only ever consulted when a session
    ///     is actually open (see ResolveScopeForNewItemAsync); with no session open, or no handler
    ///     assigned (e.g. during testing), every new item is Shared, matching pre-Session behavior.
    /// </summary>
    public Func<Task<LibraryScope>>? ChooseLibraryScopeForNewItemAsync { get; set; }

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

    /// <summary>
    ///     Display names for the language ComboBox; native names on purpose, not translated by LocalizationService
    ///     itself.
    /// </summary>
    public ObservableCollection<string> LanguageOptions { get; } = ["English", "Deutsch"];

    // Display names of the calendars for the ComboBox; populated in the constructor from
    // CalendarDefinitionLoader.LoadAll() (see _calendarsByName).
    public ObservableCollection<string> CalendarOptions { get; } = [];

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

    partial void OnPlayMusicLocallyChanged(bool value)
    {
        // Mirrors SetClientMusicEnabled's targeted stop/resume, just for the Host's own preview:
        // turning this off must stop whatever is already playing locally (not just gate future
        // tracks), and turning it back on must resume the current track with an estimated seek
        // instead of staying silent until the next one - remote clients/other windows unaffected.
        if (!value)
        {
            _localMusicPlayer?.Stop();
            _localMusicPlayer?.Dispose();
            _localMusicPlayer = null;
            return;
        }

        if (_currentMusicPlayback is not { } playback) return;

        var elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - playback.StartedAtUtc).TotalMilliseconds);
        PlayLocalMusicIfNeeded(playback.Header, playback.LocalPath, elapsedMs);
    }

    partial void OnPlaySoundLocallyChanged(bool value)
    {
        if (!value) StopLocalSoundPreviewOnly();
    }

    partial void OnSoundSeekThresholdMsChanged(int value)
    {
        SaveUiSettings();
    }

    /// <summary>
    ///     Registered into _usageRegistry for both Media and Sound library items - trigger
    ///     media configs (Timer/Alarm/IntervalEvent/CalendarEntry) reference a file by Path, not
    ///     by Id, so this resolves the Id back to whichever library currently holds it and defers
    ///     to the existing path-based scan.
    /// </summary>
    private IEnumerable<string> FindTriggerMediaUsagesById(Guid id)
    {
        var localPath = MediaLibrary.FirstOrDefault(i => i.Id == id)?.LocalPath
                        ?? SoundLibrary.FirstOrDefault(i => i.Id == id)?.LocalPath;
        return localPath is null ? [] : FindTriggerMediaUsageLabels(localPath);
    }

    private void ClearTriggerMediaReferencesById(Guid id)
    {
        var localPath = MediaLibrary.FirstOrDefault(i => i.Id == id)?.LocalPath
                        ?? SoundLibrary.FirstOrDefault(i => i.Id == id)?.LocalPath;
        if (localPath is not null) ClearTriggerMediaReferences(localPath);
    }

    /// <summary>
    ///     Registered into _usageRegistry for Music library items - Playlists already
    ///     reference tracks by stable Id (see MusicLibraryEntryDto.TrackIds).
    /// </summary>
    private IEnumerable<string> FindPlaylistUsagesById(Guid id)
    {
        var track = MusicLibrary.FirstOrDefault(m => m.Id == id);
        if (track is null) yield break;

        foreach (var playlist in Playlists)
            if (playlist.Tracks.Any(t => t.Track == track))
                yield return string.Format(LocalizationService.Get("MainWindowViewModel.Labels.PlaylistUsage"),
                    playlist.Name);
    }

    private void ClearPlaylistReferencesById(Guid id)
    {
        var track = MusicLibrary.FirstOrDefault(m => m.Id == id);
        if (track is null) return;

        foreach (var playlist in Playlists) playlist.RemoveTracksReferencing(track);
    }

    /// <summary>Creates a brand-new session folder and opens it (see SessionService.CreateSession).</summary>
    public void CreateSession(string folderPath)
    {
        _sessionService.CreateSession(folderPath);
        NotifySessionChanged();
    }

    /// <summary>
    ///     Opens an existing session folder, merging its session-local library entries into
    ///     the existing MediaLibrary/SoundLibrary/MusicLibrary/MapLibrary collections (Shared
    ///     items, already loaded from ThemeSettingsService, are untouched).
    /// </summary>
    public void OpenSession(string folderPath)
    {
        _sessionService.OpenSession(folderPath);
        AddSessionLibraryToCollections(_sessionService.LoadLibrary());
        NotifySessionChanged();
    }

    /// <summary>
    ///     Closes the currently open session - removes every SessionLocal item from the
    ///     displayed collections (the files themselves are untouched on disk) so the app reverts
    ///     to showing only the Shared Library, exactly as with no session ever opened.
    /// </summary>
    public void CloseSession()
    {
        RemoveSessionLocalItemsFromCollections();
        _sessionService.CloseSession();
        NotifySessionChanged();
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(IsSessionOpen));
        OnPropertyChanged(nameof(CurrentSessionPath));
        OnPropertyChanged(nameof(CurrentSessionName));
    }

    /// <summary>
    ///     Called by every "add new library item" flow (Media/Sound/Music/Map) right before
    ///     deciding which physical directory to copy the new file into.
    /// </summary>
    private async Task<LibraryScope> ResolveScopeForNewItemAsync()
    {
        if (!_sessionService.IsSessionOpen || ChooseLibraryScopeForNewItemAsync is null)
            return LibraryScope.Shared;

        return await ChooseLibraryScopeForNewItemAsync();
    }

    private void AddSessionLibraryToCollections(SessionLibraryDto library)
    {
        foreach (var entry in library.MediaLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            if (!Enum.TryParse<MediaKind>(entry.Kind, out var kind) || kind == MediaKind.Audio) continue;

            MediaLibrary.Add(new MediaLibraryItemViewModel(
                entry.Id, entry.Name, entry.Path, kind, entry.MimeType, entry.Loop,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged, entry.TagIds)
            {
                Scope = LibraryScope.SessionLocal
            });
        }

        foreach (var entry in library.SoundLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            var item = CreateSoundLibraryItem(entry.Id, entry.Name, entry.Icon, entry.Path, entry.MimeType,
                entry.Loop, entry.Volume, entry.RepeatCount, entry.TrimStartMs / 1000.0, entry.TrimEndMs / 1000.0,
                entry.TagIds);
            item.Scope = LibraryScope.SessionLocal;
            SoundLibrary.Add(item);
        }

        foreach (var entry in library.MusicLibrary)
        {
            if (!File.Exists(entry.Path)) continue;
            var item = CreateMusicLibraryItem(entry.Id, entry.Name, entry.Icon, entry.Path, entry.MimeType,
                entry.Volume, entry.TagIds);
            item.Scope = LibraryScope.SessionLocal;
            MusicLibrary.Add(item);
        }

        foreach (var mapEntry in library.MapLibrary)
        {
            var map = new MapItemViewModel(mapEntry.Id, mapEntry.Name, mapEntry.DefaultCellSizePx, RemoveMap,
                OnMapLibraryItemChanged, mapEntry.FogColorHex, mapEntry.FogOpacityPercent,
                mapEntry.FogBlurRadius, mapEntry.FogBlurEnabled, mapEntry.FormatVersion, LibraryScope.SessionLocal,
                mapEntry.TagIds);
            foreach (var floorEntry in mapEntry.Floors.OrderBy(f => f.Order))
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

        foreach (var npcEntry in library.NpcLibrary)
        {
            var npc = FromNpcLibraryEntryDto(npcEntry, LibraryScope.SessionLocal);
            if (npc is not null) NpcLibrary.Add(npc);
        }

        foreach (var sceneEntry in library.SceneLibrary)
            SceneLibrary.Add(FromSceneLibraryEntryDto(sceneEntry, LibraryScope.SessionLocal));

        foreach (var poiEntry in library.PointOfInterestLibrary)
            PointOfInterestLibrary.Add(FromPointOfInterestLibraryEntryDto(poiEntry, LibraryScope.SessionLocal));

        OnPropertyChanged(nameof(HasNoMediaLibraryItems));
        OnPropertyChanged(nameof(HasNoSoundLibraryItems));
        OnPropertyChanged(nameof(HasNoMaps));
        OnPropertyChanged(nameof(HasNoNpcLibraryItems));
        OnPropertyChanged(nameof(HasNoScenes));
        OnPropertyChanged(nameof(HasNoPointsOfInterest));
    }

    private void RemoveSessionLocalItemsFromCollections()
    {
        foreach (var item in MediaLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
            MediaLibrary.Remove(item);
        foreach (var item in SoundLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
            SoundLibrary.Remove(item);
        foreach (var item in MusicLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
            MusicLibrary.Remove(item);
        foreach (var item in MapLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
            MapLibrary.Remove(item);
        foreach (var item in NpcLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
        {
            NpcLibrary.Remove(item);
            if (SelectedNpc == item) SelectedNpc = null;
        }

        foreach (var item in SceneLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
        {
            SceneLibrary.Remove(item);
            if (SelectedScene == item) SelectedScene = null;
        }

        foreach (var item in PointOfInterestLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList())
        {
            PointOfInterestLibrary.Remove(item);
            if (SelectedPointOfInterest == item) SelectedPointOfInterest = null;
        }

        OnPropertyChanged(nameof(HasNoMediaLibraryItems));
        OnPropertyChanged(nameof(HasNoSoundLibraryItems));
        OnPropertyChanged(nameof(HasNoNpcLibraryItems));
        OnPropertyChanged(nameof(HasNoMaps));
        OnPropertyChanged(nameof(HasNoScenes));
        OnPropertyChanged(nameof(HasNoPointsOfInterest));
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
        foreach (var music in MusicLibrary) music.RefreshLocalizedText();
        foreach (var sent in SentMediaItems) sent.RefreshLocalizedText();
        foreach (var client in ConnectedClientItems) client.RefreshLocalizedText();
    }
}