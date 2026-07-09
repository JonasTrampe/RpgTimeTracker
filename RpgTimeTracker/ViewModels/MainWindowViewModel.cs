using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
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
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    private const string LibraryManifestFileName = "bibliothek.json";

    private const int MaxActionStatusHistory = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions LibraryManifestJsonOptions = new() { WriteIndented = true };

    // ==================== Sounds: unabhängig vom Bild/Video-"aktuelles Medium"-Slot ====================
    // Sounds laufen NIE über CurrentMediaKind/SetCurrentMediaStatus - dieser Slot ist exklusiv für
    // Bild/Video (immer nur eins gleichzeitig, ersetzt beim nächsten Senden das vorherige). Ein
    // Sound darf weder ein gerade gezeigtes Bild/Video ersetzen noch selbst durch ein neues
    // Bild/Video oder einen weiteren Sound gestoppt werden, und mehrere sollen parallel laufen
    // können - deshalb bekommt jeder Sound einen eigenen, kurzlebigen MediaPlayer statt den
    // gemeinsam genutzten MediaPlayer-Feld für Video. Sounds erscheinen entsprechend nie im
    // Spielerfenster (weder beim SL lokal noch beim Client - siehe ClientMainWindowViewModel.PlaySound),
    // sondern nur SL-seitig im "Aktuell abgespielte Sounds"-Panel (ActivePlayingSounds).

    private readonly Dictionary<string, MediaPlayer> _activeLocalSoundPlayers = new();
    private readonly DispatcherTimer _blinkTimer;

    private readonly GameClockService _clock;

    /// <summary>
    ///     Alle verfügbaren Designs (mitgelieferte SampleThemes + SL-eigene, siehe ThemeDefinitionLoader),
    ///     keyed nach dem in ThemeOptions gezeigten Anzeigenamen bzw. nach der Design-Id (für
    ///     Persistenz/Ambiente/Netzwerk-Sync). Es gibt keine separate Liste "eingebauter" Designs mehr -
    ///     alle laufen über denselben JSON-Lademechanismus.
    /// </summary>
    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesByDisplayName = new();

    private readonly Dictionary<string, ThemeDefinitionLoader.LoadedTheme> _themesById = new();

    /// <summary>Elemente, für deren aktuell laufende Occurrence bereits gewarnt wurde.</summary>
    private readonly HashSet<Guid> _headsUpFired = [];

    // ==================== Vorwarnung ("gleich passiert etwas") - rein SL-lokal, nie an Spieler gesendet ====================

    /// <summary>
    ///     Verbleibende Zeit beim letzten Tick, pro Element-Id - erkennt, wann ein Element neu "bewaffnet" wurde
    ///     (Reset/erneuter Wecker-Zyklus), um die Vorwarnung dafür wieder scharf zu schalten.
    /// </summary>
    private readonly Dictionary<Guid, TimeSpan> _headsUpLastRemaining = new();

    /// <summary>
    ///     Läuft eine unendliche Wiederholung (SoundRepeatCount == 0) gerade für ein Element, hier per Id nachschlagbar,
    ///     um sie beim Reset des Elements abzubrechen.
    /// </summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _infiniteSoundLoops = new();

    /// <summary>
    ///     Analog für die ITEM-eigene "Sound"-Auswahl (Timer/Wecker/Intervall-Feld, siehe
    ///     PlaySound(Guid,...)): welcher Sound-Bibliothek-Sound läuft gerade für welches Element.
    /// </summary>
    private readonly Dictionary<Guid, string> _itemSoundMediaIds = new();

    /// <summary>
    ///     Zuletzt angewandte Zeitsprünge (LIFO) - erlaubt Undo mehrerer Sprünge nacheinander, rein
    ///     lokal/session-basiert, nicht persistiert. Ein neuer Sprung (JumpBy) leert den Redo-Stack,
    ///     da ein neuer Sprung die "Zukunft" der bisherigen Redo-Historie ungültig macht - klassisches
    ///     Undo/Redo-Verhalten wie in Text-/Grafikeditoren.
    /// </summary>
    private readonly Stack<TimeSpan> _jumpUndoStack = new();

    /// <summary>Per UndoJump zurückgenommene Sprünge, per RedoJump wiederherstellbar (siehe _jumpUndoStack).</summary>
    private readonly Stack<TimeSpan> _jumpRedoStack = new();

    private readonly Dictionary<string, string> _localSoundCleanupPaths = new();

    /// <summary>
    ///     Spielt einen gesendeten Sound lokal beim SL ab - nur als Vorschau, wenn kein
    ///     Spieler-Client ohnehin dieselbe Datei abspielt (dieselbe Bedingung wie ShouldShowMediaLocally
    ///     für Bild/Video, hier aber ohne UI-Bindung, da Sounds bewusst nicht angezeigt werden).
    /// </summary>
    /// <summary>
    ///     Verbleibende Wiedergaben je MediaId (-1 = endlos/Loop, sonst zählt bei jedem
    ///     natürlichen Ende runter - siehe OnLocalSoundEnded).
    /// </summary>
    private readonly Dictionary<string, int> _localSoundRepeatsRemaining = new();

    private readonly TcpPlayerServerService _playerServer;

    /// <summary>
    ///     Assoziiert ein auslösendes TriggerMediaConfig (Event-Medium) mit dem MediaId des
    ///     gerade laufenden Sounds, damit StopMediaIfTriggeredBy ihn beim Reset des Timers/Weckers/
    ///     Intervalls mit beendet - analog zum Bild/Video-Slot, aber für den entkoppelten Sound-Pfad.
    /// </summary>
    private readonly Dictionary<TriggerMediaConfig, string> _triggerSoundMediaIds = new();

    private CancellationTokenSource? _actionStatusClearCts;

    /// <summary>Kurze, sich selbst löschende Erfolgs-Rückmeldung (z.B. "Timer angelegt") - rein informativ, kein Fehler.</summary>
    [ObservableProperty] private string? _actionStatusMessage;

    /// <summary>
    ///     Welches TriggerMediaConfig (falls überhaupt eines) das aktuell gezeigte Medium ausgelöst hat - siehe
    ///     StopMediaIfTriggeredBy.
    /// </summary>
    private TriggerMediaConfig? _activeTriggerMediaSource;

    /// <summary>
    ///     Wechselt den Hintergrund des aktuell aktiven SL-JSON-Designs anhand der Tageszeit (06-18 Uhr = erster
    ///     benannter Hintergrund, sonst der letzte) - bei eingebauten Designs oder Designs mit nur einem Hintergrund ein
    ///     No-Op.
    /// </summary>
    [ObservableProperty] private bool _ambienceAutomationEnabled;

    /// <summary>
    ///     Dateiname des zuletzt gesetzten Ambiente-Hintergrunds - verhindert unnötiges Neuladen desselben Bilds bei
    ///     jedem Tick.
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
    ///     Welches Galerie-Element zuletzt gezeigt/hervorgehoben wurde - für die automatische
    ///     Wiederaufnahme, wenn ein Event-Medium endet (siehe StopMediaIfTriggeredBy).
    /// </summary>
    private string? _currentGalleryMediaId;

    [ObservableProperty] private string _currentGameTimeText = string.Empty;

    [ObservableProperty] private Bitmap? _currentMediaBitmap;

    [ObservableProperty] private string? _currentMediaFileName;

    // ==================== Medien: Status-Tracking + optionale lokale Vorschau ====================

    /// <summary>
    ///     Ob CurrentMediaLocalPath eine Einweg-Cache-Kopie ist (darf gelöscht werden)
    ///     oder eine persistente Bibliotheksdatei (darf NICHT gelöscht werden).
    /// </summary>
    private bool _currentMediaIsOwnedCache;

    // ==================== Medien (Bild/Video für Spieler) ====================
    // Standardmäßig zeigt nur der Spieler-Client Medien an. Der Host zeigt sie zusätzlich
    // selbst an (über dem Spielerfenster), aber NUR wenn kein Client verbunden ist bzw. der
    // Server aus ist UND das lokale Spielerfenster offen ist - siehe ShouldShowMediaLocally.
    // Läuft ein Client mit, braucht es keine lokale Anzeige, da der Spieler es ohnehin sieht.

    [ObservableProperty] private MediaKind _currentMediaKind = MediaKind.None;

    [ObservableProperty] private string? _currentMediaLocalPath;

    [ObservableProperty] private decimal _headsUpLeadMinutes;

    [ObservableProperty] private string? _headsUpMessage;

    private CancellationTokenSource? _headsUpMessageClearCts;

    /// <summary>SL-lokale Vorwarnung, bevor ein Timer/Wecker/OnTime-Eintrag auslöst - wird nie an Spieler gesendet.</summary>
    [ObservableProperty] private bool _headsUpWarningEnabled;

    [ObservableProperty] private bool _isClockRunning;

    [ObservableProperty] private bool _isLocalPlayerFullscreen;

    [ObservableProperty] private bool _isNetworkServerRunning;

    [ObservableProperty] private bool _isPlayerWindowOpen;

    // Freier Zeitsprung, z.B. "08:00:00" vor oder "-1.00:00:00" für einen Tag zurück.
    [ObservableProperty] private string _jumpAmountText = "08:00:00";

    // Freitextfeld zum manuellen Setzen von Datum/Uhrzeit der Spielzeit.
    [ObservableProperty] private string _manualDateTimeText;

    [ObservableProperty] private string? _mediaErrorMessage;

    [ObservableProperty] private MediaPlayer? _mediaPlayer;

    /// <summary>
    ///     LAN-Adresse, unter der der Server erreichbar ist - nur gesetzt während er läuft
    ///     (siehe ToggleNetworkServer). Für die Statusleiste, damit der SL sie nicht selbst raussuchen muss.
    /// </summary>
    [ObservableProperty] private string? _networkServerAddress;

    [ObservableProperty] private int _networkServerPort = TcpPlayerServerService.DefaultPort;

    [ObservableProperty] private string _networkServerStatus = "Netzwerkserver aus";

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

    [ObservableProperty] private string _newMarkerName = "Neue Marke";

    [ObservableProperty] private string _newMarkerTime = "06:00";

    [ObservableProperty] private string _newTimerDuration = "00:10:00";

    [ObservableProperty] private decimal _newTimerDurationHours;

    [ObservableProperty] private decimal _newTimerDurationMinutes = 10;

    [ObservableProperty] private decimal _newTimerDurationSeconds;

    private CancellationTokenSource? _pendingVideoFallbackCts;

    // ==================== Video-Ende-Tracking (Loop vs. Ende+Schließen, optional Uhr-Pause) ====================
    // Gilt für JEDES gesendete Video (Ad-hoc, Bibliothek, Event-Trigger): ein nicht-loopendes
    // Video wird nach Ende automatisch geschlossen; ist zusätzlich pauseClockUntilEnd gesetzt,
    // bleibt die Spielzeit bis dahin pausiert. Maßgeblich ist, je nachdem was zuerst feuert: die
    // Rückmeldung eines Clients (media.playbackEnded, siehe
    // TcpPlayerServerService.ClientReportedPlaybackEnded) ODER MediaPlayer.EndReached des lokalen
    // Host-Players (siehe CreateLocalMediaPlayer) - eine per LibVLC lokal geschätzte Dauer dient
    // nur als Sicherheitsnetz, falls weder ein Client verbunden ist noch lokal wiedergegeben wird
    // oder beide Rückmeldungen verloren gehen.

    private string? _pendingVideoMediaId;
    private bool _pendingVideoPauseClock;
    [ObservableProperty] private string _playerCalendarMonthLabel = string.Empty;
    [ObservableProperty] private string _playerCalendarSelectedDateLabel = string.Empty;

    [ObservableProperty] private string _playerHeaderSubtitle;

    [ObservableProperty] private string _playerHeaderTitle;

    // ==================== Sitzungsprotokoll (nur SL-lokal, nur bei explizitem Opt-in) ====================

    [ObservableProperty] private bool _recordSessionLog;

    /// <summary>Ob Remote-Clients aktuell im Vollbild sein sollen.</summary>
    [ObservableProperty] private bool _remoteFullscreen;

    [ObservableProperty] private CalendarEntryViewModel? _selectedCalendarEntry;

    [ObservableProperty] private string _selectedThemeOption;

    /// <summary>Ob ein Ad-hoc gesendetes Video (nicht aus der Bibliothek) am Ende loopen soll.</summary>
    [ObservableProperty] private bool _sendMediaLoop;

    /// <summary>
    ///     Ob ein Ad-hoc gesendeter Sound (nicht aus der Sound-Bibliothek) am Ende loopen soll -
    ///     bewusst getrennt von SendMediaLoop, damit die beiden Tabs (Bibliothek/Sounds) sich nicht
    ///     gegenseitig beeinflussen.
    /// </summary>
    [ObservableProperty] private bool _sendSoundLoop;

    /// <summary>Im LAN/mDNS-Announcement übertragener Anzeigename (siehe ToggleNetworkServer).</summary>
    [ObservableProperty] private string _serverName = "RpgTimeTracker";

    /// <summary>
    ///     Optionaler PIN, den ein Client beim Verbinden (session.hello) mitschicken muss - leer
    ///     bedeutet "kein PIN erforderlich" (Standard, abwärtskompatibel). Wird von
    ///     TcpPlayerServerService.PerformHandshakeAsync gegen den vom Client gesendeten Wert geprüft.
    /// </summary>
    [ObservableProperty] private string _connectionPin = string.Empty;

    [ObservableProperty] private bool _showPlayerCalendarView;

    /// <summary>
    ///     Sekunden pro Bild für die automatische Weiterschaltung bei allen Clients (0 = manuell).
    ///     Gilt nie für Videos (siehe design-decisions.md).
    /// </summary>
    [ObservableProperty] private double _slideshowSecondsPerImage;

    private DispatcherTimer? _slideshowTimer;

    [ObservableProperty] private double _speedMultiplier = 1.0;

    [ObservableProperty] private decimal _speedMultiplierInput = 1.0m;

    /// <summary>
    ///     Nur ein Test läuft je gleichzeitig - hält die Referenz, damit der Player nicht
    ///     vorzeitig vom GC eingesammelt wird (kein Dictionary-Eintrag wie bei echten Sounds nötig).
    /// </summary>
    private MediaPlayer? _testSoundPlayer;

    public MainWindowViewModel()
    {
        // Sinnvoller Fantasy-Startzeitpunkt, kann sofort umgestellt werden.
        var start = DateTime.Now;
        _clock = new GameClockService(start);
        _clock.Tick += OnClockTick;
        _playerServer = new TcpPlayerServerService(BuildSessionSnapshot, BuildClockHeartbeat, () => ConnectionPin);
        // Zeit wird nur bei echten Sprüngen übers Netz geschickt (nicht bei jedem Tick) -
        // der Client rechnet Zeit lokal ab dem letzten Sprung mit der bekannten Geschwindigkeit
        // weiter, siehe RpgTimeTracker.Shared.Services.GameClockService auf Client-Seite.
        _clock.Jumped += newTime => _ = _playerServer.PublishClockTimeJumpedAsync(newTime);
        // Steuert, ob ein gerade laufendes Medium zusätzlich lokal (über dem Spielerfenster)
        // gezeigt wird - siehe ShouldShowMediaLocally.
        _playerServer.ClientCountChanged += count => Dispatcher.UIThread.Post(() => ConnectedClientCount = count);
        _playerServer.ClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateConnectedClients(clients));
        // Maßgeblich für Video-Ende-Tracking (Loop/Schließen, ggf. Uhr-Pause) - siehe BeginVideoTracking.
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
        _playerHeaderTitle = settings.PlayerHeaderTitle;
        _playerHeaderSubtitle = settings.PlayerHeaderSubtitle;
        _headsUpWarningEnabled = settings.HeadsUpWarningEnabled;
        _headsUpLeadMinutes = (decimal)settings.HeadsUpLeadMinutes;
        _serverName = string.IsNullOrWhiteSpace(settings.ServerName) ? "RpgTimeTracker" : settings.ServerName;
        _connectionPin = settings.ConnectionPin;

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

        // Einmalige Migration alter Daten, damit beim Umbau auf die getrennte Sound-Bibliothek
        // nichts verloren geht: (a) Sounds aus der alten "+ Sound hinzufügen"-Einstellungen-Funktion,
        // (b) Audio-Einträge, die vor der Trennung in der Bild/Video-Bibliothek gelandet waren.
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
            Log.Information("Alte Sound-Daten in die neue Sound-Bibliothek migriert ({Count} Einträge)",
                SoundLibrary.Count);
        }

        foreach (var loaded in ThemeDefinitionLoader.LoadAll())
        {
            // Mitgelieferte Designs zeigen ihren kurzen, neutralen Namen (PickerLabel, z.B.
            // "Shadowrun / Cyberpunk"); ein SL-eigenes Design bekommt einen Hinweis-Suffix, damit
            // in der Liste erkennbar bleibt, was von der App mitgeliefert wurde und was nicht.
            var baseName = loaded.Definition.PickerLabel ?? loaded.Definition.DisplayName;
            var label = loaded.IsSample ? baseName : $"{baseName} (eigenes Design)";
            ThemeOptions.Add(label);
            _themesByDisplayName[label] = loaded;
            _themesById[loaded.Definition.Id] = loaded;
        }

        var resolvedTheme = ThemeDefinitionLoader.Resolve(ThemeSettingsService.LoadLastThemeId())
                             ?? ThemeDefinitionLoader.Resolve("shadowrun");
        if (resolvedTheme is { } theme)
        {
            // Nicht per ReferenceEquals vergleichen: Resolve() ruft intern erneut LoadAll() auf und
            // erzeugt damit frische ThemeDefinitionDto-Instanzen, unabhängig von denen oben in
            // _themesByDisplayName - der Id-Wert ist der einzig stabile Vergleichspunkt.
            _selectedThemeOption = _themesByDisplayName
                .First(kv => kv.Value.Definition.Id == theme.Definition.Id).Key;
            CurrentThemeId = theme.Definition.Id;
        }
        else
        {
            // Sollte praktisch nie passieren (SampleThemes fehlt komplett im Anwendungsverzeichnis) -
            // Feld darf trotzdem nie uninitialisiert bleiben, ThemeOptions ist dann aber ohnehin leer.
            _selectedThemeOption = string.Empty;
        }

        _ambienceAutomationEnabled = settings.AmbienceAutomationEnabled;

        AddDefaultMarker("Morgengrauen", new TimeSpan(6, 0, 0));
        AddDefaultMarker("Mittag", new TimeSpan(12, 0, 0));
        AddDefaultMarker("Abend", new TimeSpan(18, 0, 0));
        AddDefaultMarker("Mitternacht", TimeSpan.Zero);

        _calendarMonth = new DateTime(start.Year, start.Month, 1);
        _calendarSelectedDate = start.Date;
        RefreshCalendarViews();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ObservableCollection<AlarmItemViewModel> Alarms { get; } = [];
    public ObservableCollection<IntervalEventItemViewModel> IntervalEvents { get; } = [];

    /// <summary>Gemeinsame Liste für das SL-Fenster.</summary>
    public ObservableCollection<TimelineDisplayItemViewModel> TimelineItems { get; } = [];

    /// <summary>Gefilterte gemeinsame Liste für das Spielerfenster.</summary>
    public ObservableCollection<TimelineDisplayItemViewModel> PlayerTimelineItems { get; } = [];

    public ObservableCollection<JumpMarkerItemViewModel> JumpMarkers { get; } = [];
    public ObservableCollection<CalendarEntryViewModel> CalendarEntries { get; } = [];
    public ObservableCollection<CalendarEntryViewModel> CalendarEntriesForSelectedDate { get; } = [];
    public ObservableCollection<PlayerCalendarDayViewModel> PlayerCalendarDays { get; } = [];
    public ObservableCollection<PlayerCalendarEntryViewModel> PlayerCalendarEntries { get; } = [];

    // Anzeigenamen der Stile für die ComboBox; wird im Konstruktor aus ThemeDefinitionLoader.LoadAll()
    // befüllt (siehe _themesByDisplayName) - keine feste Liste mehr, da es keine "eingebauten" Designs
    // getrennt von JSON-Designs mehr gibt.
    public ObservableCollection<string> ThemeOptions { get; } = [];

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public IReadOnlyList<string> CalendarRecurrenceOptions => CalendarEntryViewModel.RecurrenceOptions;

    public bool HasAmbienceBackgrounds =>
        _themesById.TryGetValue(CurrentThemeId ?? string.Empty, out var loaded) &&
        loaded.Definition.Backgrounds.Count > 1;

    private string? CurrentThemeId { get; set; }

    public string ToggleNetworkServerLabel => IsNetworkServerRunning ? "Server stoppen" : "Server starten";

    /// <summary>Kompakte Zusammenfassung für die Statusleiste am unteren Fensterrand.</summary>
    public string ServerStatusSummary => IsNetworkServerRunning
        ? $"{NetworkServerAddress ?? "?"}:{NetworkServerPort} · {ConnectedClientCount} Spieler"
        : "Server gestoppt";

    public NewItemBasicsViewModel NewTimerBasics { get; } = new("Neuer Timer", VisualItemHelper.IconTimer);

    public TriggerMediaConfig NewTimerTriggerMedia { get; } = new();

    public NewItemBasicsViewModel NewAlarmBasics { get; } = new("Neuer Wecker", VisualItemHelper.IconAlarm);

    public TriggerMediaConfig NewAlarmTriggerMedia { get; } = new();

    public NewItemBasicsViewModel NewIntervalBasics { get; } =
        new("Neues Intervall", VisualItemHelper.IconOnTime, "#FFD45A", true);

    public TriggerMediaConfig NewIntervalTriggerMedia { get; } = new();

    public ObservableCollection<ConnectedClientItemViewModel> ConnectedClientItems { get; } = [];
    public bool HasNoConnectedClients => ConnectedClientItems.Count == 0;

    public bool HasMedia => CurrentMediaKind != MediaKind.None;
    public bool HasCalendarEntries => CalendarEntries.Count > 0;
    public bool HasNoCalendarEntries => CalendarEntries.Count == 0;
    public bool HasCalendarEntriesForSelectedDate => CalendarEntriesForSelectedDate.Count > 0;
    public bool HasSelectedCalendarEntry => SelectedCalendarEntry is not null;

    /// <summary>Ein gemeinsamer Vollbild-Schalter ist sinnvoll, sobald lokal oder remote etwas steuerbar ist.</summary>
    public bool CanToggleDisplayFullscreen => IsPlayerWindowOpen || IsNetworkServerRunning;

    public bool IsDisplayFullscreenActive => RemoteFullscreen || IsLocalPlayerFullscreen;

    public string DisplayFullscreenLabel => IsDisplayFullscreenActive
        ? "Spieler-Vollbild beenden"
        : "Spieler-Vollbild";

    /// <summary>Kein Client verbunden (oder Server aus) UND das lokale Spielerfenster ist offen.</summary>
    public bool ShouldShowMediaLocally =>
        HasMedia && IsPlayerWindowOpen && (!IsNetworkServerRunning || ConnectedClientCount == 0);

    public bool ShowLocalImage => ShouldShowMediaLocally && CurrentMediaKind == MediaKind.Image;
    public bool ShowLocalVideo => ShouldShowMediaLocally && CurrentMediaKind == MediaKind.Video;

    public string ToggleClockLabel => IsClockRunning ? "⏸ Pause" : "▶ Start";

    public string NextEventJumpLabel => GetNextEventDelta() is { } delta
        ? $"⏭ Nächstes Event ({FormatTimeSpan(delta)})"
        : "⏭ Kein Event";

    public string ClockStatusText => IsClockRunning ? "Spielzeit läuft" : "Spielzeit angehalten";

    // ==================== Medienbibliothek (vorausgewählt, per Doppelklick anzeigen) ====================

    public ObservableCollection<MediaLibraryItemViewModel> MediaLibrary { get; } = [];

    public bool HasNoMediaLibraryItems => MediaLibrary.Count == 0;

    // ==================== Sound-Bibliothek (getrennt von der Bild/Video-Bibliothek) ====================

    public ObservableCollection<SoundLibraryItemViewModel> SoundLibrary { get; } = [];

    public bool HasNoSoundLibraryItems => SoundLibrary.Count == 0;

    // ==================== Galerie: session-eigene Liste gesendeter Bilder/Videos ====================
    // Anders als das Einzel-Slot-Modell oben (CurrentMediaKind) wird hier NICHTS beim nächsten
    // Senden verworfen - jedes per Ad-hoc/Bibliothek gesendete Bild/Video bleibt in SentMediaItems,
    // bis es explizit entfernt wird. SL und jeder Spieler navigieren unabhängig voneinander lokal
    // durch diese Liste (siehe ClientMainWindowViewModel); "Hervorheben" ist nur ein Sprung-Vorschlag,
    // kein Zwang. Event-Trigger-Medien sind NIE Teil davon - sie überschreiben die Anzeige nur
    // vorübergehend (Vorrang), siehe StopMediaIfTriggeredBy für die Wiederaufnahme danach.

    public ObservableCollection<SentMediaItemViewModel> SentMediaItems { get; } = [];
    public bool HasNoSentMediaItems => SentMediaItems.Count == 0;

    public ObservableCollection<string> SessionLogEntries { get; } = [];

    public bool HasSessionLogEntries => SessionLogEntries.Count > 0;

    /// <summary>
    ///     Verlauf der letzten Aktions-Hinweise (neueste zuerst) - fürs Notifications-Flyout in
    ///     der Statusleiste. ActionStatusMessage bleibt der "gerade neu"-Indikator (löscht sich nach
    ///     4s selbst, siehe ClearActionStatusAfterDelayAsync), dieser Verlauf bleibt bestehen, bis er
    ///     die Kappungsgrenze überschreitet.
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

    public string SpeedMultiplierDisplay => $"{SpeedMultiplier:0.0}×";

    partial void OnNetworkServerAddressChanged(string? value)
    {
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnIsPlayerWindowOpenChanged(bool value)
    {
        if (!value) IsLocalPlayerFullscreen = false;
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        RefreshLocalMediaPreview();
    }

    /// <summary>
    ///     SL fordert an, das lokale Spielerfenster (falls offen) fullscreen zu schalten - z.B. wenn ein Event-Medium mit
    ///     "Vollbild" auslöst.
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
        RefreshLocalMediaPreview();
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
    ///     Vereinheitlicht den Vollbild-Wunsch für alles, was es aktuell gibt: lokales
    ///     Spielerfenster, Remote-Clients oder beides.
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
    ///     Entscheidet anhand von ShouldShowMediaLocally, ob das aktuelle Medium lokal decodiert/
    ///     abgespielt wird. Wird explizit statt über eine reine Property-Diff aufgerufen, weil sich
    ///     z.B. CurrentMediaKind bei zwei gleichartigen Medien hintereinander (Bild -> Bild) nicht
    ///     "ändert" und die WPF/Avalonia-Standardbenachrichtigung dann ausbleiben würde.
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
                // Fehleranzeige bleibt dem Medien-Tab-Status (MediaErrorMessage) vorbehalten.
            }
        }
    }

    private void StopLocalVideo()
    {
        MediaPlayer?.Stop();
    }

    /// <summary>
    ///     Löst BeginVideoTracking/ResolvePendingVideo auch dann exakt zum tatsächlichen Ende auf,
    ///     wenn das Video (auch) lokal im Host-Spielerfenster läuft, statt sich allein auf die
    ///     Client-Rückmeldung bzw. die Dauer-Schätzung als Fallback zu verlassen (siehe
    ///     BeginVideoTracking-Kommentar). Beide Quellen zielen auf denselben mediaId-Wert -
    ///     ResolvePendingVideo ist dank des _pendingVideoMediaId-Abgleichs bereits idempotent,
    ///     welche Quelle auch zuerst feuert.
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
    ///     Sendet den aktuellen Zustand eines Items als timelineItem.upserted (oder als
    ///     timelineItem.removed, falls es gerade nicht spieler-sichtbar ist). Wird von den
    ///     StateChanged-Events der Timer/Alarm/Intervall-ViewModels ausgelöst - NICHT bei jedem
    ///     Uhr-Tick, sondern nur bei echten diskreten Zustandsänderungen.
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
        PlayerCalendarMonthLabel = CalendarMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        PlayerCalendarSelectedDateLabel =
            CalendarSelectedDate.ToString("dddd, dd.MM.yyyy", CultureInfo.CurrentCulture);

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

        // Designs werden per RPC als reine Design-Id an Clients gepusht (siehe design-decisions.md) -
        // ein Client muss dieselbe theme.json lokal besitzen (mitgelieferte SampleThemes oder dieselbe
        // %AppData%/RpgTimeTracker/Themes-Datei), um sie darzustellen; ohne passende lokale Datei
        // bleibt der Client auf seinem zuletzt aktiven Design (siehe ClientMainWindowViewModel.ApplyTheme).
        _ = _playerServer.PublishThemeChangedAsync(GetCurrentThemeWireValue());
        UpdateAmbience();
    }

    /// <summary>Der über das Netz verschickte/im session.snapshot eingebettete Theme-Wert - die Design-Id.</summary>
    private string GetCurrentThemeWireValue()
    {
        return _themesByDisplayName.TryGetValue(SelectedThemeOption, out var loaded) ? loaded.Definition.Id : "shadowrun";
    }

    /// <summary>
    ///     Wechselt WindowBackgroundImageBrush zwischen dem ersten und letzten benannten Hintergrund
    ///     des aktuell aktiven Designs, je nachdem ob gerade Tag (06-18 Uhr) oder Nacht ist. Ein
    ///     No-Op, solange das aktive Design nur einen Hintergrund hat (das betrifft aktuell alle
    ///     mitgelieferten Beispiele außer "Mittelalter").
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
        _ = _playerServer.PublishClockSpeedChangedAsync(rounded);
    }

    partial void OnIsNetworkServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        OnPropertyChanged(nameof(ServerStatusSummary));
        RefreshLocalMediaPreview();
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

    private void SaveUiSettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.PlayerHeaderTitle =
            string.IsNullOrWhiteSpace(PlayerHeaderTitle) ? "Spieleranzeige" : PlayerHeaderTitle;
        settings.PlayerHeaderSubtitle = PlayerHeaderSubtitle;
        settings.HeadsUpWarningEnabled = HeadsUpWarningEnabled;
        settings.HeadsUpLeadMinutes = (double)HeadsUpLeadMinutes;
        settings.AmbienceAutomationEnabled = AmbienceAutomationEnabled;
        settings.ServerName = string.IsNullOrWhiteSpace(ServerName) ? "RpgTimeTracker" : ServerName;
        settings.ConnectionPin = ConnectionPin;
        ThemeSettingsService.SaveSettings(settings);
    }

    // ==================== Medien senden (Bild/Video) ====================

    /// <summary>
    ///     Liest die gewählte Datei ein, zeigt sie lokal im Spielerfenster an und verteilt sie
    ///     über das Netzwerk an alle verbundenen Spieler-Clients.
    /// </summary>
    public async Task SendMediaFromPathAsync(string sourcePath)
    {
        Log.Information("Ad-hoc-Medium wird gesendet: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            Log.Warning("Ad-hoc-Medium abgelehnt: nicht unterstütztes Format ({SourcePath})", sourcePath);
            MediaErrorMessage =
                "Nicht unterstütztes Dateiformat. Erlaubt: Bilder (PNG/JPG/GIF/BMP/WebP) oder Videos (MP4/WebM/MKV/MOV/AVI). Für Audio siehe den Sounds-Tab.";
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

        // isOwnedCache: false - die Cache-Kopie bleibt jetzt für die Galerie erhalten (rückblätterbar),
        // statt beim nächsten Ad-hoc-Senden automatisch gelöscht zu werden. Aufräumen übernimmt
        // RetractSentMediaItem, wenn dieser Galerie-Eintrag explizit entfernt wird.
        SetCurrentMediaStatus(kind, cachedPath, fileName, false);
        AddSentMediaItem(header, cachedPath, true);
        RecordSessionEvent($"Medium gesendet (Ad-hoc): {fileName}");
        ShowActionStatus($"Medium \"{fileName}\" gesendet");

        await _playerServer.PublishMediaAsync(header, bytes);
        BeginVideoTracking(header, cachedPath, false);
    }

    /// <summary>
    ///     Liest+validiert eine ad-hoc gewählte Datei und legt eine GUID-benannte Arbeitskopie
    ///     im angegebenen Cache-Verzeichnis an (gemeinsame Logik für Bild/Video- und Sound-Ad-hoc-Versand).
    ///     Setzt bei Fehlern MediaErrorMessage und liefert null.
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
                Log.Warning("Ad-hoc-Datei abgelehnt: nicht gefunden ({SourcePath})", sourcePath);
                MediaErrorMessage = "Datei nicht gefunden.";
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc-Datei konnte nicht gelesen werden ({SourcePath})", sourcePath);
            MediaErrorMessage = $"Datei konnte nicht gelesen werden: {ex.Message}";
            return null;
        }

        if (info.Length > TcpPlayerServerService.MaxMediaBytes)
        {
            Log.Warning("Ad-hoc-Datei abgelehnt: {SizeMb} MB überschreitet Limit ({SourcePath})",
                info.Length / (1024 * 1024), sourcePath);
            MediaErrorMessage =
                $"Datei zu groß ({info.Length / (1024 * 1024)} MB). Maximal {TcpPlayerServerService.MaxMediaBytes / (1024 * 1024)} MB erlaubt.";
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc-Datei konnte nicht gelesen werden ({SourcePath})", sourcePath);
            MediaErrorMessage = $"Datei konnte nicht gelesen werden: {ex.Message}";
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
            Log.Error(ex, "Ad-hoc-Datei konnte nicht zwischengespeichert werden ({SourcePath})", sourcePath);
            MediaErrorMessage = $"Medium konnte nicht zwischengespeichert werden: {ex.Message}";
            return null;
        }

        return (bytes, cachedPath);
    }

    /// <summary>
    ///     Ad-hoc-Sound-Versand (Sounds-Tab, ohne ihn zur Bibliothek hinzuzufügen) - läuft
    ///     komplett über SendSoundAsync, siehe dessen Kommentar.
    /// </summary>
    public async Task SendSoundFromPathAsync(string sourcePath)
    {
        Log.Information("Ad-hoc-Sound wird gesendet: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            Log.Warning("Ad-hoc-Sound abgelehnt: nicht unterstütztes Format ({SourcePath})", sourcePath);
            MediaErrorMessage = "Nicht unterstütztes Dateiformat. Erlaubt: MP3/WAV/OGG/FLAC/M4A/AAC.";
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

        RecordSessionEvent($"Sound gesendet (Ad-hoc): {fileName}");
        ShowActionStatus($"Sound \"{fileName}\" gesendet");
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

        // Vollbild wird beim Schließen/Zurücksetzen eines Mediums bewusst NICHT angerührt - das
        // hat vorher ungewollt ein per Trigger oder manuell eingeschaltetes Vollbild (z.B. "Liste
        // beim Spieler dauerhaft fullscreen zeigen") beendet, nur weil irgendein Bild/Video
        // geschlossen wurde. Vollbild ändert sich nur noch über die explizite Vollbild-Aktion.
    }

    /// <summary>Fügt eine Datei zur Bibliothek hinzu, OHNE sie sofort anzuzeigen/zu senden.</summary>
    public async Task AddMediaToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            MediaErrorMessage =
                "Nicht unterstütztes Dateiformat. Erlaubt: Bilder (PNG/JPG/GIF/BMP/WebP) oder Videos (MP4/WebM/MKV/MOV/AVI). Für Audio siehe den Sounds-Tab.";
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = "Datei nicht gefunden.";
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
            Log.Information("Medium zur Bibliothek hinzugefügt: {Name} ({Kind})", item.Name, item.Kind);
            ShowActionStatus($"\"{item.Name}\" zur Bibliothek hinzugefügt");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medium konnte nicht zur Bibliothek hinzugefügt werden ({SourcePath})", sourcePath);
            MediaErrorMessage = $"Medium konnte nicht zur Bibliothek hinzugefügt werden: {ex.Message}";
        }
    }

    /// <summary>
    ///     Vom View gesetzt (siehe MainWindow.axaml.cs), da eine Warnung/Rückfrage ein Window braucht,
    ///     das das ViewModel selbst nicht kennt - analog zu den LibraryPanelView-Funcs weiter oben.
    ///     Ohne Handler (z.B. beim Testen) wird ein zugewiesenes Medium NIE stillschweigend gelöscht.
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
                    // Datei bleibt bewusst auf der Platte - die betroffenen Elemente referenzieren
                    // sie weiterhin direkt (siehe TriggerMediaConfig-Kommentar: kein eigener Kopie-
                    // Mechanismus), nur der Bibliothekseintrag verschwindet.
                    MediaLibrary.Remove(item);
                    OnPropertyChanged(nameof(HasNoMediaLibraryItems));
                    SaveMediaLibrarySettings();
                    RefreshCalendarViews();
                    Log.Information(
                        "Medium nur aus Bibliothek entfernt, Datei bleibt erhalten (noch verwendet von {Count} Elementen): {Name}",
                        usedBy.Count, item.Name);
                    return;
            }
        }

        MediaLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoMediaLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        SaveMediaLibrarySettings();
        RefreshCalendarViews();
        Log.Information("Medium aus Bibliothek entfernt: {Name}", item.Name);
    }

    /// <summary>
    ///     Prüft, ob localPath aktuell als Event-Medium zugewiesen ist - über alle Timer/Wecker/
    ///     Intervalle sowie Kalendereinträge hinweg, ausschließlich über TriggerMedia.Path (NICHT über
    ///     den Namen des Elements, der frei änderbar ist und nichts mit der Datei-Identität zu tun hat).
    ///     Gibt Anzeige-Label je Fundstelle zurück (für die Warnung), keine Objektreferenzen.
    /// </summary>
    private List<string> FindTriggerMediaUsageLabels(string localPath)
    {
        var labels = new List<string>();

        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        labels.AddRange(Timers.Where(t => Matches(t.TriggerMedia)).Select(t => $"Timer \"{t.Name}\""));
        labels.AddRange(Alarms.Where(a => Matches(a.TriggerMedia)).Select(a => $"Wecker \"{a.Name}\""));
        labels.AddRange(IntervalEvents.Where(i => Matches(i.TriggerMedia)).Select(i => $"Intervall \"{i.Name}\""));
        labels.AddRange(CalendarEntries.Where(c => Matches(c.TriggerMedia)).Select(c => $"Kalendereintrag \"{c.Title}\""));

        return labels;
    }

    /// <summary>Setzt TriggerMedia bei allen Elementen zurück, die localPath aktuell zugewiesen haben (siehe FindTriggerMediaUsageLabels).</summary>
    private void ClearTriggerMediaReferences(string localPath)
    {
        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        foreach (var t in Timers.Where(t => Matches(t.TriggerMedia))) t.TriggerMedia.ClearCommand.Execute(null);
        foreach (var a in Alarms.Where(a => Matches(a.TriggerMedia))) a.TriggerMedia.ClearCommand.Execute(null);
        foreach (var i in IntervalEvents.Where(i => Matches(i.TriggerMedia))) i.TriggerMedia.ClearCommand.Execute(null);
        foreach (var c in CalendarEntries.Where(c => Matches(c.TriggerMedia))) c.TriggerMedia.ClearCommand.Execute(null);
    }

    /// <summary>Fügt eine Audiodatei zur Sound-Bibliothek hinzu, OHNE sie sofort zu senden.</summary>
    public async Task AddSoundToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            MediaErrorMessage = "Nicht unterstütztes Dateiformat. Erlaubt: MP3/WAV/OGG/FLAC/M4A/AAC.";
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = "Datei nicht gefunden.";
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
            Log.Information("Sound zur Bibliothek hinzugefügt: {Name}", item.Name);
            ShowActionStatus($"\"{item.Name}\" zur Sound-Bibliothek hinzugefügt");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound konnte nicht zur Bibliothek hinzugefügt werden ({SourcePath})", sourcePath);
            MediaErrorMessage = $"Sound konnte nicht zur Bibliothek hinzugefügt werden: {ex.Message}";
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
        Log.Information("Sound aus Bibliothek entfernt: {Name}", item.Name);
    }

    /// <summary>Einfachklick-Handler: sendet diesen Bibliothekssound sofort an alle Spieler (siehe SendSoundAsync).</summary>
    private async void PlaySoundLibraryItem(SoundLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Bibliothekssound nicht gefunden: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = "Datei in der Sound-Bibliothek wurde nicht gefunden (evtl. manuell gelöscht).";
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bibliothekssound konnte nicht gelesen werden: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = $"Datei konnte nicht gelesen werden: {ex.Message}";
            return;
        }

        Log.Information("Bibliothekssound wird gesendet: {Name}", item.Name);

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

        RecordSessionEvent($"Sound gesendet (Bibliothek): {item.Name}");
        ShowActionStatus($"Sound \"{item.Name}\" gesendet");
        await SendSoundAsync(header, bytes, item.LocalPath, false);
    }

    /// <summary>
    ///     "Test"-Button: spielt den Sound NUR lokal beim SL ab (nie gesendet, kein
    ///     ActivePlayingSounds-Eintrag) - zum Prüfen der eingestellten Lautstärke vorab.
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
            Log.Error(ex, "Sound-Test fehlgeschlagen: {Name}", item.Name);
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
            Log.Information("Medien-Bibliothek exportiert nach {Folder} ({Count} Dateien)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus($"Medien-Bibliothek exportiert ({manifest.Items.Count} Dateien)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medien-Bibliothek-Export fehlgeschlagen ({Folder})", targetFolder);
            MediaErrorMessage = $"Export fehlgeschlagen: {ex.Message}";
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
            Log.Information("Medien-Bibliothek importiert von {Folder} ({Count} Dateien)", sourceFolder, imported);
            ShowActionStatus($"{imported} Medien importiert");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medien-Bibliothek-Import fehlgeschlagen ({Folder})", sourceFolder);
            MediaErrorMessage = $"Import fehlgeschlagen: {ex.Message}";
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
            Log.Information("Sound-Bibliothek exportiert nach {Folder} ({Count} Dateien)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus($"Sound-Bibliothek exportiert ({manifest.Items.Count} Dateien)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound-Bibliothek-Export fehlgeschlagen ({Folder})", targetFolder);
            MediaErrorMessage = $"Export fehlgeschlagen: {ex.Message}";
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
            Log.Information("Sound-Bibliothek importiert von {Folder} ({Count} Dateien)", sourceFolder, imported);
            ShowActionStatus($"{imported} Sounds importiert");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound-Bibliothek-Import fehlgeschlagen ({Folder})", sourceFolder);
            MediaErrorMessage = $"Import fehlgeschlagen: {ex.Message}";
        }
    }

    private async Task<LibraryManifestDto?> ReadLibraryManifestAsync(string sourceFolder)
    {
        var manifestPath = Path.Combine(sourceFolder, LibraryManifestFileName);
        if (!File.Exists(manifestPath))
        {
            MediaErrorMessage = "Kein gültiger Bibliotheksexport in diesem Ordner gefunden.";
            return null;
        }

        var manifest = JsonSerializer.Deserialize<LibraryManifestDto>(await File.ReadAllTextAsync(manifestPath));
        if (manifest is null) MediaErrorMessage = "Manifest konnte nicht gelesen werden.";
        return manifest;
    }

    /// <summary>Doppelklick-Handler: zeigt ein bereits in der Bibliothek gespeichertes Medium sofort an und sendet es.</summary>
    private async void ShowMediaLibraryItem(MediaLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Bibliotheksmedium nicht gefunden: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = "Datei in der Bibliothek wurde nicht gefunden (evtl. manuell gelöscht).";
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bibliotheksmedium konnte nicht gelesen werden: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = $"Datei konnte nicht gelesen werden: {ex.Message}";
            return;
        }

        Log.Information("Bibliotheksmedium wird gesendet: {Name} ({Kind})", item.Name, item.Kind);

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = item.Kind.ToString(),
            FileName = item.Name,
            MimeType = item.MimeType,
            Loop = item.Loop,
            AddToGallery = true
        };

        // isOwnedCache: false - das ist die persistente Bibliotheksdatei, die darf beim
        // nächsten Medienwechsel nicht wie ein Einweg-Cache gelöscht werden.
        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        AddSentMediaItem(header, item.LocalPath, false);
        RecordSessionEvent($"Medium gesendet (Bibliothek): {item.Name}");
        ShowActionStatus($"Medium \"{item.Name}\" gesendet");

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

        // Jedes neu gezeigte Medium ersetzt ein evtl. vorheriges Event-Medium - der Auslöser
        // (falls SendTriggerMediaAsync direkt danach _activeTriggerMediaSource erneut setzt)
        // gilt sonst weiter für ein völlig anderes, z.B. per Ad-hoc-Senden gezeigtes Medium.
        _activeTriggerMediaSource = null;

        if (previousWasOwned) DeleteFileQuietly(previousPath);

        // Explizit statt nur über OnCurrentMediaKindChanged: bei zwei Medien gleicher Art
        // hintereinander (z.B. Bild -> Bild) "ändert" sich CurrentMediaKind nicht, der Pfad
        // aber schon - ohne diesen expliziten Aufruf würde die lokale Vorschau stehen bleiben.
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
    ///     Der SL-eigene Vor/Zurück-Mechanismus ist immer der globale "Hervorheben"-Broadcast
    ///     (siehe HighlightSentMediaItem) - anders als bei Spielern gibt es beim SL keine getrennte rein
    ///     lokale Navigation, Pfeiltasten im Host-Spielerfenster nutzen dieselben Commands.
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
    ///     Videos werden nie automatisch weggeschaltet - sie haben ihre eigene, natürliche
    ///     Dauer; der Timer überspringt diesen Tick einfach und prüft beim nächsten erneut.
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
    ///     SL-Klick auf ein Galerie-Element: springt bei SL und allen Spielern (lokal, nicht
    ///     sperrend) auf dieses Bild/Video, ohne es erneut über das Netzwerk zu übertragen.
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
            NetworkServerStatus = "Netzwerkserver aus";
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
            NetworkServerStatus = $"Netzwerkserver aktiv auf Port {port}";
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Netzwerkserver konnte nicht auf Port {Port} gestartet werden", NetworkServerPort);
            IsNetworkServerRunning = false;
            NetworkServerStatus = $"Serverstart fehlgeschlagen: {ex.Message}";
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
            Log.Information("Spielzeit pausiert bei {GameTime}", _clock.CurrentTime);
            _ = _playerServer.PublishClockStoppedAsync();
        }
        else
        {
            _clock.Start();
            IsClockRunning = _clock.IsRunning;
            Log.Information("Spielzeit gestartet bei {GameTime} (Geschwindigkeit {Speed}x)", _clock.CurrentTime,
                SpeedMultiplier);
            _ = _playerServer.PublishClockStartedAsync();
        }
    }

    [RelayCommand]
    private void ApplyManualDateTime()
    {
        if (!DateTime.TryParse(ManualDateTimeText, out var parsed))
        {
            ClockErrorMessage = "Ungültiges Datum/Zeit (Format: JJJJ-MM-TT HH:mm:ss)";
            return;
        }

        ClockErrorMessage = null;
        var delta = parsed - _clock.CurrentTime;
        if (delta == TimeSpan.Zero) return;

        Log.Information("Spielzeit manuell gesetzt: {OldTime} -> {NewTime}", _clock.CurrentTime, parsed);
        _clock.SetTime(parsed);
        PushJump(delta);
        RecordSessionEvent($"Spielzeit manuell gesetzt auf {FormatGameTime(parsed)}");
    }


    [RelayCommand]
    private void JumpToNextEvent()
    {
        var next = GetNextEventDelta();
        if (next is null || next.Value <= TimeSpan.Zero)
        {
            ClockErrorMessage = "Kein laufender Timer, aktiver OnTime-Eintrag oder zukünftiger Wecker gefunden.";
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

    // ==================== Zeitsprung (vor & zurück) ====================

    [RelayCommand]
    private void JumpForward()
    {
        if (!TimeSpan.TryParse(JumpAmountText, out var parsed) || parsed == TimeSpan.Zero)
        {
            ClockErrorMessage = "Ungültiger Zeitsprung (Format hh:mm:ss oder t.hh:mm:ss, mit '-' für rückwärts)";
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
            ClockErrorMessage = "Ungültiger Zeitsprung (Format hh:mm:ss oder t.hh:mm:ss, mit '-' für rückwärts)";
            return;
        }

        ClockErrorMessage = null;
        JumpBy(-parsed);
    }

    // Wird von den Schnellauswahl-Buttons mit einem TimeSpan-String als CommandParameter aufgerufen.
    [RelayCommand]
    private void QuickJump(string amount)
    {
        if (TimeSpan.TryParse(amount, out var parsed)) JumpBy(parsed);
    }

    private void JumpBy(TimeSpan delta)
    {
        Log.Information("Zeitsprung um {Delta} von {OldTime}", delta, _clock.CurrentTime);
        _clock.Jump(delta);
        PushJump(delta);
        RecordSessionEvent($"Zeitsprung {(delta < TimeSpan.Zero ? "-" : "+")}{FormatTimeSpan(delta.Duration())}");
    }

    /// <summary>Verbucht einen bereits angewandten Sprung auf dem Undo-Stack und leert den Redo-Stack (siehe dort).</summary>
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
        Log.Information("Zeitsprung rückgängig gemacht: Gegensprung um {Delta} von {OldTime}", -delta,
            _clock.CurrentTime);
        _clock.Jump(-delta);
        _jumpRedoStack.Push(delta);
        CanUndoJump = _jumpUndoStack.Count > 0;
        CanRedoJump = true;
        RecordSessionEvent("Zeitsprung rückgängig gemacht");
    }

    [RelayCommand]
    private void RedoJump()
    {
        if (_jumpRedoStack.Count == 0) return;

        var delta = _jumpRedoStack.Pop();
        Log.Information("Zeitsprung wiederholt: erneuter Sprung um {Delta} von {OldTime}", delta,
            _clock.CurrentTime);
        _clock.Jump(delta);
        _jumpUndoStack.Push(delta);
        CanUndoJump = true;
        CanRedoJump = _jumpRedoStack.Count > 0;
        RecordSessionEvent("Zeitsprung wiederholt");
    }

    // ==================== Sprungmarken ====================

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
            ClockErrorMessage = "Ungültige Uhrzeit für neue Marke (Format hh:mm)";
            return;
        }

        ClockErrorMessage = null;

        var model = new JumpMarker
        {
            Name = string.IsNullOrWhiteSpace(NewMarkerName) ? "Marke" : NewMarkerName,
            TimeOfDay = parsed
        };
        JumpMarkers.Add(new JumpMarkerItemViewModel(model, () => _clock.CurrentTime, JumpBy, RemoveMarker));
        NewMarkerName = "Neue Marke";
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
            ClockErrorMessage = "Ungültige Timer-Dauer. Bitte hh:mm:ss verwenden, z.B. 00:10:00.";
            return;
        }

        ClockErrorMessage = null;

        var model = new TimerItem
        {
            Name = string.IsNullOrWhiteSpace(NewTimerBasics.Name) ? "Timer" : NewTimerBasics.Name,
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
        Log.Information("Timer angelegt: {Name} (Dauer {Duration}, Id={Id})", vm.Name, duration, vm.Id);
        ShowActionStatus($"Timer \"{vm.Name}\" angelegt");

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
        Log.Information("Timer entfernt: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Intervall-/OnTime-Objekte ====================

    [RelayCommand]
    private void AddIntervalEvent()
    {
        if (!TimeSpan.TryParse(NewIntervalInterval, out var interval) || interval <= TimeSpan.Zero)
            interval = ComposeNewTimeSpan(NewIntervalIntervalHours, NewIntervalIntervalMinutes,
                NewIntervalIntervalSeconds);

        if (interval <= TimeSpan.Zero)
        {
            ClockErrorMessage = "Ungültiges Intervall. Bitte hh:mm:ss verwenden, z.B. 00:10:00.";
            return;
        }

        if (!TimeSpan.TryParse(NewIntervalActiveDuration, out var activeDuration) || activeDuration <= TimeSpan.Zero)
            activeDuration =
                ComposeNewTimeSpan(NewIntervalActiveHours, NewIntervalActiveMinutes, NewIntervalActiveSeconds);

        if (activeDuration <= TimeSpan.Zero)
        {
            ClockErrorMessage = "Ungültige Aktiv-Dauer. Bitte hh:mm:ss verwenden, z.B. 00:01:00.";
            return;
        }

        if (activeDuration > interval)
        {
            ClockErrorMessage = "Die Aktiv-Dauer sollte nicht größer als das Intervall sein.";
            return;
        }

        int? maxRepeats = null;
        if (!string.IsNullOrWhiteSpace(NewIntervalMaxRepeats))
        {
            if (!int.TryParse(NewIntervalMaxRepeats, out var parsedMax) || parsedMax < 0)
            {
                ClockErrorMessage = "Ungültige maximale Wiederholungszahl (0 oder leer = unbegrenzt)";
                return;
            }

            maxRepeats = parsedMax == 0 ? null : parsedMax;
        }

        ClockErrorMessage = null;

        var model = new IntervalEventItem
        {
            Name = string.IsNullOrWhiteSpace(NewIntervalBasics.Name) ? "Intervall" : NewIntervalBasics.Name,
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
        Log.Information("OnTime-Intervall angelegt: {Name} (alle {Interval}, aktiv für {Active}, Id={Id})", vm.Name,
            interval, activeDuration, vm.Id);
        ShowActionStatus($"OnTime-Intervall \"{vm.Name}\" angelegt");

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
        Log.Information("OnTime-Intervall entfernt: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Wecker ====================

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
                ClockErrorMessage = "Ungültiges Wecker-Datum. Bitte JJJJ-MM-TT HH:mm:ss verwenden.";
                return;
            }
        }

        TimeSpan? repeat = null;
        if (!string.IsNullOrWhiteSpace(NewAlarmRepeatInterval))
        {
            if (!TimeSpan.TryParse(NewAlarmRepeatInterval, out var parsedRepeat) || parsedRepeat <= TimeSpan.Zero)
            {
                ClockErrorMessage = "Ungültiges Wiederhol-Intervall. Bitte hh:mm:ss verwenden oder leer lassen.";
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
            Name = string.IsNullOrWhiteSpace(NewAlarmBasics.Name) ? "Wecker" : NewAlarmBasics.Name,
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
        Log.Information("Wecker angelegt: {Name} (Zielzeit {TriggerAt}, Id={Id})", vm.Name, triggerAt, vm.Id);
        ShowActionStatus($"Wecker \"{vm.Name}\" angelegt");

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
        Log.Information("Wecker entfernt: {Name} (Id={Id})", vm.Name, vm.Id);
    }

    // ==================== Uhr-Tick (normal & Zeitsprung) ====================

    private void OnClockTick(DateTime newTime, TimeSpan gameDelta)
    {
        var previousTime = newTime - gameDelta;
        CurrentGameTimeText = FormatGameTime(newTime);
        OnPropertyChanged(nameof(NextEventJumpLabel));
        ManualDateTimeText = newTime.ToString("yyyy-MM-dd HH:mm:ss");
        UpdateAmbience();

        // gameDelta kann hier auch negativ sein (Rückwärtssprung) - Timer
        // reagieren nur, wenn sie aktiv laufen; Wecker gleichen sich anhand
        // des absoluten Zeitpunkts ab (können sich dadurch auch "entschärfen").
        foreach (var timer in Timers)
        {
            if (timer.Advance(gameDelta))
            {
                PlaySound(timer.Id, timer.SoundToPlay, timer.SoundRepeatCountToPlay);
                TriggerEventMedia(timer.TriggerMedia);
                RecordSessionEvent($"Timer abgelaufen: {timer.Name}");
            }

            CheckHeadsUpWarning(timer.Id, timer.Name, "Timer", timer.TimeUntilNextEvent);
        }

        foreach (var alarm in Alarms)
        {
            if (alarm.Sync(newTime))
            {
                PlaySound(alarm.Id, alarm.SoundToPlay, alarm.SoundRepeatCountToPlay);
                TriggerEventMedia(alarm.TriggerMedia);
                RecordSessionEvent($"Wecker ausgelöst: {alarm.Name}");
            }

            CheckHeadsUpWarning(alarm.Id, alarm.Name, "Wecker", alarm.TimeUntilNextEvent);
        }

        foreach (var intervalEvent in IntervalEvents)
        {
            if (intervalEvent.Advance(gameDelta))
            {
                PlaySound(intervalEvent.Id, intervalEvent.SoundToPlay, intervalEvent.SoundRepeatCountToPlay);
                TriggerEventMedia(intervalEvent.TriggerMedia);
                RecordSessionEvent($"OnTime aktiv: {intervalEvent.Name}");
            }

            CheckHeadsUpWarning(intervalEvent.Id, intervalEvent.Name, "OnTime", intervalEvent.TimeUntilNextEvent);
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
            RecordSessionEvent($"Kalender-Eintrag ausgelöst: {definition.Title}");
            ShowActionStatus($"Kalender: \"{definition.Title}\"");
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

        // Ein Sound-Bibliothek-Eintrag (statt eines Built-ins wie Pling) wird an alle Spieler
        // gesendet statt nur lokal beim SL abgespielt - siehe PlayLibrarySoundForItemAsync.
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
    ///     Sendet einen von einem Timer/Wecker/Intervall ausgelösten Sound-Bibliothek-Sound an alle
    ///     Spieler (und ggf. lokal beim SL, siehe SendSoundAsync). Loop=true (SoundRepeatCount==0)
    ///     entspricht der "endlos"-Semantik der Built-in-Sounds; eine feste Wiederholungszahl &gt; 0
    ///     sendet den Sound bewusst nur EINMAL (kein Nachbilden des Built-in-Wiederholung-mit-Pause-
    ///     Verhaltens für potenziell längere Bibliothekssounds).
    /// </summary>
    private async Task PlayLibrarySoundForItemAsync(Guid itemId, string name, string localPath, bool loop)
    {
        if (!File.Exists(localPath))
        {
            Log.Warning("Element-Sound (Bibliothek) nicht gefunden: {Name} ({LocalPath})", name, localPath);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(localPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Element-Sound (Bibliothek) konnte nicht gelesen werden: {Name}", name);
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
    ///     Bricht eine laufende unendliche Sound-Wiederholung (Built-in) bzw. einen laufenden
    ///     Sound-Bibliothek-Sound für dieses Element ab, falls vorhanden - sonst ein No-Op.
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
    ///     Baut den Exporttext; das eigentliche Speichern (Ziel wählbar) übernimmt der Code-Behind, siehe
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

        // Größer als beim letzten Tick (oder erstmals gesehen) heißt: neu gestartet/zurückgesetzt/
        // erneuter Wecker-Zyklus - die Vorwarnung für DIESE Occurrence darf wieder auslösen.
        if (!_headsUpLastRemaining.TryGetValue(id, out var previous) || value > previous) _headsUpFired.Remove(id);
        _headsUpLastRemaining[id] = value;

        if (!HeadsUpWarningEnabled) return;

        var leadTime = TimeSpan.FromMinutes((double)Math.Max(0, HeadsUpLeadMinutes));
        if (leadTime <= TimeSpan.Zero) return;

        if (_headsUpFired.Contains(id)) return;
        if (value <= TimeSpan.Zero || value > leadTime) return;

        _headsUpFired.Add(id);
        ShowHeadsUpMessage($"Gleich: {kindLabel} \"{name}\" (in {FormatTimeSpan(value)})");
    }

    private void ShowHeadsUpMessage(string message)
    {
        Log.Information("Vorwarnung: {Message}", message);
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
    ///     Für Datei-Dialog-Aktionen im Code-Behind (Speichern/Laden), die keinen Zugriff auf die private
    ///     ShowActionStatus haben.
    /// </summary>
    public void NotifyActionStatus(string message)
    {
        ShowActionStatus(message);
    }

    /// <summary>
    ///     Kurzer Erfolgs-Hinweis für abgeschlossene Aktionen (z.B. "Timer angelegt", "Medium
    ///     gesendet") - Fehler laufen weiterhin über ClockErrorMessage/MediaErrorMessage. Erscheint in
    ///     der Statusleiste (Notifications-Flyout), nicht mehr als eigene, reflow-anfällige Zeile.
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

    // ==================== Event-Medium (Timer/Wecker/Intervall lösen Bild/Video aus) ====================

    private void TriggerEventMedia(TriggerMediaConfig config)
    {
        if (!config.HasMedia || string.IsNullOrEmpty(config.Path) || !File.Exists(config.Path)) return;

        _ = SendTriggerMediaAsync(config);
    }

    private async Task SendTriggerMediaAsync(TriggerMediaConfig config)
    {
        var path = config.Path!;
        Log.Information(
            "Event-Medium ausgelöst: {Path} (Loop={Loop}, Fullscreen={Fullscreen}, PauseClock={PauseClock})",
            path, config.Loop, config.Fullscreen, config.PauseClockDuringVideo);
        RecordSessionEvent($"Event-Medium ausgelöst: {config.FileName}");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Event-Medium konnte nicht gelesen werden ({Path})", path);
            MediaErrorMessage = $"Event-Medium konnte nicht gesendet werden: {ex.Message}";
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
            // Sounds sind vom "aktuelles Medium"-Slot entkoppelt (siehe SendSoundAsync-Kommentar) -
            // kein SetCurrentMediaStatus/BeginVideoTracking, sonst würde ein Sound-Trigger ein
            // gerade angezeigtes Bild/Video ersetzen bzw. dessen Ende-Tracking kappen. Stattdessen
            // per _triggerSoundMediaIds an dieses Event gebunden, damit StopMediaIfTriggeredBy ihn
            // beim Reset des auslösenden Timers/Weckers/Intervalls mitbeendet.
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
    ///     Zentraler Versandweg für JEDEN Sound (Ad-hoc, Bibliothek, Event-Trigger, Item-Sound): sendet
    ///     über das Netzwerk, spielt optional lokal beim SL vor, und trackt ihn im "Aktuell abgespielt"-
    ///     Panel. localPath/deleteLocalAfterPlayback steuern nur die lokale SL-Vorschau (siehe
    ///     PlayLocalSoundIfNeeded); der Netzwerkversand läuft davon unabhängig immer.
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
    ///     Entfernt einen Sound aus dem Panel + allen Event/Item-Assoziationen - Sammelstelle für
    ///     jeden Weg, wie ein Sound endet (natürliches Ende, manueller Stopp, "Alle stoppen").
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
    ///     SL-seitiger Stopp eines einzelnen Sounds: beendet ihn bei allen Spieler-Clients
    ///     (media.stopSound) UND einer evtl. lokalen SL-Vorschau, dann Aufräumen im Panel.
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
        var shouldPlayLocally = IsPlayerWindowOpen && (!IsNetworkServerRunning || ConnectedClientCount == 0);
        if (!shouldPlayLocally)
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
            Log.Error(ex, "Sound konnte nicht lokal abgespielt werden: {Path}", localPath);
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

        // Nur relevant, wenn der Sound NICHT auch remote lief (sonst entfernt die
        // ClientReportedPlaybackEnded-Rückmeldung den Panel-Eintrag bereits) - RemoveActiveSound
        // ist idempotent, ein doppelter Aufruf schadet also nicht.
        RemoveActiveSound(mediaId);
    }

    /// <summary>
    ///     Wenn der letzte Spieler-Client die Verbindung verliert (oder der Server stoppt), sind
    ///     alle rein remote laufenden Sounds nicht mehr erreichbar - der Client stoppt sie zwar selbst
    ///     (siehe ClientMainWindowViewModel.StopAllSounds), aber das SL-Panel muss das nachvollziehen.
    /// </summary>
    private void ClearRemoteOnlyActiveSounds()
    {
        foreach (var sound in ActivePlayingSounds.Where(s => !_activeLocalSoundPlayers.ContainsKey(s.MediaId)).ToList())
            RemoveActiveSound(sound.MediaId);
    }

    /// <summary>
    ///     Schließt die aktuell gezeigte Medienanzeige, aber NUR wenn sie von genau diesem
    ///     Timer/Wecker/Intervall ausgelöst wurde - z.B. wenn ein Timer, dessen Ablauf gerade ein
    ///     Bild/Video angezeigt hat, zurückgesetzt wird. Ein Medium, das per Ad-hoc-Senden oder aus
    ///     der Bibliothek gezeigt wird, bleibt davon unberührt.
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
    ///     Nach dem Schließen eines Event-Mediums (Timer/Wecker/Intervall-Reset, siehe oben):
    ///     zeigt automatisch wieder das zuletzt aktive Galerie-Element, statt den Bildschirm leer zu
    ///     lassen - Event-Medien unterbrechen die Galerie nur vorübergehend (Vorrang), siehe
    ///     design-decisions.md. Ein No-Op, wenn das Element inzwischen aus der Galerie entfernt wurde.
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

        // NUR Video: Sounds werden komplett unabhängig vom Bild/Video-"aktuelles Medium"-Slot
        // gehandhabt (siehe PlayLocalSoundIfNeeded) - sonst würde das Ende eines Sounds hier
        // fälschlich ClearMediaCommand auslösen und ein gerade angezeigtes Bild/Video schließen.
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
    ///     Sicherheitsnetz: falls kein Client eine echte playbackEnded-Meldung schickt, nicht für immer pausiert/offen
    ///     bleiben.
    /// </summary>
    private async Task RunFallbackTimeoutAsync(string mediaId, string localPath, CancellationToken token)
    {
        var duration = await TryGetMediaDurationAsync(localPath) ?? TimeSpan.FromMinutes(10);
        // Puffer über die geschätzte Dauer hinaus, damit im Normalfall die echte
        // Client-Rückmeldung zuerst gewinnt (die auch Netzwerk-/Pufferzeit mit einschließt).
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

    // TimelineItems umschließt jeden Timer/Alarm/Intervall 1:1 (siehe AddTimelineItem) und
    // kaskadiert RefreshBlinkState() bereits an das jeweils gewrappte VM weiter - eine separate
    // Iteration über Timers/Alarms/IntervalEvents würde jedes Objekt doppelt aktualisieren.
    private void RefreshBlinkStates()
    {
        foreach (var item in TimelineItems) item.RefreshBlinkState();
    }

    private static string FormatGameTime(DateTime time)
    {
        return time.ToString("dddd, dd.MM.yyyy — HH:mm:ss");
    }

    // ==================== Speichern & Laden ====================

    /// <summary>Erstellt den kompletten Zustand als JSON-Text (für den Datei-Export).</summary>
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
        Log.Information("Spielstand exportiert: {TimerCount} Timer, {AlarmCount} Wecker, {IntervalCount} Intervalle",
            dto.Timers.Count, dto.Alarms.Count, dto.IntervalEvents.Count);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Stellt den kompletten Zustand aus zuvor exportiertem JSON-Text wieder her.</summary>
    public void ImportStateFromJson(string json)
    {
        AppStateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppStateDto>(json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spielstand konnte nicht geparst werden");
            ClockErrorMessage = $"Datei konnte nicht gelesen werden: {ex.Message}";
            return;
        }

        if (dto is null)
        {
            Log.Warning("Spielstand-Import abgelehnt: Datei enthält keinen gültigen Spielstand");
            ClockErrorMessage = "Datei enthält keinen gültigen Spielstand.";
            return;
        }

        Log.Information("Spielstand wird geladen: {TimerCount} Timer, {AlarmCount} Wecker, {IntervalCount} Intervalle",
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
        // Sprünge aus einer vorherigen Session/Datei rückgängig zu machen ergäbe nach dem Laden
        // keinen Sinn mehr (bezieht sich auf einen anderen Timer-/Wecker-Zustand) - Undo/Redo-
        // Historie ist bewusst rein session-lokal, siehe _jumpUndoStack-Kommentar.
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

        // Ein Laden ist ein Komplett-Ersatz des Zustands (inkl. Löschen alter Items) - dafür
        // ist ein einzelner voller Resync an alle Clients einfacher und robuster als für jede
        // Einzeländerung ein separates Delta zu senden.
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
            ClockErrorMessage = $"Kalender konnte nicht gelesen werden: {ex.Message}";
            return;
        }

        if (dto is null)
        {
            ClockErrorMessage = "Datei enthält keinen gültigen Kalender.";
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

    /// <summary>Voller Zustand für neu verbindende Clients bzw. einen expliziten Voll-Resync (Laden).</summary>
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
    ///     Begleitet den periodischen Heartbeat: korrigiert Drift der lokal abgeleiteten
    ///     Client-Uhr und heilt einen evtl. wegen skipIfBusy verworfenen clock.*-Event aus.
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

    private sealed class CalendarExportDto
    {
        public int Version { get; init; } = 1;
        public List<CalendarEntryDefinition> Entries { get; init; } = [];
    }
}