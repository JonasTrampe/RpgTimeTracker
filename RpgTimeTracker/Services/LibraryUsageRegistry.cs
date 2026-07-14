using System;
using System.Collections.Generic;
using System.Linq;

namespace RpgTimeTracker.Services;

/// <summary>
///     A small, generalized "who references this library item" lookup - replaces the old
///     Media-only, path-matching FindTriggerMediaUsageLabels/ClearTriggerMediaReferences with a
///     mechanism any consumer (trigger media configs, Playlists, and later a Characters/NPC
///     library) can register into once, instead of each library reinventing its own bespoke
///     usage-scanner. Deletion flows (see MainWindowViewModel.RemoveMediaLibraryItemAsync/
///     RemoveSoundLibraryItem/RemoveMusicLibraryItem) call FindUsagesOf before actually deleting
///     an item, and ClearReferencesTo if the GM chooses to proceed anyway.
/// </summary>
public sealed class LibraryUsageRegistry
{
    private readonly List<Func<Guid, IEnumerable<string>>> _finders = [];
    private readonly List<Action<Guid>> _clearActions = [];

    /// <summary>Registers a finder returning a display label per place that currently references
    ///     the given library item Id (empty if none).</summary>
    public void Register(Func<Guid, IEnumerable<string>> findUsages)
    {
        _finders.Add(findUsages);
    }

    /// <summary>Registers an action that unsets every reference to the given library item Id.</summary>
    public void RegisterClearAction(Action<Guid> clearReferences)
    {
        _clearActions.Add(clearReferences);
    }

    public IReadOnlyList<string> FindUsagesOf(Guid id)
    {
        return _finders.SelectMany(find => find(id)).ToList();
    }

    public void ClearReferencesTo(Guid id)
    {
        foreach (var clear in _clearActions) clear(id);
    }
}
