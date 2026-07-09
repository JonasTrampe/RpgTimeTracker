using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;

namespace RpgTimeTracker.ViewModels;

public partial class JumpMarkerItemViewModel : ObservableObject
{
    private readonly Func<DateTime> _getCurrentGameTime;
    private readonly JumpMarker _model;
    private readonly Action<JumpMarkerItemViewModel> _onDeleteRequested;
    private readonly Action<TimeSpan> _requestJump;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string _name;

    // Editierbarer Text im Format hh:mm.
    [ObservableProperty] private string _timeOfDayText;

    public JumpMarkerItemViewModel(
        JumpMarker model,
        Func<DateTime> getCurrentGameTime,
        Action<TimeSpan> requestJump,
        Action<JumpMarkerItemViewModel> onDeleteRequested)
    {
        _model = model;
        _getCurrentGameTime = getCurrentGameTime;
        _requestJump = requestJump;
        _onDeleteRequested = onDeleteRequested;
        _name = model.Name;
        _timeOfDayText = FormatTimeOfDay(model.TimeOfDay);
    }

    partial void OnNameChanged(string value)
    {
        _model.Name = value;
    }

    [RelayCommand]
    private void ApplyTime()
    {
        if (!TimeSpan.TryParse(TimeOfDayText, out var parsed) || parsed < TimeSpan.Zero ||
            parsed >= TimeSpan.FromDays(1))
        {
            ErrorMessage = "Ungültige Uhrzeit (Format hh:mm)";
            return;
        }

        ErrorMessage = null;
        _model.TimeOfDay = parsed;
        TimeOfDayText = FormatTimeOfDay(parsed);
    }

    [RelayCommand]
    private void JumpToNext()
    {
        var current = _getCurrentGameTime();
        var target = _model.NextOccurrence(current);
        _requestJump(target - current);
    }

    [RelayCommand]
    private void JumpToPrevious()
    {
        var current = _getCurrentGameTime();
        var target = _model.PreviousOccurrence(current);
        _requestJump(target - current);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    /// <summary>Erstellt einen serialisierbaren Schnappschuss für das Speichern.</summary>
    public JumpMarkerDto ToDto()
    {
        return new JumpMarkerDto
        {
            Name = _model.Name,
            TimeOfDayTicks = _model.TimeOfDay.Ticks
        };
    }

    /// <summary>Baut ein ViewModel aus einem gespeicherten Zustand wieder auf.</summary>
    public static JumpMarkerItemViewModel FromDto(
        JumpMarkerDto dto,
        Func<DateTime> getCurrentGameTime,
        Action<TimeSpan> requestJump,
        Action<JumpMarkerItemViewModel> onDeleteRequested)
    {
        var model = new JumpMarker
        {
            Name = dto.Name,
            TimeOfDay = TimeSpan.FromTicks(dto.TimeOfDayTicks)
        };
        return new JumpMarkerItemViewModel(model, getCurrentGameTime, requestJump, onDeleteRequested);
    }

    private static string FormatTimeOfDay(TimeSpan ts)
    {
        return ts.ToString(@"hh\:mm");
    }
}