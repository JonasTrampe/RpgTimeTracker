using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Common base for MediaLibraryItemViewModel and SoundLibraryItemViewModel: both are
///     a named, locally stored filesystem item with rename/delete/change
///     notification - everything beyond that (thumbnail/kind/loop for media; volume/
///     repeats/trim for sounds) is deliberately kept in the respective derived classes, since
///     it differs too much in substance to be usefully shared.
///     CRTP (TSelf = the concrete derived class) instead of a non-generic
///     Action&lt;LibraryItemViewModelBase&gt;, so that the existing, concretely typed
///     onDeleteRequested/onChanged callback methods in MainWindowViewModel (e.g.
///     RemoveMediaLibraryItem(MediaLibraryItemViewModel)) still fit unchanged via method group
///     as a constructor argument.
/// </summary>
public abstract partial class LibraryItemViewModelBase<TSelf> : ObservableObject
    where TSelf : LibraryItemViewModelBase<TSelf>
{
    private readonly Action<TSelf>? _onChanged;
    private readonly Action<TSelf> _onDeleteRequested;

    [ObservableProperty] private string _name;

    protected LibraryItemViewModelBase(
        Guid id,
        string name,
        string localPath,
        string mimeType,
        Action<TSelf> onDeleteRequested,
        Action<TSelf>? onChanged,
        LibraryScope scope = LibraryScope.Shared)
    {
        Id = id;
        _name = name;
        LocalPath = localPath;
        MimeType = mimeType;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        _scope = scope;
    }

    /// <summary>Stable identity for this library item - added so it can be referenced by value
    ///     (e.g. by an NPC's portrait/sound reference, or the usage registry) instead of by its
    ///     mutable Name/LocalPath, matching the precedent already set by MusicLibraryItemViewModel/
    ///     MapItemViewModel.</summary>
    public Guid Id { get; }

    /// <summary>Settable (not get-only) so a "move to Shared"/"move to this Session" action can
    ///     update it in place once the underlying file has actually been moved on disk - see
    ///     MainWindowViewModel's MoveMediaLibraryItemToScope/MoveSoundLibraryItemToScope.</summary>
    public string LocalPath { get; private set; }

    public string MimeType { get; }

    /// <summary>Called only after the backing file has already been moved to its new location.</summary>
    public void UpdateLocalPath(string newLocalPath)
    {
        LocalPath = newLocalPath;
        OnPropertyChanged(nameof(LocalPath));
    }

    /// <summary>Whether this item lives in the always-present Shared Library or inside the
    ///     currently open Session's own folder - see LibraryScope. Settable (not get-only) since
    ///     a "move to Shared"/"move to this Session" action changes it in place rather than
    ///     requiring the item to be recreated.</summary>
    [ObservableProperty] private LibraryScope _scope;

    partial void OnScopeChanged(LibraryScope value)
    {
        NotifyChanged();
    }

    partial void OnNameChanged(string value)
    {
        NotifyChanged();
    }

    /// <summary>
    ///     For derived classes: their own change-triggering properties (e.g. Loop,
    ///     Volume, Trim) also call this via this method, since _onChanged remains encapsulated here.
    /// </summary>
    protected void NotifyChanged()
    {
        _onChanged?.Invoke((TSelf)this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested((TSelf)this);
    }

    /// <summary>
    ///     Called by MainWindowViewModel on LocalizationService.LanguageChanged - forces every
    ///     bound property (e.g. a derived class's KindLabel) to be re-read in the new language,
    ///     since a language switch doesn't otherwise raise property-changed on its own.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }
}