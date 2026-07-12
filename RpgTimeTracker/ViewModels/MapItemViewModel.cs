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

    /// <summary>Per-map fog render style override, falling back to the global setting when null -
    ///     see MainWindowViewModel.GetEffectiveFogStyle/ThemeSettingsService.MapLibraryEntryDto.</summary>
    [ObservableProperty] private string? _fogColorHex;

    [ObservableProperty] private int? _fogOpacityPercent;
    [ObservableProperty] private double? _fogBlurRadius;
    [ObservableProperty] private bool? _fogBlurEnabled;

    /// <summary>Default CellSizePx for new floors added to this map - see
    ///     ThemeSettingsService.MapLibraryEntryDto.DefaultCellSizePx.</summary>
    [ObservableProperty] private int _defaultCellSizePx;

    public MapItemViewModel(
        Guid id,
        string name,
        int defaultCellSizePx,
        Action<MapItemViewModel> onDeleteRequested,
        Action<MapItemViewModel>? onChanged,
        string? fogColorHex = null,
        int? fogOpacityPercent = null,
        double? fogBlurRadius = null,
        bool? fogBlurEnabled = null)
    {
        Id = id;
        _name = name;
        _defaultCellSizePx = defaultCellSizePx;
        _fogColorHex = fogColorHex;
        _fogOpacityPercent = fogOpacityPercent;
        _fogBlurRadius = fogBlurRadius;
        _fogBlurEnabled = fogBlurEnabled;
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

    partial void OnFogColorHexChanged(string? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogOpacityPercentChanged(int? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogBlurRadiusChanged(double? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogBlurEnabledChanged(bool? value)
    {
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    /// <summary>Floors are layers of the same building/map, so their order is meaningful (unlike
    ///     the map library list itself) - the GM can reorder them via the floor list's up/down
    ///     buttons.</summary>
    public void MoveFloorUp(MapFloorItemViewModel floor)
    {
        var index = Floors.IndexOf(floor);
        if (index <= 0) return;

        Floors.Move(index, index - 1);
        _onChanged?.Invoke(this);
    }

    public void MoveFloorDown(MapFloorItemViewModel floor)
    {
        var index = Floors.IndexOf(floor);
        if (index < 0 || index >= Floors.Count - 1) return;

        Floors.Move(index, index + 1);
        _onChanged?.Invoke(this);
    }
}
