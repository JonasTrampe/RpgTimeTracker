using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One chip in a LibraryPanelView's tag-filter bar - a campaign-wide <see cref="TagViewModel"/>
///     plus whether it's currently part of the active multi-select filter. Distinct from
///     <see cref="TagOptionViewModel"/> (which toggles a single item's own tag membership); this one
///     toggles membership in the filter's selected-tags set instead.
/// </summary>
public partial class TagFilterOptionViewModel : ObservableObject
{
    private readonly Action<Guid, bool> _onToggled;

    [ObservableProperty] private bool _isSelected;

    public TagFilterOptionViewModel(TagViewModel tag, Action<Guid, bool> onToggled)
    {
        Tag = tag;
        _onToggled = onToggled;
    }

    public TagViewModel Tag { get; }

    partial void OnIsSelectedChanged(bool value) => _onToggled(Tag.Id, value);
}
