using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        // Unlike Timer/IntervalEvent's duration fields, an unset/unparseable target time has no
        // single obvious numeric default - falls back to "1 hour from now" rather than blocking
        // the add, matching Timer/IntervalEvent's "always succeeds via a sensible default" behavior.
        if (!CalendarService.Active.TryParseDateTimeText(NewAlarmDateTime, out var triggerAt))
        {
            triggerAt = _clock.CurrentTime.Add(TimeSpan.FromHours(1));
            NewAlarmDateTime = CalendarService.Active.FormatDateTimeText(triggerAt);
        }

        // A zero/blank repeat interval is never a genuine user error - it just means "one-time
        // alarm", the same as leaving the field empty (the UI's AllowEmpty TimeSpanInput can end
        // up showing "00:00:00" instead of a true empty string depending on how it was
        // interacted with - see TimeSpanInput's doc comments). Only a string that fails to parse
        // at all (a real typo) should block the add.
        TimeSpan? repeat = null;
        if (!string.IsNullOrWhiteSpace(NewAlarmRepeatInterval))
        {
            if (!TimeSpan.TryParse(NewAlarmRepeatInterval, out var parsedRepeat))
            {
                ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidRepeatInterval");
                return;
            }

            if (parsedRepeat > TimeSpan.Zero) repeat = parsedRepeat;
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

        var nextAlarm = _clock.CurrentTime.Add(TimeSpan.FromHours(8));
        NewAlarmDateTime = CalendarService.Active.FormatDateTimeText(nextAlarm);
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

    private void OnClockTick(GameInstant newTime, TimeSpan gameDelta)
    {
        var previousTime = newTime.Add(-gameDelta);
        CurrentGameTimeText = FormatGameTime(newTime);
        OnPropertyChanged(nameof(NextEventJumpLabel));
        ManualDateTimeText = CalendarService.Active.FormatDateTimeText(newTime);
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

    private void TriggerCalendarEntries(GameInstant previousTime, GameInstant currentTime)
    {
        if (currentTime < previousTime)
            return;

        foreach (var entry in CalendarEntries)
        {
            if (!entry.TryBuildDefinition(out var definition))
                continue;

            var nextOccurrence = definition.GetNextOccurrenceAtOrAfter(CalendarService.Active, previousTime);
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
            Layer = MediaHeaderDto.LayerSound,
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

}
