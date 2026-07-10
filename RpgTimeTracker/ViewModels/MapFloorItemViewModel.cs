using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One floor of a MapItemViewModel: an image plus its "starting" fog template
///     (see FogMask/FogMaskSerializer). No fog editing yet (that's a later
///     milestone's SL Map Editor Window) - here the starting fog is always fully
///     hidden and only shown as a solid placeholder over the thumbnail.
/// </summary>
public sealed partial class MapFloorItemViewModel : ObservableObject
{
    private readonly Action<MapFloorItemViewModel> _onDeleteRequested;
    private readonly Action<MapFloorItemViewModel>? _onChanged;

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
        CellSizePx = cellSizePx;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Thumbnail = thumbnail;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public Guid Id { get; }
    public string ImagePath { get; }
    public string FogPath { get; }
    public int CellSizePx { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    public Bitmap? Thumbnail { get; }

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
