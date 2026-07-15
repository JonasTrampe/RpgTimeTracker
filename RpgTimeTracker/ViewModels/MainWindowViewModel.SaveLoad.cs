using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
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
            CurrentGameTimeSeconds = _clock.CurrentTime.TotalSeconds,
            ActiveCalendar = CalendarService.Active,
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

    /// <summary>
    ///     Restores state from a .rtt-save zip container, or an old plain-JSON save
    ///     (see SaveFileArchive.Unwrap for the upgrade-path fallback).
    /// </summary>
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
            ClockErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"),
                ex.Message);
            return;
        }

        if (dto is null)
        {
            Log.Warning("Game state import rejected: file does not contain a valid game state");
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidGameState");
            return;
        }

        // Version 5 replaced DateTime-based game time with the calendar-agnostic GameInstant -
        // a breaking, unmigrated format change (see AppStateDto.Version's doc comment). An older
        // save's "CurrentGameTime" field doesn't exist in this schema, so without this guard it
        // would silently deserialize as CurrentGameTimeSeconds=0 instead of failing loudly.
        if (dto.Version < 5)
        {
            Log.Warning("Game state import rejected: save format version {Version} predates the calendar rework " +
                        "and cannot be loaded", dto.Version);
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.SaveFormatTooOld");
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

        CalendarService.Active = dto.ActiveCalendar;
        var currentGameTime = new GameInstant(dto.CurrentGameTimeSeconds);
        _clock.SetTime(currentGameTime);
        // Undoing jumps from a previous session/file would no longer make sense after loading
        // (it refers to a different timer/alarm state) - undo/redo
        // history is deliberately purely session-local, see the _jumpUndoStack comment.
        _jumpUndoStack.Clear();
        _jumpRedoStack.Clear();
        CanUndoJump = false;
        CanRedoJump = false;
        CurrentGameTimeText = FormatGameTime(currentGameTime);
        ManualDateTimeText = CalendarService.Active.FormatDateTimeText(currentGameTime);
        // The "new alarm" target-time default is otherwise only set once at construction (see
        // MainWindowViewModel's constructor) and after adding an alarm - without this, it stays
        // frozen at whatever the clock's initial default time was, completely unrelated to the
        // just-loaded save's actual current game time.
        NewAlarmDateTime = CalendarService.Active.FormatDateTimeText(currentGameTime.Add(TimeSpan.FromHours(1)));
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
            var alarm = AlarmItemViewModel.FromDto(a, currentGameTime, RemoveAlarm);
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

        CalendarMonth = CalendarMonthStart(currentGameTime);
        CalendarSelectedDate = CalendarService.Active.DayStart(currentGameTime);
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
            ClockErrorMessage =
                string.Format(LocalizationService.Get("MainWindowViewModel.Errors.CalendarCouldNotBeRead"), ex.Message);
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

        CalendarMonth = CalendarMonthStart(_clock.CurrentTime);
        CalendarSelectedDate = CalendarService.Active.DayStart(_clock.CurrentTime);
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    /// <summary>Full state for newly connecting clients or an explicit full resync (load).</summary>
    private SessionSnapshotParams BuildSessionSnapshot()
    {
        return new SessionSnapshotParams
        {
            CurrentGameTimeSeconds = _clock.CurrentTime.TotalSeconds,
            ActiveCalendar = CalendarService.Active,
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
            CurrentGameTimeSeconds = _clock.CurrentTime.TotalSeconds,
            SpeedMultiplier = SpeedMultiplier,
            IsClockRunning = IsClockRunning
        };
    }

    private sealed class LibraryManifestEntryDto
    {
        /// <summary>
        ///     The item's original Id on the exporting machine - used only to build an
        ///     old-id -&gt; new-id map during import (see ImportMediaSectionFromZip/
        ///     ImportSoundSectionFromZip), so an NPC variant referencing this item in the same
        ///     bundle can be relinked to the freshly-imported copy. Never reused as the imported
        ///     item's actual Id (imports always mint fresh Guids, per this class's established
        ///     convention).
        /// </summary>
        public Guid Id { get; init; }

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

        /// <summary>See MapFloorEntryDto.Order.</summary>
        public int Order { get; init; }
    }

    private sealed class MapManifestDto
    {
        public string Name { get; init; } = string.Empty;
        public List<MapFloorManifestEntryDto> Floors { get; init; } = [];

        /// <summary>See MapItemViewModel.CurrentFormatVersion/FormatVersion.</summary>
        public int FormatVersion { get; init; } = MapItemViewModel.CurrentFormatVersion;
    }

    /// <summary>
    ///     Manifest for the "maps" section of a full-session export - unlike MapManifestDto (one
    ///     map per .rtt-map file), a session bundle needs all maps in the library at once.
    /// </summary>
    private sealed class MapLibraryManifestEntryDto
    {
        /// <summary>
        ///     Old-export Id, used only to relink a Scene's Map reference on import (see
        ///     ImportMapsSectionFromZip/ImportSceneSectionFromZip) - Guid.Empty on exports
        ///     predating this field, which just means no Scene can relink to it.
        /// </summary>
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;
        public List<MapFloorManifestEntryDto> Floors { get; } = [];

        /// <summary>See MapItemViewModel.CurrentFormatVersion/FormatVersion.</summary>
        public int FormatVersion { get; init; } = MapItemViewModel.CurrentFormatVersion;
    }

    private sealed class MapLibraryManifestDto
    {
        public List<MapLibraryManifestEntryDto> Maps { get; } = [];
    }

    /// <summary>
    ///     Manifest for the "npcs" section of a full-session/session export - NPCs own no
    ///     files themselves (see NpcLibraryItemViewModel's doc comment), so this just wraps the
    ///     same NpcLibraryEntryDto shape used for Shared/session-local library persistence; the
    ///     Image/TokenImage/Sound Ids it carries are relinked on import via ImportNpcSectionFromZip's
    ///     mediaIdMap/soundIdMap parameters.
    /// </summary>
    private sealed class NpcLibraryManifestDto
    {
        public List<NpcLibraryEntryDto> Npcs { get; init; } = [];
    }

    /// <summary>
    ///     Manifest for the "scenes" section of a full-session/session export - Scenes own
    ///     no files themselves (see SceneLibraryItemViewModel's doc comment), so this just wraps
    ///     the same SceneLibraryEntryDto shape used for Shared/session-local library persistence;
    ///     the Image/Map/Music/Sound Ids it carries are relinked on import via
    ///     ImportSceneSectionFromZip's mediaIdMap/soundIdMap/musicIdMap/mapIdMap parameters.
    /// </summary>
    private sealed class SceneLibraryManifestDto
    {
        public List<SceneLibraryEntryDto> Scenes { get; init; } = [];
    }

    private sealed class CalendarExportDto
    {
        public int Version { get; init; } = 1;
        public List<CalendarEntryDefinition> Entries { get; init; } = [];
    }
}