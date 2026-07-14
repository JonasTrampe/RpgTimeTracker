using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A preselected sound in the GM sound library - completely separate from the image/video
///     library (see MediaLibraryItemViewModel). A single click sends it immediately to all players
///     (and locally at the GM's side if applicable, see MainWindowViewModel.SendSoundAsync); "Test" plays it ONLY locally
///     at the GM's side, without sending it - to check the volume before actually sending it.
/// </summary>
public partial class SoundLibraryItemViewModel : LibraryItemViewModelBase<SoundLibraryItemViewModel>
{
    private readonly Action<SoundLibraryItemViewModel> _onPlayRequested;
    private readonly Action<SoundLibraryItemViewModel> _onTestRequested;

    [ObservableProperty] private string _icon;

    /// <summary>Whether the sound should automatically restart from the beginning at the end, instead of ending naturally.</summary>
    [ObservableProperty] private bool _loop;

    /// <summary>Only effective when Loop=false: total number of playbacks (1 = once, no repeat).</summary>
    [ObservableProperty] private int _repeatCount = 1;

    [ObservableProperty] private double _trimEndSeconds;

    /// <summary>Trim in seconds - 0 means "no trim at this point".</summary>
    [ObservableProperty] private double _trimStartSeconds;

    /// <summary>Default volume (0-100) with which this sound is sent/tested.</summary>
    [ObservableProperty] private int _volume;

    public SoundLibraryItemViewModel(
        Guid id,
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
        : base(id, name, localPath, mimeType, onDeleteRequested, onChanged)
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