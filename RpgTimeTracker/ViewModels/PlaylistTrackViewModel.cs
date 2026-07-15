using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One slot in a PlaylistViewModel's track list - a reference to an existing
///     MusicLibraryItemViewModel, not a copy: removing a track from a playlist never touches the
///     Music Library itself or its file, it only removes this reference. Move up/down and remove
///     are handled by the owning PlaylistViewModel (which owns the ordered Tracks collection).
/// </summary>
public sealed partial class PlaylistTrackViewModel : ObservableObject
{
    private readonly Action<PlaylistTrackViewModel> _onMoveDownRequested;
    private readonly Action<PlaylistTrackViewModel> _onMoveUpRequested;
    private readonly Action<PlaylistTrackViewModel> _onRemoveRequested;

    public PlaylistTrackViewModel(
        MusicLibraryItemViewModel track,
        Action<PlaylistTrackViewModel> onRemoveRequested,
        Action<PlaylistTrackViewModel> onMoveUpRequested,
        Action<PlaylistTrackViewModel> onMoveDownRequested)
    {
        Track = track;
        _onRemoveRequested = onRemoveRequested;
        _onMoveUpRequested = onMoveUpRequested;
        _onMoveDownRequested = onMoveDownRequested;
    }

    public MusicLibraryItemViewModel Track { get; }

    [RelayCommand]
    private void Remove()
    {
        _onRemoveRequested(this);
    }

    [RelayCommand]
    private void MoveUp()
    {
        _onMoveUpRequested(this);
    }

    [RelayCommand]
    private void MoveDown()
    {
        _onMoveDownRequested(this);
    }
}