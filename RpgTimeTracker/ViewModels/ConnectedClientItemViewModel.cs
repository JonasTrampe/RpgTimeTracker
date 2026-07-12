using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Network;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

/// <summary>A connected player client in the GM-side list, with the ability to manually
///     disconnect it and to route whether this window's Music/Sound broadcasts are on
///     (see TcpPlayerServerService.SetClientMusicEnabled/SetClientSoundEnabled).</summary>
public partial class ConnectedClientItemViewModel : ObservableObject
{
    private readonly Action<ConnectedClientItemViewModel> _onDisconnectRequested;
    private readonly Action<ConnectedClientItemViewModel, bool> _onMusicEnabledChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onSoundEnabledChanged;
    private readonly string _connectedSinceTime;

    public ConnectedClientItemViewModel(ConnectedClientInfo info,
        Action<ConnectedClientItemViewModel> onDisconnectRequested,
        Action<ConnectedClientItemViewModel, bool> onMusicEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onSoundEnabledChanged)
    {
        RemoteEndpoint = info.RemoteEndpoint;
        _connectedSinceTime = info.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        _onDisconnectRequested = onDisconnectRequested;
        _onMusicEnabledChanged = onMusicEnabledChanged;
        _onSoundEnabledChanged = onSoundEnabledChanged;
        _musicEnabled = info.MusicEnabled;
        _soundEnabled = info.SoundEnabled;
    }

    public string RemoteEndpoint { get; }

    [ObservableProperty] private bool _musicEnabled;

    [ObservableProperty] private bool _soundEnabled;

    partial void OnMusicEnabledChanged(bool value) => _onMusicEnabledChanged(this, value);

    partial void OnSoundEnabledChanged(bool value) => _onSoundEnabledChanged(this, value);

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