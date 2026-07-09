using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Network;

namespace RpgTimeTracker.ViewModels;

/// <summary>A connected player client in the GM-side list, with the ability to manually disconnect it.</summary>
public partial class ConnectedClientItemViewModel : ObservableObject
{
    private readonly Action<ConnectedClientItemViewModel> _onDisconnectRequested;

    public ConnectedClientItemViewModel(ConnectedClientInfo info,
        Action<ConnectedClientItemViewModel> onDisconnectRequested)
    {
        RemoteEndpoint = info.RemoteEndpoint;
        ConnectedSinceDisplay = info.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        _onDisconnectRequested = onDisconnectRequested;
    }

    public string RemoteEndpoint { get; }
    public string ConnectedSinceDisplay { get; }

    [RelayCommand]
    private void Disconnect()
    {
        _onDisconnectRequested(this);
    }
}