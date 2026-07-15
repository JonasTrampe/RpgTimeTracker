using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Shared display/edit row for timer, alarm, and on-time objects.
///     The actual domain view models remain separate; this class merely bundles them
///     for the combined lists in the GM and player windows.
/// </summary>
public partial class TimelineDisplayItemViewModel : ObservableObject, IPlayerTimelineEntry, ITaggable
{
    private readonly AlarmItemViewModel? _alarm;
    private readonly IntervalEventItemViewModel? _interval;
    private readonly TimerItemViewModel? _timer;

    [ObservableProperty] private bool _editBlink;

    [ObservableProperty] private string _editColorHex = string.Empty;

    [ObservableProperty] private bool _editIsPlayerVisible = true;

    [ObservableProperty] private string _editName = string.Empty;

    [ObservableProperty] private string _editSound = "Pling";

    [ObservableProperty] private int _editSoundRepeatCount = 1;

    private bool _editValuesInitialized;

    [ObservableProperty] private bool _isConfigVisible;

    public TimelineDisplayItemViewModel(TimerItemViewModel timer)
    {
        _timer = timer;
        _timer.PropertyChanged += SourceChanged;
    }

    public TimelineDisplayItemViewModel(AlarmItemViewModel alarm)
    {
        _alarm = alarm;
        _alarm.PropertyChanged += SourceChanged;
    }

    public TimelineDisplayItemViewModel(IntervalEventItemViewModel interval)
    {
        _interval = interval;
        _interval.PropertyChanged += SourceChanged;
    }

    public Guid Id => _timer?.Id ?? _alarm?.Id ?? _interval?.Id ?? Guid.Empty;

    public TriggerMediaConfig TriggerMedia => _timer?.TriggerMedia ?? _alarm?.TriggerMedia ?? _interval!.TriggerMedia;

    /// <summary>Timer-specific Tag Ids (see TimerTag), passed through to whichever domain view
    ///     model this wraps - lets the Elementliste's tag-filter bar/assignment flyout work
    ///     against this shared display row without knowing the concrete underlying type.</summary>
    public ObservableCollection<Guid> TagIds =>
        (_timer as ITaggable)?.TagIds ?? (_alarm as ITaggable)?.TagIds ?? ((ITaggable)_interval!).TagIds;

    /// <summary>Set by MainWindowViewModel for a Scene-owned timeline item (Phase 3 of the
    ///     Scenes/Tags/Calendars project) - null for a global Timer/Alarm/IntervalEvent. A
    ///     reference rather than a snapshot of the name, so renaming the Scene updates the label
    ///     shown in the Elementliste. Purely local UI state, not persisted (a Scene-owned item's
    ///     ownership is implicit from which Scene's Timers/Alarms/IntervalEvents collection it
    ///     lives in).</summary>
    public SceneLibraryItemViewModel? OwningScene { get; init; }

    public bool IsSceneOwned => OwningScene is not null;

    public string OwningSceneLabel => OwningScene is { } scene
        ? string.Format(LocalizationService.Get("MainWindow.ItemList.SceneOwnedFormat"), scene.Name)
        : string.Empty;

    /// <summary>Phase 4 of the Scenes/Tags/Calendars project: if set, firing this item also
    ///     activates the named Scene - see MainWindowViewModel.ActivateSceneById.</summary>
    public Guid? TargetSceneId
    {
        get => _timer?.TargetSceneId ?? _alarm?.TargetSceneId ?? _interval?.TargetSceneId;
        set
        {
            if (_timer is not null) _timer.TargetSceneId = value;
            else if (_alarm is not null) _alarm.TargetSceneId = value;
            else if (_interval is not null) _interval.TargetSceneId = value;
            OnPropertyChanged();
        }
    }

    public bool IsTimer => _timer is not null;
    public bool IsAlarm => _alarm is not null;
    public bool IsInterval => _interval is not null;

    public string Icon
    {
        get => _timer?.Icon ?? _alarm?.Icon ?? _interval?.Icon ?? VisualItemHelper.IconTimer;
        set
        {
            var normalized = VisualItemHelper.NormalizeIcon(value);
            if (_timer is not null) _timer.Icon = normalized;
            else if (_alarm is not null) _alarm.Icon = normalized;
            else if (_interval is not null) _interval.Icon = normalized;
            RefreshAll();
        }
    }

    public string Sound
    {
        get => _timer?.Sound ?? _alarm?.Sound ?? _interval?.Sound ?? "Pling";
        set
        {
            if (_timer is not null) _timer.Sound = value;
            else if (_alarm is not null) _alarm.Sound = value;
            else if (_interval is not null) _interval.Sound = value;
            RefreshAll();
        }
    }

    public int SoundRepeatCount
    {
        get => _timer?.SoundRepeatCount ?? _alarm?.SoundRepeatCount ?? _interval?.SoundRepeatCount ?? 1;
        set
        {
            if (_timer is not null) _timer.SoundRepeatCount = value;
            else if (_alarm is not null) _alarm.SoundRepeatCount = value;
            else if (_interval is not null) _interval.SoundRepeatCount = value;
            RefreshAll();
        }
    }

    public string ColorHex
    {
        get => _timer?.ColorHex ?? _alarm?.ColorHex ?? _interval?.ColorHex ?? string.Empty;
        set
        {
            if (_timer is not null) _timer.ColorHex = value;
            else if (_alarm is not null) _alarm.ColorHex = value;
            else if (_interval is not null) _interval.ColorHex = value;
            RefreshAll();
        }
    }

    public bool Blink
    {
        get => _timer?.Blink ?? _alarm?.Blink ?? _interval?.Blink ?? false;
        set
        {
            if (_timer is not null) _timer.Blink = value;
            else if (_alarm is not null) _alarm.Blink = value;
            else if (_interval is not null) _interval.Blink = value;
            RefreshAll();
        }
    }

    public bool IsPlayerVisible
    {
        get => _timer?.IsPlayerVisible ?? _alarm?.IsPlayerVisible ?? _interval?.IsPlayerVisible ?? true;
        set
        {
            if (_timer is not null) _timer.IsPlayerVisible = value;
            else if (_alarm is not null) _alarm.IsPlayerVisible = value;
            else if (_interval is not null) _interval.IsPlayerVisible = value;
            RefreshAll();
        }
    }

    public bool IsSlOnly => !IsPlayerVisible;

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;

    // Timer edit
    public string DurationText
    {
        get => _timer?.DurationText ?? string.Empty;
        set
        {
            if (_timer is not null) _timer.DurationText = value;
            RefreshAll();
        }
    }

    public bool CanEditDuration => _timer?.CanEditDuration ?? false;

    // Alarm edit
    public string TriggerAtDateTimeText
    {
        get => _alarm?.TriggerAtDateTimeTextEdit ?? string.Empty;
        set
        {
            if (_alarm is not null) _alarm.TriggerAtDateTimeTextEdit = value;
            RefreshAll();
        }
    }

    public string RepeatIntervalTextEdit
    {
        get => _alarm?.RepeatIntervalTextEdit ?? string.Empty;
        set
        {
            if (_alarm is not null) _alarm.RepeatIntervalTextEdit = value;
            RefreshAll();
        }
    }

    // OnTime edit
    public string IntervalText
    {
        get => _interval?.IntervalText ?? string.Empty;
        set
        {
            if (_interval is not null) _interval.IntervalText = value;
            RefreshAll();
        }
    }

    public string ActiveDurationText
    {
        get => _interval?.ActiveDurationText ?? string.Empty;
        set
        {
            if (_interval is not null) _interval.ActiveDurationText = value;
            RefreshAll();
        }
    }

    public string MaxRepeatsText
    {
        get => _interval?.MaxRepeatsText ?? string.Empty;
        set
        {
            if (_interval is not null) _interval.MaxRepeatsText = value;
            RefreshAll();
        }
    }


    public decimal DurationHoursValue
    {
        get => ParseDecimalPart(DurationText, 0);
        set => DurationText = ComposeTimeSpanText(value, DurationMinutesValue, DurationSecondsValue);
    }

    public decimal DurationMinutesValue
    {
        get => ParseDecimalPart(DurationText, 1);
        set => DurationText = ComposeTimeSpanText(DurationHoursValue, value, DurationSecondsValue);
    }

    public decimal DurationSecondsValue
    {
        get => ParseDecimalPart(DurationText, 2);
        set => DurationText = ComposeTimeSpanText(DurationHoursValue, DurationMinutesValue, value);
    }

    public decimal IntervalHoursValue
    {
        get => ParseDecimalPart(IntervalText, 0);
        set => IntervalText = ComposeTimeSpanText(value, IntervalMinutesValue, IntervalSecondsValue);
    }

    public decimal IntervalMinutesValue
    {
        get => ParseDecimalPart(IntervalText, 1);
        set => IntervalText = ComposeTimeSpanText(IntervalHoursValue, value, IntervalSecondsValue);
    }

    public decimal IntervalSecondsValue
    {
        get => ParseDecimalPart(IntervalText, 2);
        set => IntervalText = ComposeTimeSpanText(IntervalHoursValue, IntervalMinutesValue, value);
    }

    public decimal ActiveHoursValue
    {
        get => ParseDecimalPart(ActiveDurationText, 0);
        set => ActiveDurationText = ComposeTimeSpanText(value, ActiveMinutesValue, ActiveSecondsValue);
    }

    public decimal ActiveMinutesValue
    {
        get => ParseDecimalPart(ActiveDurationText, 1);
        set => ActiveDurationText = ComposeTimeSpanText(ActiveHoursValue, value, ActiveSecondsValue);
    }

    public decimal ActiveSecondsValue
    {
        get => ParseDecimalPart(ActiveDurationText, 2);
        set => ActiveDurationText = ComposeTimeSpanText(ActiveHoursValue, ActiveMinutesValue, value);
    }

    public decimal RepeatHoursValue
    {
        get => ParseDecimalPart(RepeatIntervalTextEdit, 0);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(value, RepeatMinutesValue, RepeatSecondsValue);
    }

    public decimal RepeatMinutesValue
    {
        get => ParseDecimalPart(RepeatIntervalTextEdit, 1);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(RepeatHoursValue, value, RepeatSecondsValue);
    }

    public decimal RepeatSecondsValue
    {
        get => ParseDecimalPart(RepeatIntervalTextEdit, 2);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(RepeatHoursValue, RepeatMinutesValue, value);
    }

    public string DurationHoursText
    {
        get => ExtractPart(DurationText, 0);
        set => DurationText = ComposeTimeSpanText(value, DurationMinutesText, DurationSecondsText);
    }

    public string DurationMinutesText
    {
        get => ExtractPart(DurationText, 1);
        set => DurationText = ComposeTimeSpanText(DurationHoursText, value, DurationSecondsText);
    }

    public string DurationSecondsText
    {
        get => ExtractPart(DurationText, 2);
        set => DurationText = ComposeTimeSpanText(DurationHoursText, DurationMinutesText, value);
    }

    public string IntervalHoursText
    {
        get => ExtractPart(IntervalText, 0);
        set => IntervalText = ComposeTimeSpanText(value, IntervalMinutesText, IntervalSecondsText);
    }

    public string IntervalMinutesText
    {
        get => ExtractPart(IntervalText, 1);
        set => IntervalText = ComposeTimeSpanText(IntervalHoursText, value, IntervalSecondsText);
    }

    public string IntervalSecondsText
    {
        get => ExtractPart(IntervalText, 2);
        set => IntervalText = ComposeTimeSpanText(IntervalHoursText, IntervalMinutesText, value);
    }

    public string ActiveHoursText
    {
        get => ExtractPart(ActiveDurationText, 0);
        set => ActiveDurationText = ComposeTimeSpanText(value, ActiveMinutesText, ActiveSecondsText);
    }

    public string ActiveMinutesText
    {
        get => ExtractPart(ActiveDurationText, 1);
        set => ActiveDurationText = ComposeTimeSpanText(ActiveHoursText, value, ActiveSecondsText);
    }

    public string ActiveSecondsText
    {
        get => ExtractPart(ActiveDurationText, 2);
        set => ActiveDurationText = ComposeTimeSpanText(ActiveHoursText, ActiveMinutesText, value);
    }

    public string RepeatHoursText
    {
        get => ExtractPart(RepeatIntervalTextEdit, 0);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(value, RepeatMinutesText, RepeatSecondsText);
    }

    public string RepeatMinutesText
    {
        get => ExtractPart(RepeatIntervalTextEdit, 1);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(RepeatHoursText, value, RepeatSecondsText);
    }

    public string RepeatSecondsText
    {
        get => ExtractPart(RepeatIntervalTextEdit, 2);
        set => RepeatIntervalTextEdit = ComposeTimeSpanText(RepeatHoursText, RepeatMinutesText, value);
    }

    public DateTimeOffset? TriggerDateTimeEdit
    {
        get => DateTime.TryParse(TriggerAtDateTimeText, out var parsed) ? new DateTimeOffset(parsed.Date) : null;
        set
        {
            if (value is null) return;
            var time = DateTime.TryParse(TriggerAtDateTimeText, out var parsed) ? parsed.TimeOfDay : TimeSpan.Zero;
            TriggerAtDateTimeText = value.Value.Date.Add(time).ToString("yyyy-MM-dd HH:mm:ss");
            RefreshAll();
        }
    }

    public string? ErrorMessage => _timer?.ErrorMessage ?? _alarm?.ErrorMessage ?? _interval?.ErrorMessage;

    public string StartPauseLabel => _timer?.StartPauseLabel ?? _interval?.StartPauseLabel ?? string.Empty;
    public bool CanStartPause => _timer is not null || _interval is not null;
    public bool CanReset => _timer is not null || _interval is not null;
    public bool CanDismiss => _alarm?.IsTriggered == true;

    public string KindLabel => IsTimer
        ? LocalizationService.Get("TimelineDisplayItem.KindTimer")
        : IsAlarm
            ? LocalizationService.Get("TimelineDisplayItem.KindAlarm")
            : LocalizationService.Get("TimelineDisplayItem.KindInterval");

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public string Name
    {
        get => _timer?.Name ?? _alarm?.Name ?? _interval?.Name ?? string.Empty;
        set
        {
            if (_timer is not null) _timer.Name = value;
            else if (_alarm is not null) _alarm.Name = value;
            else if (_interval is not null) _interval.Name = value;
            RefreshAll();
        }
    }

    public string PrimaryValue =>
        _timer?.RemainingText ??
        _alarm?.TimeRemainingText ??
        _interval?.RemainingText ??
        string.Empty;

    public string DetailText
    {
        get
        {
            if (_timer is not null) return $"Rest: {_timer.RemainingText} · Dauer: {_timer.DurationDisplay}";
            if (_alarm is not null) return $"{_alarm.TriggerAtText} · Wdh.: {_alarm.RepeatIntervalDisplay}";
            if (_interval is not null) return $"{_interval.DetailDisplay} · {_interval.RepeatDisplay}";
            return string.Empty;
        }
    }

    public string StatusText
    {
        get
        {
            if (_timer is not null)
            {
                if (_timer.IsCompleted) return LocalizationService.Get("TimelineDisplayItem.StatusExpired");
                return _timer.IsRunning
                    ? LocalizationService.Get("TimelineDisplayItem.StatusRunning")
                    : LocalizationService.Get("TimelineDisplayItem.StatusPaused");
            }

            if (_alarm is not null)
                return _alarm.IsTriggered
                    ? LocalizationService.Get("TimelineDisplayItem.StatusTriggered")
                    : LocalizationService.Get("TimelineDisplayItem.StatusReady");

            if (_interval is not null)
            {
                if (_interval.IsCompleted) return LocalizationService.Get("IntervalEvent.StatusDone");
                if (_interval.IsActive) return LocalizationService.Get("TimelineDisplayItem.StatusActive");
                return _interval.IsRunning
                    ? LocalizationService.Get("TimelineDisplayItem.StatusRunning")
                    : LocalizationService.Get("TimelineDisplayItem.StatusPaused");
            }

            return string.Empty;
        }
    }

    public bool IsActive => _timer?.IsRunning == true || _alarm?.IsTriggered == true || _interval?.IsActive == true;

    public bool IsCompleted =>
        _timer?.IsCompleted == true || _alarm?.IsTriggered == true || _interval?.IsCompleted == true;

    public bool IsBlinkActive => _timer?.IsBlinkActive == true || _alarm?.IsBlinkActive == true ||
                                 _interval?.IsBlinkActive == true;

    public IBrush? ItemBorderBrush => _timer?.ItemBorderBrush ?? _alarm?.ItemBorderBrush ?? _interval?.ItemBorderBrush;

    public double Progress => _timer?.Progress ?? _interval?.Progress ?? 0;

    public bool HasProgress => _timer is not null || _interval is not null;

    /// <summary>Called by MainWindowViewModel on LocalizationService.LanguageChanged (see LibraryItemViewModelBase.RefreshLocalizedText for the same pattern).</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }

    public bool Wraps(TimerItemViewModel timer)
    {
        return ReferenceEquals(_timer, timer);
    }

    public bool Wraps(AlarmItemViewModel alarm)
    {
        return ReferenceEquals(_alarm, alarm);
    }

    public bool Wraps(IntervalEventItemViewModel interval)
    {
        return ReferenceEquals(_interval, interval);
    }

    /// <summary>
    ///     Builds the network snapshot for timelineItem.upserted. Centralized in one place so that
    ///     both the full session.snapshot (newly connecting clients) and the targeted
    ///     delta upserts (on edit/start/pause/complete/...) use the same mapping.
    /// </summary>
    public TimelineItemSnapshotDto ToRpcSnapshot()
    {
        if (_timer is not null)
        {
            var dto = _timer.ToDto();
            return new TimelineItemSnapshotDto
            {
                Id = _timer.Id.ToString(),
                Kind = TimelineItemSnapshotDto.KindTimer,
                Name = dto.Name,
                Icon = dto.Icon,
                Sound = dto.Sound ?? string.Empty,
                ColorHex = dto.ColorHex ?? string.Empty,
                Blink = dto.Blink,
                IsPlayerVisible = dto.IsPlayerVisible,
                DurationTicks = dto.DurationTicks,
                ElapsedTicks = dto.ElapsedTicks,
                IsRunning = dto.IsRunning,
                IsCompleted = _timer.IsCompleted
            };
        }

        if (_alarm is not null)
        {
            var dto = _alarm.ToDto();
            return new TimelineItemSnapshotDto
            {
                Id = _alarm.Id.ToString(),
                Kind = TimelineItemSnapshotDto.KindAlarm,
                Name = dto.Name,
                Icon = dto.Icon,
                Sound = dto.Sound ?? string.Empty,
                ColorHex = dto.ColorHex ?? string.Empty,
                Blink = dto.Blink,
                IsPlayerVisible = dto.IsPlayerVisible,
                TriggerAtSeconds = dto.TriggerAtSeconds,
                RepeatIntervalTicks = dto.RepeatIntervalTicks,
                IsTriggered = dto.IsTriggered
            };
        }

        var idto = _interval!.ToDto();
        return new TimelineItemSnapshotDto
        {
            Id = _interval.Id.ToString(),
            Kind = TimelineItemSnapshotDto.KindInterval,
            Name = idto.Name,
            Icon = idto.Icon,
            Sound = idto.Sound ?? string.Empty,
            ColorHex = idto.ColorHex ?? string.Empty,
            Blink = idto.Blink,
            IsPlayerVisible = idto.IsPlayerVisible,
            IntervalTicks = idto.IntervalTicks,
            ActiveDurationTicks = idto.ActiveDurationTicks,
            MaxRepeats = idto.MaxRepeats,
            ElapsedTicks = idto.ElapsedTicks,
            IsRunning = idto.IsRunning,
            IsCompleted = idto.IsCompleted
        };
    }

    [RelayCommand]
    private void ToggleConfig()
    {
        if (!IsConfigVisible) LoadEditValuesFromItem();

        IsConfigVisible = !IsConfigVisible;
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (_timer is not null) _timer.ToggleRunCommand.Execute(null);
        else if (_interval is not null) _interval.ToggleRunCommand.Execute(null);
        RefreshAll();
    }

    [RelayCommand]
    private void Reset()
    {
        if (_timer is not null) _timer.ResetCommand.Execute(null);
        else if (_interval is not null) _interval.ResetCommand.Execute(null);
        RefreshAll();
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (_alarm is not null) _alarm.DismissCommand.Execute(null);
        RefreshAll();
    }

    [RelayCommand]
    private void ApplyEdits()
    {
        ApplyStagedEditValues();

        if (_timer is not null) _timer.ApplyEditsCommand.Execute(null);
        else if (_alarm is not null) _alarm.ApplyEditsCommand.Execute(null);
        else if (_interval is not null) _interval.ApplyEditsCommand.Execute(null);

        LoadEditValuesFromItem();
        RefreshAll();
    }

    [RelayCommand]
    private void Delete()
    {
        if (_timer is not null) _timer.DeleteCommand.Execute(null);
        else if (_alarm is not null) _alarm.DeleteCommand.Execute(null);
        else if (_interval is not null) _interval.DeleteCommand.Execute(null);
    }

    private void LoadEditValuesFromItem()
    {
        EditName = Name;
        EditSound = Sound;
        EditSoundRepeatCount = SoundRepeatCount;
        EditColorHex = ColorHex;
        EditBlink = Blink;
        EditIsPlayerVisible = IsPlayerVisible;
        _editValuesInitialized = true;
    }

    private void ApplyStagedEditValues()
    {
        if (!_editValuesInitialized) LoadEditValuesFromItem();

        Name = EditName;
        Sound = EditSound;
        SoundRepeatCount = EditSoundRepeatCount;
        ColorHex = EditColorHex;
        Blink = EditBlink;
        IsPlayerVisible = EditIsPlayerVisible;
    }

    public void RefreshBlinkState()
    {
        _timer?.RefreshBlinkState();
        _alarm?.RefreshBlinkState();
        _interval?.RefreshBlinkState();
        OnPropertyChanged(nameof(IsBlinkActive));
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IconGeometry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(SoundRepeatCount));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(Blink));
        OnPropertyChanged(nameof(IsPlayerVisible));
        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(EditSound));
        OnPropertyChanged(nameof(EditSoundRepeatCount));
        OnPropertyChanged(nameof(EditColorHex));
        OnPropertyChanged(nameof(EditBlink));
        OnPropertyChanged(nameof(EditIsPlayerVisible));
        OnPropertyChanged(nameof(IsSlOnly));
        OnPropertyChanged(nameof(PrimaryValue));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsBlinkActive));
        OnPropertyChanged(nameof(ItemBorderBrush));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(DurationHoursText));
        OnPropertyChanged(nameof(DurationMinutesText));
        OnPropertyChanged(nameof(DurationSecondsText));
        OnPropertyChanged(nameof(DurationHoursValue));
        OnPropertyChanged(nameof(DurationMinutesValue));
        OnPropertyChanged(nameof(DurationSecondsValue));
        OnPropertyChanged(nameof(CanEditDuration));
        OnPropertyChanged(nameof(TriggerAtDateTimeText));
        OnPropertyChanged(nameof(RepeatIntervalTextEdit));
        OnPropertyChanged(nameof(RepeatHoursText));
        OnPropertyChanged(nameof(RepeatMinutesText));
        OnPropertyChanged(nameof(RepeatSecondsText));
        OnPropertyChanged(nameof(RepeatHoursValue));
        OnPropertyChanged(nameof(RepeatMinutesValue));
        OnPropertyChanged(nameof(RepeatSecondsValue));
        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(IntervalHoursText));
        OnPropertyChanged(nameof(IntervalMinutesText));
        OnPropertyChanged(nameof(IntervalSecondsText));
        OnPropertyChanged(nameof(IntervalHoursValue));
        OnPropertyChanged(nameof(IntervalMinutesValue));
        OnPropertyChanged(nameof(IntervalSecondsValue));
        OnPropertyChanged(nameof(ActiveDurationText));
        OnPropertyChanged(nameof(ActiveHoursText));
        OnPropertyChanged(nameof(ActiveMinutesText));
        OnPropertyChanged(nameof(ActiveSecondsText));
        OnPropertyChanged(nameof(ActiveHoursValue));
        OnPropertyChanged(nameof(ActiveMinutesValue));
        OnPropertyChanged(nameof(ActiveSecondsValue));
        OnPropertyChanged(nameof(MaxRepeatsText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(TargetSceneId));
        OnPropertyChanged(nameof(StartPauseLabel));
        OnPropertyChanged(nameof(CanStartPause));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanDismiss));
    }

    private static string CombineDateAndTime(string dateText, string timeText)
    {
        var date = DateTime.TryParse(dateText, out var parsedDate) ? parsedDate.Date : DateTime.Today;
        var time = TimeSpan.TryParse(timeText, out var parsedTime) ? parsedTime : TimeSpan.Zero;
        return date.Add(time).ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string ExtractPart(string text, int part)
    {
        if (!TimeSpan.TryParse(text, out var parsed)) parsed = TimeSpan.Zero;

        var hours = (int)parsed.TotalHours;
        return part switch
        {
            0 => hours.ToString(),
            1 => parsed.Minutes.ToString(),
            _ => parsed.Seconds.ToString()
        };
    }

    private static string ComposeTimeSpanText(string hours, string minutes, string seconds)
    {
        var h = ParseNonNegativeInt(hours);
        var m = Math.Clamp(ParseNonNegativeInt(minutes), 0, 59);
        var s = Math.Clamp(ParseNonNegativeInt(seconds), 0, 59);
        return $"{h:00}:{m:00}:{s:00}";
    }

    private static string ComposeTimeSpanText(decimal hours, decimal minutes, decimal seconds)
    {
        var h = Math.Max(0, (int)hours);
        var m = Math.Clamp((int)minutes, 0, 59);
        var s = Math.Clamp((int)seconds, 0, 59);
        return $"{h:00}:{m:00}:{s:00}";
    }

    private static decimal ParseDecimalPart(string text, int part)
    {
        return decimal.TryParse(ExtractPart(text, part), out var parsed) ? parsed : 0m;
    }

    private static int ParseNonNegativeInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Blink ticks (every 500ms) already trigger RefreshBlinkState() for each timeline item,
        // which explicitly re-raises IsBlinkActive/ItemBorderBrush. Without this special case,
        // every blink tick would additionally trigger a full RefreshAll() (~50 notifications)
        // here per item, just to re-report the same two properties.
        if (e.PropertyName is nameof(IsBlinkActive) or nameof(ItemBorderBrush))
        {
            OnPropertyChanged(nameof(IsBlinkActive));
            OnPropertyChanged(nameof(ItemBorderBrush));
            return;
        }

        RefreshAll();
    }
}