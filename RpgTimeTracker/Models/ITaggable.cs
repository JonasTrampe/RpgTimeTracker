using System;
using System.Collections.ObjectModel;

namespace RpgTimeTracker.Models;

/// <summary>
///     Implemented by every library item view model that carries Tag references (Media/Sound/
///     Music/Map/NPC), so LibraryPanelView's tag-filter bar can filter a heterogeneous ItemsSource
///     without knowing the concrete item type.
/// </summary>
public interface ITaggable
{
    ObservableCollection<Guid> TagIds { get; }
}