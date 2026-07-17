using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Network;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A connected player client in the GM-side list, with the ability to manually
///     disconnect it and to route whether this window's Music/Sound/Image/Video/Map broadcasts
///     are on (see TcpPlayerServerService.SetClientMusicEnabled/SetClientSoundEnabled/
///     SetClientImageEnabled/SetClientVideoEnabled/SetClientMapEnabled), plus whether it's allowed
///     to draw map annotation strokes (see SetClientCanAnnotate).
/// </summary>
public partial class ConnectedClientItemViewModel : ObservableObject
{
    private readonly string _connectedSinceTime;
    private readonly Action<ConnectedClientItemViewModel> _onDisconnectRequested;
    private readonly Action<ConnectedClientItemViewModel, bool> _onCanAnnotateChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onImageEnabledChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onMapEnabledChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onMusicEnabledChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onSoundEnabledChanged;
    private readonly Action<ConnectedClientItemViewModel, bool> _onVideoEnabledChanged;

    [ObservableProperty] private bool _canAnnotate;

    [ObservableProperty] private bool _imageEnabled;

    [ObservableProperty] private bool _mapEnabled;

    [ObservableProperty] private bool _musicEnabled;

    [ObservableProperty] private bool _soundEnabled;

    [ObservableProperty] private bool _videoEnabled;

    public ConnectedClientItemViewModel(ConnectedClientInfo info,
        Action<ConnectedClientItemViewModel> onDisconnectRequested,
        Action<ConnectedClientItemViewModel, bool> onMusicEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onSoundEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onImageEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onVideoEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onMapEnabledChanged,
        Action<ConnectedClientItemViewModel, bool> onCanAnnotateChanged)
    {
        RemoteEndpoint = info.RemoteEndpoint;
        _connectedSinceTime = info.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        _onDisconnectRequested = onDisconnectRequested;
        _onMusicEnabledChanged = onMusicEnabledChanged;
        _onSoundEnabledChanged = onSoundEnabledChanged;
        _onImageEnabledChanged = onImageEnabledChanged;
        _onVideoEnabledChanged = onVideoEnabledChanged;
        _onMapEnabledChanged = onMapEnabledChanged;
        _onCanAnnotateChanged = onCanAnnotateChanged;
        _musicEnabled = info.MusicEnabled;
        _soundEnabled = info.SoundEnabled;
        _imageEnabled = info.ImageEnabled;
        _videoEnabled = info.VideoEnabled;
        _mapEnabled = info.MapEnabled;
        _canAnnotate = info.CanAnnotate;
    }

    public string RemoteEndpoint { get; }

    public string ConnectedSinceDisplay =>
        string.Format(LocalizationService.Get("MainWindow.Settings.ConnectedSince"), _connectedSinceTime);

    partial void OnMusicEnabledChanged(bool value)
    {
        _onMusicEnabledChanged(this, value);
    }

    partial void OnSoundEnabledChanged(bool value)
    {
        _onSoundEnabledChanged(this, value);
    }

    partial void OnImageEnabledChanged(bool value)
    {
        _onImageEnabledChanged(this, value);
    }

    partial void OnVideoEnabledChanged(bool value)
    {
        _onVideoEnabledChanged(this, value);
    }

    partial void OnMapEnabledChanged(bool value)
    {
        _onMapEnabledChanged(this, value);
    }

    partial void OnCanAnnotateChanged(bool value)
    {
        _onCanAnnotateChanged(this, value);
    }

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