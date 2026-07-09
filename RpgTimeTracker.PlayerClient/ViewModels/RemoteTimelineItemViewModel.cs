using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.PlayerClient.ViewModels;

/// <summary>
///     Pure display shell for a timer/alarm/interval item on the remote client.
///     The values are set by ClientMainWindowViewModel, which derives the actual
///     progress/remaining-time calculation locally from the (RPC-synchronized) shared models
///     + the local clock - the server no longer sends ready-formatted strings here.
/// </summary>
public partial class RemoteTimelineItemViewModel : ObservableObject, IPlayerTimelineEntry
{
    [ObservableProperty] private string _colorHex = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _icon = VisualItemHelper.IconTimer;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBlinkActive;
    [ObservableProperty] private bool _isCompleted;

    [ObservableProperty] private string _kindLabel = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _primaryValue = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = string.Empty;

    public RemoteTimelineItemViewModel(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; }

    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);
    public IBrush? ItemBorderBrush => VisualItemHelper.TryBrush(ColorHex);

    partial void OnIconChanged(string value)
    {
        OnPropertyChanged(nameof(IconGeometry));
    }

    partial void OnColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(ItemBorderBrush));
    }
}