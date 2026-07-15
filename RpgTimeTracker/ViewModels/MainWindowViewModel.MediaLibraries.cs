using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    // ==================== Now Playing (playlist sequencer) ====================
    // Deliberately separate from SelectedPlaylist: SelectedPlaylist is just "which playlist is
    // being edited in the UI" - CurrentPlaylist is "which playlist is actually playing," and the
    // two are independent (editing one playlist while a different one plays is normal).

    /// <summary>The playlist currently playing (or null if nothing is), started via PlayPlaylist.</summary>
    [ObservableProperty] private PlaylistViewModel? _currentPlaylist;

    /// <summary>The track from CurrentPlaylist currently playing.</summary>
    [ObservableProperty] private MusicLibraryItemViewModel? _currentPlaylistTrack;

    /// <summary>
    ///     Live volume (0-100) of the currently playing track - bound to the Now Playing
    ///     slider. Reset to the track's own default whenever a new track starts (see
    ///     PlayCurrentPlaylistTrackAsync), then adjustable live from there.
    /// </summary>
    [ObservableProperty] private int _currentPlaylistVolume = 100;

    [ObservableProperty] private bool _isPlaylistPlaying;

    /// <summary>Library track chosen in the Music tab's "add to playlist" picker, not yet added.</summary>
    [ObservableProperty] private MusicLibraryItemViewModel? _playlistTrackToAdd;

    [ObservableProperty] private PlaylistViewModel? _selectedPlaylist;
    // ==================== Media library (preselected, show via double-click) ====================

    public ObservableCollection<MediaLibraryItemViewModel> MediaLibrary { get; } = [];

    public bool HasNoMediaLibraryItems => MediaLibrary.Count == 0;

    // ==================== Sound library (separate from the image/video library) ====================

    public ObservableCollection<SoundLibraryItemViewModel> SoundLibrary { get; } = [];

    public bool HasNoSoundLibraryItems => SoundLibrary.Count == 0;

    // ==================== Music library (separate from sound effects - see MusicLibraryItemViewModel) ====================

    public ObservableCollection<MusicLibraryItemViewModel> MusicLibrary { get; } = [];

    public bool HasNoMusicLibraryItems => MusicLibrary.Count == 0;

    // ==================== Playlists (ordered references into the Music Library) ====================

    public ObservableCollection<PlaylistViewModel> Playlists { get; } = [];

    public bool HasNoPlaylists => Playlists.Count == 0;

    partial void OnCurrentPlaylistVolumeChanged(int value)
    {
        if (!IsPlaylistPlaying) return;

        if (_localMusicPlayer is not null) _localMusicPlayer.Volume = Math.Clamp(value, 0, 100);
        _ = _playerServer.PublishMusicSetVolumeAsync(value);
    }

    [RelayCommand]
    private void AddPlaylist()
    {
        var playlist = new PlaylistViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewPlaylistName"), true, false,
            RemovePlaylist, _ => SavePlaylistsSettings());
        Playlists.Add(playlist);
        OnPropertyChanged(nameof(HasNoPlaylists));
        SelectedPlaylist = playlist;
        SavePlaylistsSettings();
    }

    private void RemovePlaylist(PlaylistViewModel playlist)
    {
        if (CurrentPlaylist == playlist) StopPlaylistPlayback();

        Playlists.Remove(playlist);
        OnPropertyChanged(nameof(HasNoPlaylists));
        if (SelectedPlaylist == playlist) SelectedPlaylist = null;

        // A Playlist referenced by a Scene carries no content of its own for the Scene (same as
        // a Tag assignment) - unset silently rather than routing through the confirm-delete
        // LibraryUsageRegistry flow used for Media/Sound/Map/Music.
        foreach (var scene in SceneLibrary) scene.Playlists.Remove(playlist);

        SavePlaylistsSettings();
    }

    /// <summary>Adds PlaylistTrackToAdd to SelectedPlaylist - the Music tab's "Add" button calls this.</summary>
    [RelayCommand]
    private void AddSelectedTrackToSelectedPlaylist()
    {
        if (SelectedPlaylist is null || PlaylistTrackToAdd is null) return;

        SelectedPlaylist.AddTrack(PlaylistTrackToAdd);
        PlaylistTrackToAdd = null;
    }

    private void SavePlaylistsSettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.Playlists = Playlists.Select(p => new PlaylistEntryDto
        {
            Id = p.Id,
            Name = p.Name,
            TrackIds = p.Tracks.Select(t => t.Track.Id).ToList(),
            LoopPlaylist = p.LoopPlaylist,
            Shuffle = p.Shuffle
        }).ToList();
        ThemeSettingsService.SaveSettings(settings);
    }
}