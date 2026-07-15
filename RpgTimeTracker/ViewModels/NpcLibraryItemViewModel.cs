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
public sealed partial class NpcLibraryItemViewModel : ObservableObject, ITaggable
{
    private readonly Action<NpcLibraryItemViewModel> _onDeleteRequested;
    private readonly Action<NpcLibraryItemViewModel>? _onChanged;

    /// <summary>Set while MainWindowViewModel.FromNpcLibraryEntryDto is reconstructing this
    ///     Character from its saved DTO - suppresses NotifyChanged (and therefore
    ///     SaveNpcLibrarySettings) while GmInfoBlocks/Variants/ActiveVariant are being populated,
    ///     since at that point this instance isn't registered in MainWindowViewModel.NpcLibrary
    ///     yet. Without this, every AddVariant/property-set call during load would trigger a save
    ///     that serializes the in-memory NpcLibrary as it stood *before* this (and any later)
    ///     Character was added - on every app launch, this silently truncated the last Character
    ///     in the Shared library from settings.json. See BeginBulkLoad/EndBulkLoad.</summary>
    private bool _suppressChangeNotifications;

    [ObservableProperty] private string _name;
    [ObservableProperty] private LibraryScope _scope;
    [ObservableProperty] private NpcVariantViewModel? _activeVariant;

    public Guid Id { get; }

    /// <summary>Shared across all Variants - private GM reference notes, not per-mood.</summary>
    public ObservableCollection<NpcGmInfoBlockViewModel> GmInfoBlocks { get; } = [];

    public ObservableCollection<NpcVariantViewModel> Variants { get; } = [];

    public NpcVariantViewModel DefaultVariant => Variants.First(v => v.IsDefault);

    /// <summary>Whether this Character has any variant beyond the always-present Default one -
    ///     bound by the Characters tab to hide the Variants tab strip (and the ActiveVariant name
    ///     label in the compact header) until there's actually a choice to make; with only the
    ///     Default variant, the editor below already IS that variant's editor, so naming/switching
    ///     UI would just be redundant chrome.</summary>
    public bool HasMultipleVariants => Variants.Count > 1;

    /// <summary>Whether the currently active variant can be deleted (the Default variant never
    ///     can) - bound by the Characters tab's Variants section header to show/hide its delete
    ///     button, which sits there (not in the per-variant editor) precisely so toggling its
    ///     visibility never shifts the Add button next to it.</summary>
    public bool CanDeleteActiveVariant => ActiveVariant is { IsDefault: false };

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    /// <summary>Freeform Tag Ids attached to this character (see Tag) - separate from Scene
    ///     membership, a different, explicit mechanism.</summary>
    public ObservableCollection<Guid> TagIds { get; } = [];

    public NpcLibraryItemViewModel(
        Guid id,
        string name,
        Action<NpcLibraryItemViewModel> onDeleteRequested,
        Action<NpcLibraryItemViewModel>? onChanged,
        LibraryScope scope = LibraryScope.Shared,
        IEnumerable<Guid>? tagIds = null)
    {
        Id = id;
        _name = name;
        _scope = scope;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        if (tagIds is not null) foreach (var tagId in tagIds) TagIds.Add(tagId);
        TagIds.CollectionChanged += (_, _) => NotifyChanged();
    }

    partial void OnNameChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnScopeChanged(LibraryScope value)
    {
        OnPropertyChanged(nameof(IsSessionLocal));
        NotifyChanged();
    }

    partial void OnActiveVariantChanged(NpcVariantViewModel? value)
    {
        OnPropertyChanged(nameof(CanDeleteActiveVariant));
        NotifyChanged();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    private void NotifyChanged()
    {
        if (!_suppressChangeNotifications) _onChanged?.Invoke(this);
    }

    /// <summary>See _suppressChangeNotifications' doc comment. Must always be paired with a
    ///     matching EndBulkLoad(), even if population fails/returns early.</summary>
    public void BeginBulkLoad() => _suppressChangeNotifications = true;

    public void EndBulkLoad() => _suppressChangeNotifications = false;

    // ==================== Variants ====================

    public NpcVariantViewModel AddVariant(string name, bool isDefault = false, Guid? id = null)
    {
        var variant = new NpcVariantViewModel(id ?? Guid.NewGuid(), name, isDefault, RemoveVariant, _ => NotifyChanged());
        Variants.Add(variant);
        if (isDefault || Variants.Count == 1) ActiveVariant ??= variant;
        OnPropertyChanged(nameof(HasMultipleVariants));
        NotifyChanged();
        return variant;
    }

    /// <summary>The Default variant can't be removed - a no-op if attempted, since
    ///     NpcVariantViewModel's own Delete command has no way to refuse and stay silent otherwise.</summary>
    private void RemoveVariant(NpcVariantViewModel variant)
    {
        if (variant.IsDefault) return;

        Variants.Remove(variant);
        if (ActiveVariant == variant) ActiveVariant = DefaultVariant;
        OnPropertyChanged(nameof(HasMultipleVariants));
        NotifyChanged();
    }

    // ==================== GM info blocks ====================

    public void AddGmInfoBlock(string title)
    {
        GmInfoBlocks.Add(new NpcGmInfoBlockViewModel(title, string.Empty, RemoveGmInfoBlock, _ => NotifyChanged()));
        NotifyChanged();
    }

    private void RemoveGmInfoBlock(NpcGmInfoBlockViewModel block)
    {
        GmInfoBlocks.Remove(block);
        NotifyChanged();
    }

    /// <summary>GM info blocks are ordered - mirrors MapItemViewModel.MoveFloorUp/MoveFloorDown.</summary>
    public void MoveGmInfoBlockUp(NpcGmInfoBlockViewModel block)
    {
        var index = GmInfoBlocks.IndexOf(block);
        if (index <= 0) return;

        GmInfoBlocks.Move(index, index - 1);
        NotifyChanged();
    }

    public void MoveGmInfoBlockDown(NpcGmInfoBlockViewModel block)
    {
        var index = GmInfoBlocks.IndexOf(block);
        if (index < 0 || index >= GmInfoBlocks.Count - 1) return;

        GmInfoBlocks.Move(index, index + 1);
        NotifyChanged();
    }

    // ==================== Effective (fallback-to-Default) accessors ====================
    // Same pattern as MainWindowViewModel.GetEffectiveFogStyle (per-map override falling back to
    // a global default) - here the "default" is local to the NPC (its Default variant) rather than
    // a global setting.

    public MediaLibraryItemViewModel? GetEffectiveImage(NpcVariantViewModel variant) =>
        variant.IsDefault ? variant.Image : variant.Image ?? DefaultVariant.Image;

    public MediaLibraryItemViewModel? GetEffectiveTokenImage(NpcVariantViewModel variant) =>
        variant.IsDefault ? variant.TokenImage : variant.TokenImage ?? DefaultVariant.TokenImage;

    public string? GetEffectiveTokenIcon(NpcVariantViewModel variant) =>
        variant.IsDefault ? variant.TokenIcon : variant.TokenIcon ?? DefaultVariant.TokenIcon;

    public string? GetEffectivePlayerInfo(NpcVariantViewModel variant) =>
        variant.IsDefault ? variant.PlayerInfo : variant.PlayerInfo ?? DefaultVariant.PlayerInfo;

    public IReadOnlyList<SoundLibraryItemViewModel> GetEffectiveSounds(NpcVariantViewModel variant) =>
        variant.IsDefault || variant.HasSoundsOverride ? variant.Sounds : DefaultVariant.Sounds;

    /// <summary>Last link of the token fallback chain (explicit token image -> icon -> initials) -
    ///     initials derived from up to the first two words of the NPC's Name.</summary>
    public string ResolvedTokenInitials => string.Concat(
        Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(word => char.ToUpperInvariant(word[0])));
}
