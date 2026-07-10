using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Network;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

/// <summary>A connected player client in the GM-side list, with the ability to manually disconnect it.</summary>
public partial class ConnectedClientItemViewModel : ObservableObject
{
    private readonly Action<ConnectedClientItemViewModel> _onDisconnectRequested;
    private readonly string _connectedSinceTime;

    public ConnectedClientItemViewModel(ConnectedClientInfo info,
        Action<ConnectedClientItemViewModel> onDisconnectRequested)
    {
        RemoteEndpoint = info.RemoteEndpoint;
        _connectedSinceTime = info.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        _onDisconnectRequested = onDisconnectRequested;
    }

    public string RemoteEndpoint { get; }

    public string ConnectedSinceDisplay =>
        string.Format(LocalizationService.Get("MainWindow.Settings.ConnectedSince"), _connectedSinceTime);

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ConnectedSinceDisplay));
    }

    [RelayCommand]
    private void Disconnect()
    {
        _onDisconnectRequested(this);
    }
}