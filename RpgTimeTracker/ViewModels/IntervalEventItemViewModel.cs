using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

public partial class IntervalEventItemViewModel : ObservableObject
{
    private readonly IntervalEventItem _model;
    private readonly Action<IntervalEventItemViewModel> _onDeleteRequested;

    [ObservableProperty] private string _activeDurationText;

    [ObservableProperty] private bool _blink;

    [ObservableProperty] private string _colorHex;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string _icon;

    [ObservableProperty] private string _intervalText;

    [ObservableProperty] private bool _isPlayerVisible;

    [ObservableProperty] private string _maxRepeatsText;

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _sound;

    [ObservableProperty] private int _soundRepeatCount;

    public IntervalEventItemViewModel(IntervalEventItem model, Action<IntervalEventItemViewModel> onDeleteRequested)
    {
        _model = model;
        _onDeleteRequested = onDeleteRequested;
        _name = model.Name;
        _icon = VisualItemHelper.NormalizeIcon(model.Icon);
        _model.Icon = _icon;
        _sound = string.IsNullOrWhiteSpace(model.Sound) ? SoundService.Pling : model.Sound;
        _soundRepeatCount = model.SoundRepeatCount < 0 ? 0 : model.SoundRepeatCount;
        _colorHex = string.IsNullOrWhiteSpace(model.ColorHex) ? "#FFD45A" : model.ColorHex;
        _blink = model.Blink;
        _isPlayerVisible = model.IsPlayerVisible;
        _intervalText = FormatTimeSpan(model.Interval);
        _activeDurationText = FormatTimeSpan(model.ActiveDuration);
        _maxRepeatsText = model.MaxRepeats.HasValue && model.MaxRepeats.Value > 0
            ? model.MaxRepeats.Value.ToString()
            : string.Empty;
    }

    public Guid Id => _model.Id;

    /// <summary>Optionales Bild/Video, das automatisch verteilt wird, wenn dieses Intervall aktiv wird.</summary>
    public TriggerMediaConfig TriggerMedia { get; } = new();

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public bool IsRunning => _model.IsRunning;
    public bool IsCompleted => _model.IsCompleted;
    public bool IsActive => _model.IsActive;
    public bool IsBlinkActive => Blink && IsActive && VisualItemHelper.BlinkFrame();

    public string StartPauseLabel => _model.IsCompleted ? "Fertig" :
        _model.IsRunning ? "Pause" :
        _model.Elapsed > TimeSpan.Zero ? "Weiter" : "Start";

    public string RemainingText => _model.IsActive
        ? $"aktiv: {FormatTimeSpan(_model.Remaining)}"
        : $"nächster: {FormatTimeSpan(_model.Remaining)}";

    public string RepeatDisplay => _model.MaxRepeats.HasValue && _model.MaxRepeats.Value > 0
        ? $"{_model.CurrentRepeatNumber}/{_model.MaxRepeats.Value}"
        : $"{_model.CurrentRepeatNumber}/∞";

    public string DetailDisplay =>
        $"alle {FormatTimeSpan(_model.Interval)} für {FormatTimeSpan(_model.ActiveDuration)}";

    public double Progress => _model.Progress;

    public IBrush? ItemBorderBrush => IsBlinkActive
        ? VisualItemHelper.TryBrush(ColorHex) ?? Brushes.Gold
        : VisualItemHelper.TryBrush(ColorHex);

    public string SoundToPlay => _model.Sound;
    public int SoundRepeatCountToPlay => _model.SoundRepeatCount;
    public TimeSpan? TimeUntilNextEvent => _model.IsRunning && !_model.IsCompleted ? _model.Remaining : null;

    /// <summary>
    ///     Feuert bei diskreten Zustandsänderungen (bearbeitet/gestartet/pausiert/zurückgesetzt/fertig), nicht bei jedem
    ///     Uhr-Tick.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    ///     Feuert bei Reset() - MainWindowViewModel stoppt dann eine evtl. unendlich wiederholende Sound-Schleife dieses
    ///     Intervalls.
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
        if (!TimeSpan.TryParse(IntervalText, out var interval) || interval <= TimeSpan.Zero)
        {
            ErrorMessage = "Ungültiges Intervall";
            return;
        }

        if (!TimeSpan.TryParse(ActiveDurationText, out var activeDuration) || activeDuration <= TimeSpan.Zero)
        {
            ErrorMessage = "Ungültige Aktiv-Dauer";
            return;
        }

        if (activeDuration > interval)
        {
            ErrorMessage = "Aktiv-Dauer sollte nicht größer als Intervall sein.";
            return;
        }

        int? maxRepeats = null;
        if (!string.IsNullOrWhiteSpace(MaxRepeatsText))
        {
            if (!int.TryParse(MaxRepeatsText, out var parsedMax) || parsedMax < 0)
            {
                ErrorMessage = "Max. Wiederholungen: 0/leer = unbegrenzt";
                return;
            }

            maxRepeats = parsedMax == 0 ? null : parsedMax;
        }

        _model.Interval = interval;
        _model.ActiveDuration = activeDuration;
        _model.MaxRepeats = maxRepeats;
        ErrorMessage = null;
        RefreshAll();
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleRun()
    {
        ApplyEdits();
        if (!string.IsNullOrWhiteSpace(ErrorMessage)) return;

        if (_model.IsRunning)
            _model.Pause();
        else
            _model.Start();

        RefreshAll();
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        _model.Reset();
        ErrorMessage = null;
        RefreshAll();
        StateChanged?.Invoke();
        ResetRequested?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    public bool Advance(TimeSpan gameDelta)
    {
        var wasCompleted = _model.IsCompleted;
        var triggeredNow = _model.Advance(gameDelta);
        RefreshAll();

        // Terminale Zustandsänderung (MaxRepeats erreicht) muss dem Client mitgeteilt werden,
        // damit er nicht ewig ein Item als "läuft noch" fortrechnet - anders als die normale
        // aktiv/inaktiv-Zyklik, die der Client rein lokal aus Elapsed/Interval ableitet.
        if (!wasCompleted && _model.IsCompleted) StateChanged?.Invoke();

        return triggeredNow;
    }

    public IntervalEventDto ToDto()
    {
        return new IntervalEventDto
        {
            Name = _model.Name,
            Icon = _model.Icon,
            Sound = _model.Sound,
            SoundRepeatCount = _model.SoundRepeatCount,
            ColorHex = _model.ColorHex,
            Blink = _model.Blink,
            IsPlayerVisible = _model.IsPlayerVisible,
            IntervalTicks = _model.Interval.Ticks,
            ActiveDurationTicks = _model.ActiveDuration.Ticks,
            MaxRepeats = _model.MaxRepeats,
            ElapsedTicks = _model.Elapsed.Ticks,
            IsRunning = _model.IsRunning,
            IsCompleted = _model.IsCompleted,
            TriggerMediaPath = TriggerMedia.Path,
            TriggerMediaFileName = TriggerMedia.FileName,
            TriggerMediaKind = TriggerMedia.Kind == MediaKind.None ? null : TriggerMedia.Kind.ToString(),
            TriggerMediaFullscreen = TriggerMedia.Fullscreen,
            TriggerMediaPauseClock = TriggerMedia.PauseClockDuringVideo,
            TriggerMediaLoop = TriggerMedia.Loop
        };
    }

    public static IntervalEventItemViewModel FromDto(IntervalEventDto dto,
        Action<IntervalEventItemViewModel> onDeleteRequested)
    {
        var model = new IntervalEventItem
        {
            Name = dto.Name,
            Icon = VisualItemHelper.NormalizeIcon(dto.Icon),
            Sound = string.IsNullOrWhiteSpace(dto.Sound) ? SoundService.Pling : dto.Sound,
            SoundRepeatCount = dto.SoundRepeatCount < 0 ? 0 : dto.SoundRepeatCount,
            ColorHex = string.IsNullOrWhiteSpace(dto.ColorHex) ? "#FFD45A" : dto.ColorHex!,
            Blink = dto.Blink,
            IsPlayerVisible = dto.IsPlayerVisible,
            Interval = TimeSpan.FromTicks(dto.IntervalTicks <= 0 ? TimeSpan.FromMinutes(10).Ticks : dto.IntervalTicks),
            ActiveDuration = TimeSpan.FromTicks(dto.ActiveDurationTicks <= 0
                ? TimeSpan.FromMinutes(1).Ticks
                : dto.ActiveDurationTicks),
            MaxRepeats = dto.MaxRepeats
        };
        model.Restore(TimeSpan.FromTicks(dto.ElapsedTicks), dto.IsRunning, dto.IsCompleted);
        var vm = new IntervalEventItemViewModel(model, onDeleteRequested);
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

    public void RefreshBlinkState()
    {
        OnPropertyChanged(nameof(IsBlinkActive));
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(IconGeometry));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsBlinkActive));
        OnPropertyChanged(nameof(StartPauseLabel));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(RepeatDisplay));
        OnPropertyChanged(nameof(DetailDisplay));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}.{ts:hh\\:mm\\:ss}"
            : $"{ts:hh\\:mm\\:ss}";
    }
}