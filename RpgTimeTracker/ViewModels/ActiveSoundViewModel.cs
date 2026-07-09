using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Ein gerade laufender, an Spieler gesendeter Sound - fürs "Aktuell abgespielte Sounds"-Panel
///     beim SL. Rein SL-seitige Anzeige (siehe MainWindowViewModel.ActivePlayingSounds); der Sound
///     selbst läuft komplett unabhängig vom Bild/Video-"aktuelles Medium"-Slot (siehe
///     MainWindowViewModel.SendSoundAsync-Kommentar).
/// </summary>
public partial class ActiveSoundViewModel : ObservableObject, IDisposable
{
    private readonly Action<ActiveSoundViewModel> _onStopRequested;
    private readonly Action<ActiveSoundViewModel, int> _onVolumeChanged;
    private readonly SoundLibraryItemViewModel? _sourceItem;

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
        _sourceItem = sourceItem;
        if (_sourceItem is not null)
            _sourceItem.PropertyChanged += OnSourceItemPropertyChanged;
    }

    public string MediaId { get; }
    public bool Loop { get; }
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);

    public void Dispose()
    {
        if (_sourceItem is not null)
            _sourceItem.PropertyChanged -= OnSourceItemPropertyChanged;
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

    // Live-Regler im Panel - jede Änderung wird sofort an Host+Client(s) übertragen (media.setVolume).
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
        if (_sourceItem is null) return;

        if (e.PropertyName == nameof(SoundLibraryItemViewModel.Name))
            Name = _sourceItem.Name;
        else if (e.PropertyName == nameof(SoundLibraryItemViewModel.Icon))
            Icon = _sourceItem.Icon;
    }
}