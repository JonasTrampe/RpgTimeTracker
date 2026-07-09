using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Optional image/video that is automatically distributed to the players as soon as a timer
///     runs out, an alarm triggers, or an OnTime interval becomes active (see
///     MainWindowViewModel.TriggerEventMedia). Purely host-side authoring configuration - it is
///     not transmitted to clients, only the media itself (via the existing media channel).
/// </summary>
public partial class TriggerMediaConfig : ObservableObject
{
    [ObservableProperty] private string? _fileName;

    [ObservableProperty] private bool _fullscreen;

    [ObservableProperty] private MediaKind _kind = MediaKind.None;

    /// <summary>
    ///     Only relevant for video/audio: restart from the beginning at the end instead of reporting the end to the
    ///     host and being closed.
    /// </summary>
    [ObservableProperty] private bool _loop;

    [ObservableProperty] private string? _path;

    /// <summary>
    ///     Only relevant for video: pauses game time for the duration of playback. Not
    ///     available for sounds - those are completely decoupled from video-end tracking (see
    ///     MainWindowViewModel.BeginVideoTracking), since multiple sounds can run in parallel and none
    ///     of them may replace an image/video or cut off its own end tracking.
    /// </summary>
    [ObservableProperty] private bool _pauseClockDuringVideo;

    public bool HasMedia => Kind != MediaKind.None && !string.IsNullOrEmpty(Path);
    public bool IsVideo => Kind == MediaKind.Video;

    partial void OnKindChanged(MediaKind value)
    {
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(IsVideo));
    }

    partial void OnPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMedia));
    }

    public void CopyTo(TriggerMediaConfig other)
    {
        other.Path = Path;
        other.FileName = FileName;
        other.Kind = Kind;
        other.Fullscreen = Fullscreen;
        other.PauseClockDuringVideo = PauseClockDuringVideo;
        other.Loop = Loop;
    }

    [RelayCommand]
    private void Clear()
    {
        Path = null;
        FileName = null;
        Kind = MediaKind.None;
        Fullscreen = false;
        PauseClockDuringVideo = false;
        Loop = false;
    }
}