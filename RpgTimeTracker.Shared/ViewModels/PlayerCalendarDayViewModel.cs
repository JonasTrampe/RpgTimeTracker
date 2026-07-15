using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.ViewModels;

public partial class PlayerCalendarDayViewModel : ObservableObject
{
    private readonly Action<GameInstant> _onSelected;

    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _isCurrentMonth;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isToday;

    public PlayerCalendarDayViewModel(GameInstant date, string dayNumber, Action<GameInstant> onSelected)
    {
        Date = date;
        DayNumber = dayNumber;
        _onSelected = onSelected;
    }

    public GameInstant Date { get; }
    public string DayNumber { get; }

    [RelayCommand]
    private void Select()
    {
        _onSelected(Date);
    }
}