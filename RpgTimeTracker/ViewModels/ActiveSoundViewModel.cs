using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A currently playing sound sent to players - for the GM's "currently playing sounds"
///     panel. Purely GM-side display (see MainWindowViewModel.ActivePlayingSounds); the sound
///     itself runs completely independently of the image/video "current medium" slot (see
///     MainWindowViewModel.SendSoundAsync comment).
/// </summary>
public partial class ActiveSoundViewModel : ObservableObject, IDisposable
{
    private readonly Action<ActiveSoundViewModel> _onStopRequested;
    private readonly Action<ActiveSoundViewModel, int> _onVolumeChanged;

    [ObservableProperty] private string _icon;
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _volume;

    public ActiveSoundViewModel(
        string mediaId,
        string name,
        string icon,
        bool loop,
        int volume,
        Action<ActiveSoundViewModel> onStopRequested,
        Action<ActiveSoundViewModel, int> onVolumeChanged,
        SoundLibraryItemViewModel? sourceItem = null)
    {
        MediaId = mediaId;
        _name = name;
        _icon = VisualItemHelper.NormalizeIcon(icon);
        Loop = loop;
        _volume = volume;
        _onStopRequested = onStopRequested;
        _onVolumeChanged = onVolumeChanged;
        SourceItem = sourceItem;
        if (SourceItem is not null)
            SourceItem.PropertyChanged += OnSourceItemPropertyChanged;
    }

    public string MediaId { get; }
    public bool Loop { get; }

    /// <summary>
    ///     The Sound Library item this playing sound was sent from, if any - lets
    ///     MainWindowViewModel find and stop every active sound that originated from a given
    ///     library item (see StopShowingSound), the same "stop" gesture Maps/Playlists have.
    /// </summary>
    public SoundLibraryItemViewModel? SourceItem { get; }

    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public void Dispose()
    {
        if (SourceItem is not null)
            SourceItem.PropertyChanged -= OnSourceItemPropertyChanged;
    }

    partial void OnIconChanged(string value)
    {
        var normalized = VisualItemHelper.NormalizeIcon(value);
        if (Icon != normalized)
        {
            Icon = normalized;
            return;
        }

        OnPropertyChanged(nameof(IconGeometry));
    }

    // Live slider in the panel - every change is immediately transmitted to host+client(s) (media.setVolume).
    partial void OnVolumeChanged(int value)
    {
        _onVolumeChanged(this, value);
    }

    [RelayCommand]
    private void Stop()
    {
        _onStopRequested(this);
    }

    private void OnSourceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SourceItem is null) return;

        if (e.PropertyName == nameof(SoundLibraryItemViewModel.Name))
            Name = SourceItem.Name;
        else if (e.PropertyName == nameof(SoundLibraryItemViewModel.Icon))
            Icon = SourceItem.Icon;
    }
}