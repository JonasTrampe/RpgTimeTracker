using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.ViewModels;

public partial class PlayerCalendarEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _colorHex = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _icon = VisualItemHelper.IconCalendar;
    [ObservableProperty] private string _timeText = string.Empty;
    [ObservableProperty] private string _title = string.Empty;

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);
    public IBrush? ColorBrush => VisualItemHelper.TryBrush(ColorHex);

    partial void OnIconChanged(string value)
    {
        OnPropertyChanged(nameof(IconGeometry));
    }

    partial void OnColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(ColorBrush));
    }
}