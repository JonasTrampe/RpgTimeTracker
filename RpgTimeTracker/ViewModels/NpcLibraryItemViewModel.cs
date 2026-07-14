using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named character (NPC or PC) in the Characters library - own top-level library panel,
///     not nested under Maps. Deliberately does not derive from LibraryItemViewModelBase&lt;TSelf&gt;,
///     following MapItemViewModel's precedent: that base class assumes a single owned
///     LocalPath/MimeType per item, which doesn't fit here either, just for the opposite reason -
///     an NPC owns no files at all. Its portrait/token image and sounds are references (by Id)
///     into the Media/Sound Libraries instead of copies, which is the first cross-library
///     reference relationship in this codebase - see MainWindowViewModel's LibraryUsageRegistry
///     wiring for how a referenced Media/Sound item's deletion is guarded against.
/// </summary>
public sealed partial class NpcLibraryItemViewModel : ObservableObject
{
    private readonly Action<NpcLibraryItemViewModel> _onDeleteRequested;
    private readonly Action<NpcLibraryItemViewModel>? _onChanged;

    [ObservableProperty] private string _name;
    [ObservableProperty] private LibraryScope _scope;
    [ObservableProperty] private NpcStateViewModel? _activeState;

    public Guid Id { get; }

    /// <summary>Shared across all States - private GM reference notes, not per-mood.</summary>
    public ObservableCollection<NpcGmInfoBlockViewModel> GmInfoBlocks { get; } = [];

    public ObservableCollection<NpcStateViewModel> States { get; } = [];

    public NpcStateViewModel DefaultState => States.First(s => s.IsDefault);

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    public NpcLibraryItemViewModel(
        Guid id,
        string name,
        Action<NpcLibraryItemViewModel> onDeleteRequested,
        Action<NpcLibraryItemViewModel>? onChanged,
        LibraryScope scope = LibraryScope.Shared)
    {
        Id = id;
        _name = name;
        _scope = scope;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnScopeChanged(LibraryScope value)
    {
        OnPropertyChanged(nameof(IsSessionLocal));
        _onChanged?.Invoke(this);
    }

    partial void OnActiveStateChanged(NpcStateViewModel? value)
    {
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    // ==================== States ====================

    public NpcStateViewModel AddState(string name, bool isDefault = false, Guid? id = null)
    {
        var state = new NpcStateViewModel(id ?? Guid.NewGuid(), name, isDefault, RemoveState, _ => _onChanged?.Invoke(this));
        States.Add(state);
        if (isDefault || States.Count == 1) ActiveState ??= state;
        _onChanged?.Invoke(this);
        return state;
    }

    /// <summary>The Default state can't be removed - a no-op if attempted, since NpcStateViewModel's
    ///     own Delete command has no way to refuse and stay silent otherwise.</summary>
    private void RemoveState(NpcStateViewModel state)
    {
        if (state.IsDefault) return;

        States.Remove(state);
        if (ActiveState == state) ActiveState = DefaultState;
        _onChanged?.Invoke(this);
    }

    // ==================== GM info blocks ====================

    public void AddGmInfoBlock(string title)
    {
        GmInfoBlocks.Add(new NpcGmInfoBlockViewModel(title, string.Empty, RemoveGmInfoBlock, _ => _onChanged?.Invoke(this)));
        _onChanged?.Invoke(this);
    }

    private void RemoveGmInfoBlock(NpcGmInfoBlockViewModel block)
    {
        GmInfoBlocks.Remove(block);
        _onChanged?.Invoke(this);
    }

    /// <summary>GM info blocks are ordered - mirrors MapItemViewModel.MoveFloorUp/MoveFloorDown.</summary>
    public void MoveGmInfoBlockUp(NpcGmInfoBlockViewModel block)
    {
        var index = GmInfoBlocks.IndexOf(block);
        if (index <= 0) return;

        GmInfoBlocks.Move(index, index - 1);
        _onChanged?.Invoke(this);
    }

    public void MoveGmInfoBlockDown(NpcGmInfoBlockViewModel block)
    {
        var index = GmInfoBlocks.IndexOf(block);
        if (index < 0 || index >= GmInfoBlocks.Count - 1) return;

        GmInfoBlocks.Move(index, index + 1);
        _onChanged?.Invoke(this);
    }

    // ==================== Effective (fallback-to-Default) accessors ====================
    // Same pattern as MainWindowViewModel.GetEffectiveFogStyle (per-map override falling back to
    // a global default) - here the "default" is local to the NPC (its Default state) rather than
    // a global setting.

    public MediaLibraryItemViewModel? GetEffectiveImage(NpcStateViewModel state) =>
        state.IsDefault ? state.Image : state.Image ?? DefaultState.Image;

    public MediaLibraryItemViewModel? GetEffectiveTokenImage(NpcStateViewModel state) =>
        state.IsDefault ? state.TokenImage : state.TokenImage ?? DefaultState.TokenImage;

    public string? GetEffectiveTokenIcon(NpcStateViewModel state) =>
        state.IsDefault ? state.TokenIcon : state.TokenIcon ?? DefaultState.TokenIcon;

    public string? GetEffectivePlayerInfo(NpcStateViewModel state) =>
        state.IsDefault ? state.PlayerInfo : state.PlayerInfo ?? DefaultState.PlayerInfo;

    public IReadOnlyList<SoundLibraryItemViewModel> GetEffectiveSounds(NpcStateViewModel state) =>
        state.IsDefault || state.HasSoundsOverride ? state.Sounds : DefaultState.Sounds;

    /// <summary>Last link of the token fallback chain (explicit token image -> icon -> initials) -
    ///     initials derived from up to the first two words of the NPC's Name.</summary>
    public string ResolvedTokenInitials => string.Concat(
        Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(word => char.ToUpperInvariant(word[0])));
}
