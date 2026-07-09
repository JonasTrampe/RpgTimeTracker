using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
        string name,
        string localPath,
        string mimeType,
        Action<TSelf> onDeleteRequested,
        Action<TSelf>? onChanged)
    {
        _name = name;
        LocalPath = localPath;
        MimeType = mimeType;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public string LocalPath { get; }
    public string MimeType { get; }

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
}