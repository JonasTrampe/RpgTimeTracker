using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One named mood/variant of an NPC (e.g. "Neutral", "Angry", "Wounded" - see
///     NpcLibraryItemViewModel.Variants). Image/TokenImage/TokenIcon/PlayerInfo/Sounds all fall back
///     to the owning NPC's Default variant when unset - see NpcLibraryItemViewModel.GetEffectiveImage
///     etc., which resolve the fallback (this class deliberately doesn't know about its owner or
///     sibling variants, to stay a plain data holder).
/// </summary>
public partial class NpcVariantViewModel : ObservableObject
{
    private readonly Action<NpcVariantViewModel>? _onChanged;
    private readonly Action<NpcVariantViewModel> _onDeleteRequested;

    /// <summary>
    ///     Whether Sounds is an explicit override for this variant (even if it ends up empty -
    ///     "no sounds at all" is a valid override) rather than "inherit the Default variant's
    ///     sounds" - flips to true the first time the GM adds/removes a sound on a non-default
    ///     variant. See NpcLibraryItemViewModel.GetEffectiveSounds.
    /// </summary>
    [ObservableProperty] private bool _hasSoundsOverride;

    [ObservableProperty] private MediaLibraryItemViewModel? _image;
    [ObservableProperty] private bool _isDefault;

    /// <summary>
    ///     Purely local UI state (not persisted) - whether PlayerInfo currently shows its
    ///     rendered Markdown preview instead of the plain-text editor. See MainWindow.axaml's
    ///     per-field preview toggle button. Defaults to false (edit) here since PlayerInfo isn't a
    ///     constructor parameter (it's set afterward via the property setter) - callers that
    ///     already know the loaded value should set this explicitly once PlayerInfo is populated;
    ///     see MainWindowViewModel.FromNpcLibraryEntryDto. See
    ///     NpcGmInfoBlockViewModel.IsPreviewMode's doc comment for the edit-vs-preview reasoning.
    /// </summary>
    [ObservableProperty] private bool _isPlayerInfoPreviewMode;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _playerInfo;
    [ObservableProperty] private string? _tokenIcon;
    [ObservableProperty] private MediaLibraryItemViewModel? _tokenImage;

    public NpcVariantViewModel(
        Guid id,
        string name,
        bool isDefault,
        Action<NpcVariantViewModel> onDeleteRequested,
        Action<NpcVariantViewModel>? onChanged)
    {
        Id = id;
        _name = name;
        _isDefault = isDefault;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        Sounds.CollectionChanged += (_, _) => _onChanged?.Invoke(this);
    }

    public string PlayerInfoPreviewToggleIcon => IsPlayerInfoPreviewMode ? "✎" : "👁";

    public Guid Id { get; }

    public ObservableCollection<SoundLibraryItemViewModel> Sounds { get; } = [];

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

    partial void OnIsPlayerInfoPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayerInfoPreviewToggleIcon));
    }

    [RelayCommand]
    private void TogglePlayerInfoPreview()
    {
        IsPlayerInfoPreviewMode = !IsPlayerInfoPreviewMode;
    }

    /// <summary>
    ///     Not exposed for the Default variant in the UI (it can't be deleted) - the check
    ///     belongs to the caller (NpcLibraryItemViewModel.RemoveVariant), not this command, since
    ///     the command has no way to refuse and stay silent otherwise.
    /// </summary>
    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}