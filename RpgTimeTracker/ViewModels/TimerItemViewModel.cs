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

public partial class TimerItemViewModel : ObservableObject
{
    private readonly TimerItem _model;
    private readonly Action<TimerItemViewModel> _onDeleteRequested;

    [ObservableProperty] private bool _blink;

    [ObservableProperty] private string _colorHex;

    [ObservableProperty] private string _durationText;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string _icon;

    [ObservableProperty] private bool _isPlayerVisible;

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _sound;

    [ObservableProperty] private int _soundRepeatCount;

    public TimerItemViewModel(TimerItem model, Action<TimerItemViewModel> onDeleteRequested)
    {
        _model = model;
        _onDeleteRequested = onDeleteRequested;
        _name = model.Name;
        _icon = VisualItemHelper.NormalizeIcon(model.Icon);
        _model.Icon = _icon;
        _sound = string.IsNullOrWhiteSpace(model.Sound) ? SoundService.Pling : model.Sound;
        _soundRepeatCount = model.SoundRepeatCount < 0 ? 0 : model.SoundRepeatCount;
        _colorHex = model.ColorHex;
        _blink = model.Blink;
        _isPlayerVisible = model.IsPlayerVisible;
        _durationText = FormatTimeSpan(model.Duration);
        _model.Completed += OnModelCompleted;
    }

    public Guid Id => _model.Id;

    /// <summary>Optionales Bild/Video, das automatisch verteilt wird, wenn dieser Timer abläuft.</summary>
    public TriggerMediaConfig TriggerMedia { get; } = new();

    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public double Progress => _model.Progress;
    public string RemainingText => FormatTimeSpan(_model.Remaining);
    public string DurationDisplay => FormatTimeSpan(_model.Duration);
    public bool IsRunning => _model.IsRunning;
    public bool IsCompleted => _model.IsCompleted;
    public bool CanEditDuration => true;

    public string StartPauseLabel => _model.IsCompleted ? "Abgelaufen" :
        _model.IsRunning ? "Pause" :
        _model.Elapsed > TimeSpan.Zero ? "Weiter" : "Start";

    public IBrush? ItemBorderBrush => IsBlinkActive
        ? VisualItemHelper.TryBrush(ColorHex) ?? Brushes.Gold
        : VisualItemHelper.TryBrush(ColorHex);

    public bool IsBlinkActive => Blink && IsCompleted && VisualItemHelper.BlinkFrame();
    public TimeSpan? TimeUntilNextEvent => _model.IsRunning && !_model.IsCompleted ? _model.Remaining : null;

    public string SoundToPlay => _model.Sound;
    public int SoundRepeatCountToPlay => _model.SoundRepeatCount;

    /// <summary>
    ///     Feuert bei diskreten Zustandsänderungen (bearbeitet/gestartet/pausiert/zurückgesetzt/
    ///     abgelaufen) - NICHT bei jedem Uhr-Tick. MainWindowViewModel hängt sich hier ein, um nur
    ///     bei echten Änderungen ein timelineItem.upserted übers Netz zu schicken.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    ///     Feuert bei Reset() - MainWindowViewModel schließt dann eine gerade laufende, von diesem Timer ausgelöste
    ///     Medienanzeige.
    /// </summary>
    public event Action? MediaResetRequested;

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
        if (!TimeSpan.TryParse(DurationText, out var parsed) || parsed <= TimeSpan.Zero)
        {
            ErrorMessage = "Ungültige Dauer (Format dd.hh:mm:ss)";
            return;
        }

        _model.Duration = parsed;
        _model.Restore(_model.Elapsed, _model.IsRunning);

        ErrorMessage = null;
        RefreshAll();
        StateChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (_model.IsCompleted) return;

        if (_model.IsRunning)
        {
            _model.Pause();
        }
        else
        {
            if (_model.Elapsed == TimeSpan.Zero)
            {
                if (!TimeSpan.TryParse(DurationText, out var parsed) || parsed <= TimeSpan.Zero)
                {
                    ErrorMessage = "Ungültige Dauer (Format hh:mm:ss)";
                    return;
                }

                _model.Duration = parsed;
                ErrorMessage = null;
            }

            _model.Start();
        }

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
        MediaResetRequested?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    public TimerDto ToDto()
    {
        return new TimerDto
        {
            Name = _model.Name,
            Icon = _model.Icon,
            Sound = _model.Sound,
            SoundRepeatCount = _model.SoundRepeatCount,
            ColorHex = _model.ColorHex,
            Blink = _model.Blink,
            IsPlayerVisible = _model.IsPlayerVisible,
            DurationTicks = _model.Duration.Ticks,
            ElapsedTicks = _model.Elapsed.Ticks,
            IsRunning = _model.IsRunning,
            TriggerMediaPath = TriggerMedia.Path,
            TriggerMediaFileName = TriggerMedia.FileName,
            TriggerMediaKind = TriggerMedia.Kind == MediaKind.None ? null : TriggerMedia.Kind.ToString(),
            TriggerMediaFullscreen = TriggerMedia.Fullscreen,
            TriggerMediaPauseClock = TriggerMedia.PauseClockDuringVideo,
            TriggerMediaLoop = TriggerMedia.Loop
        };
    }

    public static TimerItemViewModel FromDto(TimerDto dto, Action<TimerItemViewModel> onDeleteRequested)
    {
        var model = new TimerItem
        {
            Name = dto.Name,
            Icon = VisualItemHelper.NormalizeIcon(dto.Icon),
            Sound = string.IsNullOrWhiteSpace(dto.Sound) ? SoundService.Pling : dto.Sound,
            SoundRepeatCount = dto.SoundRepeatCount < 0 ? 0 : dto.SoundRepeatCount,
            ColorHex = dto.ColorHex ?? string.Empty,
            Blink = dto.Blink,
            IsPlayerVisible = dto.IsPlayerVisible,
            Duration = TimeSpan.FromTicks(dto.DurationTicks)
        };
        model.Restore(TimeSpan.FromTicks(dto.ElapsedTicks), dto.IsRunning);
        var vm = new TimerItemViewModel(model, onDeleteRequested);
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

    public bool Advance(TimeSpan gameDelta)
    {
        var completedNow = _model.Advance(gameDelta);
        RefreshAll();
        return completedNow;
    }

    private void OnModelCompleted()
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
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(IconGeometry));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CanEditDuration));
        OnPropertyChanged(nameof(StartPauseLabel));
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