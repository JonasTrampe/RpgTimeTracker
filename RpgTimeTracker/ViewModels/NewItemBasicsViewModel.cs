using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     The "basic setup" when creating a new timer/alarm/interval: icon, name,
///     sound (+repeat), display color, blink, player-visible. Bundles fields that
///     were previously duplicated as seven separate NewTimerXxx/NewAlarmXxx/NewIntervalXxx
///     properties on MainWindowViewModel, so that NewItemBasicsEditor.axaml (view) exists once
///     instead of three times.
/// </summary>
public partial class NewItemBasicsViewModel : ObservableObject
{
    private readonly bool _defaultBlink;
    private readonly string _defaultColorHex;
    private readonly string _defaultIcon;

    private readonly string _defaultName;

    [ObservableProperty] private bool _blink;

    [ObservableProperty] private string _colorHex;

    [ObservableProperty] private string _icon;

    [ObservableProperty] private bool _isPlayerVisible = true;

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _sound = SoundService.Pling;

    [ObservableProperty] private int _soundRepeatCount = 1;

    public NewItemBasicsViewModel(string defaultName, string defaultIcon, string defaultColorHex = "",
        bool defaultBlink = false)
    {
        _defaultName = defaultName;
        _defaultIcon = defaultIcon;
        _defaultColorHex = defaultColorHex;
        _defaultBlink = defaultBlink;

        _name = defaultName;
        _icon = defaultIcon;
        _colorHex = defaultColorHex;
        _blink = defaultBlink;
    }

    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public ObservableCollection<string> SoundOptions => SoundService.SoundOptions;

    partial void OnIconChanged(string value)
    {
        var normalized = VisualItemHelper.NormalizeIcon(value);
        if (Icon != normalized) Icon = normalized;
    }

    /// <summary>Reset to the default values for the next item after creating one.</summary>
    public void ResetToDefaults()
    {
        Name = _defaultName;
        Icon = _defaultIcon;
        Sound = SoundService.Pling;
        SoundRepeatCount = 1;
        ColorHex = _defaultColorHex;
        Blink = _defaultBlink;
        IsPlayerVisible = true;
    }
}