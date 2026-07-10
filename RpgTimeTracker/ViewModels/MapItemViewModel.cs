using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named map (e.g. a house), made of one or more floors (MapFloorItemViewModel).
///     Deliberately does not derive from LibraryItemViewModelBase&lt;TSelf&gt; despite the
///     naming similarity to MediaLibraryItemViewModel/SoundLibraryItemViewModel - that
///     base class bakes in a single LocalPath/MimeType per item, which doesn't fit a
///     map (a folder of several floor images, not one file). Rename/delete are
///     re-implemented directly here instead, following the same shape.
/// </summary>
public sealed partial class MapItemViewModel : ObservableObject
{
    private readonly Action<MapItemViewModel> _onDeleteRequested;
    private readonly Action<MapItemViewModel>? _onChanged;

    [ObservableProperty] private string _name;

    public MapItemViewModel(
        Guid id,
        string name,
        Action<MapItemViewModel> onDeleteRequested,
        Action<MapItemViewModel>? onChanged)
    {
        Id = id;
        _name = name;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        Floors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFloors));
    }

    public Guid Id { get; }

    public ObservableCollection<MapFloorItemViewModel> Floors { get; } = [];

    public bool HasNoFloors => Floors.Count == 0;

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}
