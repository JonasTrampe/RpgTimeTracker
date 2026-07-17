using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named, non-Character marker a map token can link to (a chest, a trap, a signpost) - see
///     issue #67. Follows the same top-level-library pattern as NpcLibraryItemViewModel/
///     SceneLibraryItemViewModel: deliberately does not derive from
///     LibraryItemViewModelBase&lt;TSelf&gt;, since this owns no single file of its own.
///     IconImage (a Media Library reference) and IconGlyph (a Bootstrap icon name) are mutually
///     exclusive - setting one clears the other, matching the "Icon: Media Library image, or... a
///     Bootstrap icon" wording from the original design. Neither is required; ResolvedTokenInitials
///     mirrors NpcLibraryItemViewModel's fallback chain for a marker with no icon set at all.
/// </summary>
public sealed partial class PointOfInterestLibraryItemViewModel : ObservableObject, ITaggable
{
    private readonly Action<PointOfInterestLibraryItemViewModel>? _onChanged;
    private readonly Action<PointOfInterestLibraryItemViewModel> _onDeleteRequested;

    [ObservableProperty] private string _description = string.Empty;

    /// <summary>
    ///     Markdown-authored player-facing bio/lore, mirroring NpcVariantViewModel.PlayerInfo -
    ///     distinct from Description, which is the GM-facing tooltip detail. Gated by
    ///     PlayerVisiblePlayerInfo, same pattern as Character tokens' PlayerVisiblePlayerInfo.
    /// </summary>
    [ObservableProperty] private string _playerInfo = string.Empty;

    [ObservableProperty] private MediaLibraryItemViewModel? _iconImage;

    [ObservableProperty] private string? _iconGlyph;

    [ObservableProperty] private string _name;

    [ObservableProperty] private bool _playerVisibleName;

    [ObservableProperty] private bool _playerVisibleDescription;

    [ObservableProperty] private bool _playerVisiblePlayerInfo;

    /// <summary>
    ///     Mirrors NpcVariantViewModel.IsPlayerInfoPreviewMode - whether the PlayerInfo editor
    ///     shows the raw Markdown TextBox or the rendered MarkdownScrollViewer preview.
    /// </summary>
    [ObservableProperty] private bool _isPlayerInfoPreviewMode;

    [ObservableProperty] private LibraryScope _scope;

    /// <summary>
    ///     See NpcLibraryItemViewModel._suppressChangeNotifications' doc comment - same purpose
    ///     here, set while MainWindowViewModel.FromPointOfInterestLibraryEntryDto is reconstructing
    ///     this entry from its saved DTO.
    /// </summary>
    private bool _suppressChangeNotifications;

    public PointOfInterestLibraryItemViewModel(
        Guid id,
        string name,
        Action<PointOfInterestLibraryItemViewModel> onDeleteRequested,
        Action<PointOfInterestLibraryItemViewModel>? onChanged,
        LibraryScope scope = LibraryScope.Shared,
        IEnumerable<Guid>? tagIds = null)
    {
        Id = id;
        _name = name;
        _scope = scope;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        if (tagIds is not null)
            foreach (var tagId in tagIds)
                TagIds.Add(tagId);
        TagIds.CollectionChanged += (_, _) => NotifyChanged();
    }

    public Guid Id { get; }

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    /// <summary>
    ///     Last link of the fallback chain (explicit icon image -> glyph -> initials) - initials
    ///     derived from up to the first two words of the Name, same as
    ///     NpcLibraryItemViewModel.ResolvedTokenInitials.
    /// </summary>
    public string ResolvedInitials => string.Concat(
        Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(word => char.ToUpperInvariant(word[0])));

    public ObservableCollection<Guid> TagIds { get; } = [];

    public string PlayerInfoPreviewToggleIcon => IsPlayerInfoPreviewMode ? "✎" : "👁";

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedInitials));
        NotifyChanged();
    }

    partial void OnDescriptionChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnPlayerInfoChanged(string value)
    {
        NotifyChanged();
    }

    /// <summary>Mutually exclusive with IconGlyph - see this class's doc comment.</summary>
    partial void OnIconImageChanged(MediaLibraryItemViewModel? value)
    {
        if (value is not null && IconGlyph is not null) IconGlyph = null;
        NotifyChanged();
    }

    partial void OnIconGlyphChanged(string? value)
    {
        if (value is not null && IconImage is not null) IconImage = null;
        NotifyChanged();
    }

    partial void OnPlayerVisibleNameChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisibleDescriptionChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisiblePlayerInfoChanged(bool value)
    {
        NotifyChanged();
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

    partial void OnScopeChanged(LibraryScope value)
    {
        OnPropertyChanged(nameof(IsSessionLocal));
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

    /// <summary>See NpcLibraryItemViewModel.BeginBulkLoad's doc comment - same purpose here.</summary>
    public void BeginBulkLoad()
    {
        _suppressChangeNotifications = true;
    }

    public void EndBulkLoad()
    {
        _suppressChangeNotifications = false;
    }
}
