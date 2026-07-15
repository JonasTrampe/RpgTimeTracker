using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named, ordered playlist of Music Library tracks. Deliberately does not derive from
///     LibraryItemViewModelBase&lt;TSelf&gt; - same reasoning as MapItemViewModel: a playlist
///     isn't a single file, it's a name plus an ordered list of references into the Music
///     Library. Deleting a playlist never deletes the underlying tracks/files, only this
///     definition (see MainWindowViewModel.RemovePlaylist).
/// </summary>
public sealed partial class PlaylistViewModel : ObservableObject
{
    private readonly Action<PlaylistViewModel>? _onChanged;
    private readonly Action<PlaylistViewModel> _onDeleteRequested;

    /// <summary>Restart from the first track after the last one finishes, instead of stopping.</summary>
    [ObservableProperty] private bool _loopPlaylist = true;

    [ObservableProperty] private string _name;

    /// <summary>Play tracks in random order instead of the list order below.</summary>
    [ObservableProperty] private bool _shuffle;

    public PlaylistViewModel(
        Guid id,
        string name,
        bool loopPlaylist,
        bool shuffle,
        Action<PlaylistViewModel> onDeleteRequested,
        Action<PlaylistViewModel>? onChanged)
    {
        Id = id;
        _name = name;
        _loopPlaylist = loopPlaylist;
        _shuffle = shuffle;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTracks));
    }

    public Guid Id { get; }

    public ObservableCollection<PlaylistTrackViewModel> Tracks { get; } = [];

    public bool HasNoTracks => Tracks.Count == 0;

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnLoopPlaylistChanged(bool value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnShuffleChanged(bool value)
    {
        _onChanged?.Invoke(this);
    }

    /// <summary>
    ///     Appends a reference to an existing Music Library track - see PlaylistTrackViewModel.
    ///     Persists immediately via onChanged; for restoring a playlist from settings at startup
    ///     (where each track shouldn't trigger its own separate, premature save), use
    ///     LoadTrack instead.
    /// </summary>
    public void AddTrack(MusicLibraryItemViewModel track)
    {
        LoadTrack(track);
        _onChanged?.Invoke(this);
    }

    /// <summary>
    ///     Adds a track reference WITHOUT persisting - only for reconstructing a playlist
    ///     from ThemeSettingsService.PlaylistEntryDto at startup (see MainWindowViewModel's load
    ///     loop), where the caller saves once after the whole playlist is rebuilt, not per track.
    /// </summary>
    public void LoadTrack(MusicLibraryItemViewModel track)
    {
        Tracks.Add(new PlaylistTrackViewModel(track, RemoveTrack, MoveTrackUp, MoveTrackDown));
    }

    /// <summary>
    ///     Removes every reference to a track that was just deleted from the Music Library
    ///     entirely - called from MainWindowViewModel.RemoveMusicLibraryItem for every playlist,
    ///     so a deleted track never lingers as a dangling reference.
    /// </summary>
    public void RemoveTracksReferencing(MusicLibraryItemViewModel track)
    {
        var stale = Tracks.Where(t => t.Track == track).ToList();
        if (stale.Count == 0) return;

        foreach (var entry in stale) Tracks.Remove(entry);
        _onChanged?.Invoke(this);
    }

    private void RemoveTrack(PlaylistTrackViewModel entry)
    {
        Tracks.Remove(entry);
        _onChanged?.Invoke(this);
    }

    private void MoveTrackUp(PlaylistTrackViewModel entry)
    {
        var index = Tracks.IndexOf(entry);
        if (index <= 0) return;

        Tracks.Move(index, index - 1);
        _onChanged?.Invoke(this);
    }

    private void MoveTrackDown(PlaylistTrackViewModel entry)
    {
        var index = Tracks.IndexOf(entry);
        if (index < 0 || index >= Tracks.Count - 1) return;

        Tracks.Move(index, index + 1);
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}