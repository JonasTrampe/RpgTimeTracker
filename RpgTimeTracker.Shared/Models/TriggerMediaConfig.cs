using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     Optionales Bild/Video, das automatisch an die Spieler verteilt wird, sobald ein Timer
///     abläuft, ein Wecker auslöst oder ein OnTime-Intervall aktiv wird (siehe
///     MainWindowViewModel.TriggerEventMedia). Rein host-seitige Autoren-Konfiguration - wird
///     nicht an Clients übertragen, nur das Medium selbst (über den bestehenden Medien-Kanal).
/// </summary>
public partial class TriggerMediaConfig : ObservableObject
{
    [ObservableProperty] private string? _fileName;

    [ObservableProperty] private bool _fullscreen;

    [ObservableProperty] private MediaKind _kind = MediaKind.None;

    /// <summary>
    ///     Nur für Video/Audio relevant: am Ende von vorn beginnen statt dem Host das Ende zu melden und geschlossen zu
    ///     werden.
    /// </summary>
    [ObservableProperty] private bool _loop;

    [ObservableProperty] private string? _path;

    /// <summary>
    ///     Nur für Video relevant: pausiert die Spielzeit für die Dauer der Wiedergabe. Für
    ///     Sounds nicht verfügbar - die sind komplett vom Video-Ende-Tracking entkoppelt (siehe
    ///     MainWindowViewModel.BeginVideoTracking), da mehrere Sounds parallel laufen können und keiner
    ///     von ihnen ein Bild/Video ersetzen oder dessen eigenes Ende-Tracking kappen darf.
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