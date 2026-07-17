using System;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models.Persistence;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One floor of a MapItemViewModel: an image plus its "starting" fog template
///     (see FogMask/FogMaskSerializer). No fog editing yet (that's a later
///     milestone's SL Map Editor Window) - here the starting fog is always fully
///     hidden and only shown as a solid placeholder over the thumbnail.
/// </summary>
public sealed partial class MapFloorItemViewModel : ObservableObject
{
    private readonly Action<MapFloorItemViewModel>? _onChanged;
    private readonly Action<MapFloorItemViewModel> _onDeleteRequested;

    /// <summary>
    ///     GM-editable per floor (see MainWindowViewModel.RescaleFloorCellSizeAsync) - a
    ///     change rescales the on-disk/cached fog masks and GridWidth/GridHeight to match.
    /// </summary>
    [ObservableProperty] private int _cellSizePx;

    [ObservableProperty] private int _gridHeight;

    [ObservableProperty] private int _gridWidth;

    [ObservableProperty] private string _name;

    public MapFloorItemViewModel(
        Guid id,
        string name,
        string imagePath,
        string fogPath,
        int cellSizePx,
        int gridWidth,
        int gridHeight,
        Bitmap? thumbnail,
        Action<MapFloorItemViewModel> onDeleteRequested,
        Action<MapFloorItemViewModel>? onChanged)
    {
        Id = id;
        _name = name;
        ImagePath = imagePath;
        FogPath = fogPath;
        _cellSizePx = cellSizePx;
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        Thumbnail = thumbnail;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public Guid Id { get; }
    public string ImagePath { get; private set; }
    public string FogPath { get; private set; }
    public Bitmap? Thumbnail { get; }

    /// <summary>
    ///     SemiPermanent/Permanent map annotation lines on this floor (see MapLineDto's doc
    ///     comment) - Temporary lines are never added here, they only ever exist as a live RPC/fade
    ///     animation. Populated on load from MapFloorEntryDto.Lines and mutated by
    ///     MainWindowViewModel's AddMapLine/RemoveMapLine/ClearMapLines.
    /// </summary>
    public ObservableCollection<MapLineDto> Lines { get; } = [];

    /// <summary>
    ///     Called only after the backing files have already been moved to their new
    ///     location - see MainWindowViewModel.MoveMapLibraryItemToScope, which moves the whole
    ///     per-map directory in one step and then updates every floor's paths to match.
    /// </summary>
    public void UpdatePaths(string newImagePath, string newFogPath)
    {
        ImagePath = newImagePath;
        FogPath = newFogPath;
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(FogPath));
    }

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