using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Ein vorausgewählter Sound in der SL-Sound-Bibliothek - komplett getrennt von der Bild/Video-
///     Bibliothek (siehe MediaLibraryItemViewModel). Einfachklick sendet ihn sofort an alle Spieler
///     (und ggf. lokal beim SL, siehe MainWindowViewModel.SendSoundAsync); "Test" spielt ihn NUR lokal
///     beim SL ab, ohne ihn zu senden - zum Prüfen der Lautstärke vor dem eigentlichen Senden.
/// </summary>
public partial class SoundLibraryItemViewModel : LibraryItemViewModelBase<SoundLibraryItemViewModel>
{
    private readonly Action<SoundLibraryItemViewModel> _onPlayRequested;
    private readonly Action<SoundLibraryItemViewModel> _onTestRequested;

    [ObservableProperty] private string _icon;

    /// <summary>Ob der Sound am Ende automatisch von vorn beginnen soll, statt natürlich zu enden.</summary>
    [ObservableProperty] private bool _loop;

    /// <summary>Nur wirksam wenn Loop=false: Gesamtanzahl der Wiedergaben (1 = einmal, kein Wiederholen).</summary>
    [ObservableProperty] private int _repeatCount = 1;

    [ObservableProperty] private double _trimEndSeconds;

    /// <summary>Zurechtschneiden in Sekunden - 0 bedeutet "kein Trim an dieser Stelle".</summary>
    [ObservableProperty] private double _trimStartSeconds;

    /// <summary>Standard-Lautstärke (0-100), mit der dieser Sound gesendet/getestet wird.</summary>
    [ObservableProperty] private int _volume;

    public SoundLibraryItemViewModel(
        string name,
        string icon,
        string localPath,
        string mimeType,
        bool loop,
        int volume,
        int repeatCount,
        double trimStartSeconds,
        double trimEndSeconds,
        Action<SoundLibraryItemViewModel> onDeleteRequested,
        Action<SoundLibraryItemViewModel> onPlayRequested,
        Action<SoundLibraryItemViewModel> onTestRequested,
        Action<SoundLibraryItemViewModel>? onChanged = null)
        : base(name, localPath, mimeType, onDeleteRequested, onChanged)
    {
        _icon = VisualItemHelper.NormalizeIcon(icon);
        _loop = loop;
        _volume = volume;
        _repeatCount = repeatCount;
        _trimStartSeconds = trimStartSeconds;
        _trimEndSeconds = trimEndSeconds;
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

    partial void OnLoopChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnVolumeChanged(int value)
    {
        NotifyChanged();
    }

    partial void OnRepeatCountChanged(int value)
    {
        NotifyChanged();
    }

    partial void OnTrimStartSecondsChanged(double value)
    {
        NotifyChanged();
    }

    partial void OnTrimEndSecondsChanged(double value)
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