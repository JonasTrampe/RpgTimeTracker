using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

public partial class AlarmItemViewModel : ObservableObject
{
    private readonly AlarmItem _model;
    private readonly Action<AlarmItemViewModel> _onDeleteRequested;

    [ObservableProperty] private bool _blink;

    [ObservableProperty] private string _colorHex;

    private GameInstant _currentGameTime;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string _icon;

    [ObservableProperty] private bool _isPlayerVisible;

    [ObservableProperty] private bool _isRepeating;

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _repeatIntervalTextEdit;

    [ObservableProperty] private string _sound;

    [ObservableProperty] private int _soundRepeatCount;

    [ObservableProperty] private string _triggerAtDateTimeTextEdit;

    public AlarmItemViewModel(AlarmItem model, GameInstant currentGameTime, Action<AlarmItemViewModel> onDeleteRequested)
    {
        _model = model;
        _onDeleteRequested = onDeleteRequested;
        _currentGameTime = currentGameTime;
        _name = model.Name;
        _icon = VisualItemHelper.NormalizeIcon(model.Icon);
        _model.Icon = _icon;
        _sound = string.IsNullOrWhiteSpace(model.Sound) ? SoundService.Pling : model.Sound;
        _soundRepeatCount = model.SoundRepeatCount < 0 ? 0 : model.SoundRepeatCount;
        _colorHex = model.ColorHex;
        _blink = model.Blink;
        _isPlayerVisible = model.IsPlayerVisible;
        _triggerAtDateTimeTextEdit = CalendarService.Active.FormatDateTimeText(model.TriggerAt);
        _repeatIntervalTextEdit =
            model.RepeatInterval.HasValue ? FormatTimeSpan(model.RepeatInterval.Value) : string.Empty;
        _isRepeating = model.RepeatInterval.HasValue;
        _model.Triggered += OnModelTriggered;
    }

    public Guid Id => _model.Id;

    /// <summary>Optional image/video that is automatically distributed when this alarm triggers.</summary>
    public TriggerMediaConfig TriggerMedia { get; } = new();

    /// <summary>Phase 4 of the Scenes/Tags/Calendars project: if set, firing this Alarm also
    ///     activates the named Scene - see MainWindowViewModel.ActivateSceneById.</summary>
    [ObservableProperty] private Guid? _targetSceneId;

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public string TriggerAtText
    {
        get
        {
            var date = CalendarService.Active.ToCalendarDate(_model.TriggerAt);
            return $"{date.WeekdayName}, {date.Day:00}.{date.MonthIndex + 1:00}.{date.Year:0000} {date.Hour:00}:{date.Minute:00}";
        }
    }

    public string RepeatIntervalDisplay => _model.RepeatInterval.HasValue
        ? FormatTimeSpan(_model.RepeatInterval.Value)
        : LocalizationService.Get("AlarmItem.RepeatOnce");

    public bool IsTriggered => _model.IsTriggered;

    public string TimeRemainingText
    {
        get
        {
            var remaining = _model.TimeRemaining(_currentGameTime);
            return _model.IsOverdue(_currentGameTime) || _model.IsTriggered
                ? LocalizationService.Get("AlarmItem.NowLabel")
                : FormatTimeSpan(remaining);
        }
    }

    public IBrush? ItemBorderBrush => IsBlinkActive
        ? VisualItemHelper.TryBrush(ColorHex) ?? Brushes.OrangeRed
        : VisualItemHelper.TryBrush(ColorHex);

    public bool IsBlinkActive => Blink && IsTriggered && VisualItemHelper.BlinkFrame();
    public string SoundToPlay => _model.Sound;
    public int SoundRepeatCountToPlay => _model.SoundRepeatCount;
    public TimeSpan? TimeUntilNextEvent => _model.IsTriggered ? null : _model.TimeRemaining(_currentGameTime);

    /// <summary>Called by MainWindowViewModel on LocalizationService.LanguageChanged (see LibraryItemViewModelBase.RefreshLocalizedText for the same pattern).</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Fires on discrete state changes (edited/triggered/dismissed), not on every clock tick.</summary>
    public event Action? StateChanged;

    /// <summary>
    ///     Fires on Dismiss() - MainWindowViewModel then stops a possibly infinitely repeating sound loop
    ///     of this alarm.
    /// </summary>
    public event Action? ResetRequested;

    partial void OnNameChanged(string value)
    {
        _model.Name = value;
    }

    partial void OnIconChanged(string value)
    {
        var normalized = VisualItemHelper.NormalizeIcon(value);
        _model.Icon = normalized;
        if (Icon != normalized)
        {
            Icon = normalized;
            return;
        }

        OnPropertyChanged(nameof(IconGeometry));
    }

    partial void OnSoundChanged(string value)
    {
        _model.Sound = string.IsNullOrWhiteSpace(value) ? SoundService.Pling : value;
    }

    partial void OnSoundRepeatCountChanged(int value)
    {
        _model.SoundRepeatCount = value < 0 ? 0 : value;
    }

    partial void OnColorHexChanged(string value)
    {
        _model.ColorHex = value ?? string.Empty;
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    partial void OnIsPlayerVisibleChanged(bool value)
    {
        _model.IsPlayerVisible = value;
        StateChanged?.Invoke();
    }

    partial void OnBlinkChanged(bool value)
    {
        _model.Blink = value;
        OnPropertyChanged(nameof(IsBlinkActive));
    }

    [RelayCommand]
    private void ApplyEdits()
    {
        if (!CalendarService.Active.TryParseDateTimeText(TriggerAtDateTimeTextEdit, out var triggerAt))
        {
            ErrorMessage = LocalizationService.Get("AlarmItem.ErrorInvalidDate");
            return;
        }

        // A zero/blank repeat interval means "one-time alarm", not an error - see AddAlarm's
        // matching fix for why (the AllowEmpty TimeSpanInput can show "00:00:00" instead of a
        // true empty string). Only a string that fails to parse at all should block the edit.
        TimeSpan? repeat = null;
        if (!string.IsNullOrWhiteSpace(RepeatIntervalTextEdit))
        {
            if (!TimeSpan.TryParse(RepeatIntervalTextEdit, out var parsedRepeat))
            {
                ErrorMessage = LocalizationService.Get("AlarmItem.ErrorInvalidRepeatInterval");
                return;
            }

            if (parsedRepeat > TimeSpan.Zero) repeat = parsedRepeat;
        }

        _model.TriggerAt = triggerAt;
        _model.RepeatInterval = repeat;
        _model.Reset(_currentGameTime);
        IsRepeating = repeat.HasValue;
        ErrorMessage = null;
        RefreshAll();
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _model.Reset(_currentGameTime);
        RefreshAll();
        StateChanged?.Invoke();
        ResetRequested?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    public bool Sync(GameInstant currentGameTime)
    {
        _currentGameTime = currentGameTime;
        var triggeredNow = _model.SyncToTime(currentGameTime);
        RefreshAll();
        return triggeredNow;
    }

    public AlarmDto ToDto()
    {
        return new AlarmDto
        {
            Name = _model.Name,
            Icon = _model.Icon,
            Sound = _model.Sound,
            SoundRepeatCount = _model.SoundRepeatCount,
            ColorHex = _model.ColorHex,
            Blink = _model.Blink,
            IsPlayerVisible = _model.IsPlayerVisible,
            TriggerAtSeconds = _model.TriggerAt.TotalSeconds,
            RepeatIntervalTicks = _model.RepeatInterval?.Ticks,
            IsTriggered = _model.IsTriggered,
            TriggerMediaPath = TriggerMedia.Path,
            TriggerMediaFileName = TriggerMedia.FileName,
            TriggerMediaKind = TriggerMedia.Kind == MediaKind.None ? null : TriggerMedia.Kind.ToString(),
            TriggerMediaFullscreen = TriggerMedia.Fullscreen,
            TriggerMediaPauseClock = TriggerMedia.PauseClockDuringVideo,
            TriggerMediaLoop = TriggerMedia.Loop,
            TargetSceneId = TargetSceneId
        };
    }

    public static AlarmItemViewModel FromDto(AlarmDto dto, GameInstant currentGameTime,
        Action<AlarmItemViewModel> onDeleteRequested)
    {
        var model = new AlarmItem
        {
            Name = dto.Name,
            Icon = VisualItemHelper.NormalizeIcon(dto.Icon),
            Sound = string.IsNullOrWhiteSpace(dto.Sound) ? SoundService.Pling : dto.Sound,
            SoundRepeatCount = dto.SoundRepeatCount < 0 ? 0 : dto.SoundRepeatCount,
            ColorHex = dto.ColorHex ?? string.Empty,
            Blink = dto.Blink,
            IsPlayerVisible = dto.IsPlayerVisible,
            TriggerAt = new GameInstant(dto.TriggerAtSeconds),
            RepeatInterval = dto.RepeatIntervalTicks.HasValue ? TimeSpan.FromTicks(dto.RepeatIntervalTicks.Value) : null
        };
        model.Restore(dto.IsTriggered);
        var vm = new AlarmItemViewModel(model, currentGameTime, onDeleteRequested) { TargetSceneId = dto.TargetSceneId };
        if (!string.IsNullOrWhiteSpace(dto.TriggerMediaPath) &&
            Enum.TryParse<MediaKind>(dto.TriggerMediaKind, out var kind) && kind != MediaKind.None)
        {
            vm.TriggerMedia.Path = dto.TriggerMediaPath;
            vm.TriggerMedia.FileName = dto.TriggerMediaFileName;
            vm.TriggerMedia.Kind = kind;
            vm.TriggerMedia.Fullscreen = dto.TriggerMediaFullscreen;
            vm.TriggerMedia.PauseClockDuringVideo = dto.TriggerMediaPauseClock;
            vm.TriggerMedia.Loop = dto.TriggerMediaLoop;
        }

        return vm;
    }

    private void OnModelTriggered()
    {
        RefreshAll();
        StateChanged?.Invoke();
    }

    public void RefreshBlinkState()
    {
        OnPropertyChanged(nameof(IsBlinkActive));
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(TriggerAtText));
        OnPropertyChanged(nameof(RepeatIntervalDisplay));
        OnPropertyChanged(nameof(IsTriggered));
        OnPropertyChanged(nameof(TimeRemainingText));
        OnPropertyChanged(nameof(ItemBorderBrush));
        OnPropertyChanged(nameof(IsBlinkActive));
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}.{ts:hh\\:mm\\:ss}"
            : $"{ts:hh\\:mm\\:ss}";
    }
}