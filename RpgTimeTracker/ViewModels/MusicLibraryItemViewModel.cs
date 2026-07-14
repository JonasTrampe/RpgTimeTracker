using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A track in the GM music library - separate from the sound library
///     (see SoundLibraryItemViewModel) since music plays on its own transport/channel,
///     independently of sound effects, and will later be referenced by Playlists/Scenes by
///     its stable Id. "Play"/"Test" mirror the sound library's distinction: Play sends to
///     players (subject to per-window routing, once that milestone lands); "Test" plays ONLY
///     locally at the GM's side, to preview before actually sending it.
/// </summary>
public partial class MusicLibraryItemViewModel : LibraryItemViewModelBase<MusicLibraryItemViewModel>
{
    private readonly Action<MusicLibraryItemViewModel> _onPlayRequested;
    private readonly Action<MusicLibraryItemViewModel> _onTestRequested;

    [ObservableProperty] private string _icon;

    /// <summary>Default volume (0-100) with which this track is played.</summary>
    [ObservableProperty] private int _volume;

    public MusicLibraryItemViewModel(
        Guid id,
        string name,
        string icon,
        string localPath,
        string mimeType,
        int volume,
        Action<MusicLibraryItemViewModel> onDeleteRequested,
        Action<MusicLibraryItemViewModel> onPlayRequested,
        Action<MusicLibraryItemViewModel> onTestRequested,
        Action<MusicLibraryItemViewModel>? onChanged = null)
        : base(id, name, localPath, mimeType, onDeleteRequested, onChanged)
    {
        _icon = VisualItemHelper.NormalizeIcon(icon);
        _volume = volume;
        _onPlayRequested = onPlayRequested;
        _onTestRequested = onTestRequested;
    }

    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    partial void OnIconChanged(string value)
    {
        var normalized = VisualItemHelper.NormalizeIcon(value);
        if (Icon != normalized)
        {
            Icon = normalized;
            return;
        }

        OnPropertyChanged(nameof(IconGeometry));
        NotifyChanged();
    }

    partial void OnVolumeChanged(int value)
    {
        NotifyChanged();
    }

    [RelayCommand]
    private void Play()
    {
        _onPlayRequested(this);
    }

    [RelayCommand]
    private void Test()
    {
        _onTestRequested(this);
    }
}
