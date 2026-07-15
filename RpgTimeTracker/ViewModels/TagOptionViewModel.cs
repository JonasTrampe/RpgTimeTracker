using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One row in a <see cref="Views.Controls.TagSelectorFlyout" /> - pairs a campaign-wide
///     <see cref="TagViewModel" /> with whether the item currently being edited has it, without
///     mutating the Tag itself. Toggling calls back into the flyout to Add/Remove the Id from the
///     item's own TagIds collection.
/// </summary>
public partial class TagOptionViewModel : ObservableObject
{
    private readonly Action<Guid, bool> _onToggled;

    [ObservableProperty] private bool _isAssigned;

    public TagOptionViewModel(TagViewModel tag, bool isAssigned, Action<Guid, bool> onToggled)
    {
        Tag = tag;
        _isAssigned = isAssigned;
        _onToggled = onToggled;
    }

    public TagViewModel Tag { get; }

    partial void OnIsAssignedChanged(bool value)
    {
        _onToggled(Tag.Id, value);
    }
}