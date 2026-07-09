using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.PlayerClient.ViewModels;

/// <summary>
///     Reine Anzeige-Hülle für ein Timer-/Wecker-/Intervall-Item auf dem Remote-Client.
///     Die Werte werden von ClientMainWindowViewModel gesetzt, das die eigentliche
///     Fortschritts-/Restzeit-Berechnung lokal aus den (per RPC synchronisierten) Shared-Modellen
///     + der lokalen Uhr ableitet - der Server schickt hier keine fertig formatierten Strings mehr.
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