using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.Shared.ViewModels;

public partial class PlayerCalendarDayViewModel : ObservableObject
{
    private readonly Action<DateTime> _onSelected;

    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _isCurrentMonth;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isToday;

    public PlayerCalendarDayViewModel(DateTime date, Action<DateTime> onSelected)
    {
        Date = date.Date;
        _onSelected = onSelected;
    }

    public DateTime Date { get; }
    public string DayNumber => Date.Day.ToString();

    [RelayCommand]
    private void Select()
    {
        _onSelected(Date);
    }
}