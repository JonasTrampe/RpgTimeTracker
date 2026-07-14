using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One named mood/variant of an NPC (e.g. "Neutral", "Angry", "Wounded" - see
///     NpcLibraryItemViewModel.States). Image/TokenImage/TokenIcon/PlayerInfo/Sounds all fall back
///     to the owning NPC's Default state when unset - see NpcLibraryItemViewModel.GetEffectiveImage
///     etc., which resolve the fallback (this class deliberately doesn't know about its owner or
///     sibling states, to stay a plain data holder).
/// </summary>
public partial class NpcStateViewModel : ObservableObject
{
    private readonly Action<NpcStateViewModel> _onDeleteRequested;
    private readonly Action<NpcStateViewModel>? _onChanged;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private MediaLibraryItemViewModel? _image;
    [ObservableProperty] private MediaLibraryItemViewModel? _tokenImage;
    [ObservableProperty] private string? _tokenIcon;
    [ObservableProperty] private string? _playerInfo;

    /// <summary>Whether Sounds is an explicit override for this state (even if it ends up empty -
    ///     "no sounds at all" is a valid override) rather than "inherit the Default state's
    ///     sounds" - flips to true the first time the GM adds/removes a sound on a non-default
    ///     state. See NpcLibraryItemViewModel.GetEffectiveSounds.</summary>
    [ObservableProperty] private bool _hasSoundsOverride;

    public Guid Id { get; }

    public ObservableCollection<SoundLibraryItemViewModel> Sounds { get; } = [];

    public NpcStateViewModel(
        Guid id,
        string name,
        bool isDefault,
        Action<NpcStateViewModel> onDeleteRequested,
        Action<NpcStateViewModel>? onChanged)
    {
        Id = id;
        _name = name;
        _isDefault = isDefault;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        Sounds.CollectionChanged += (_, _) => _onChanged?.Invoke(this);
    }

    public void AddSound(SoundLibraryItemViewModel sound)
    {
        Sounds.Add(sound);
        HasSoundsOverride = true;
    }

    public void RemoveSound(SoundLibraryItemViewModel sound)
    {
        Sounds.Remove(sound);
        HasSoundsOverride = true;
    }

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnImageChanged(MediaLibraryItemViewModel? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnTokenImageChanged(MediaLibraryItemViewModel? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnTokenIconChanged(string? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnPlayerInfoChanged(string? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnHasSoundsOverrideChanged(bool value)
    {
        _onChanged?.Invoke(this);
    }

    /// <summary>Not exposed for the Default state in the UI (it can't be deleted) - the check
    ///     belongs to the caller (NpcLibraryItemViewModel.RemoveState), not this command, since
    ///     the command has no way to refuse and stay silent otherwise.</summary>
    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}
