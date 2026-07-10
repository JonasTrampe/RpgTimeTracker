using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Shared "what does a map look like right now" display state, used by both the Host's local
///     player-window preview (MainWindowViewModel.MapDisplay) and the real PlayerClient
///     (ClientMainWindowViewModel.MapDisplay) - floor image + fog cutout + local floor
///     navigation, identical in both places. The two owners differ only in how they feed data
///     in: the PlayerClient deserializes fog from the network, the Host shares the exact same
///     FogMask instance it's painting into (see NotifyFogChanged) - see ApplyFogCells vs
///     NotifyFogChanged below.
///
///     Rendering model (see MapDisplayView.axaml): the sharp floor image is always shown;
///     hidden cells are cut out of a second, blurred copy of the *same* image plus a color-tint
///     layer, both clipped via MaskBrush (an opacity mask built from the live FogMask, see
///     FogOverlayRenderer.BuildMaskBitmap). Blurring the actual map content (rather than a flat
///     fog-colored blob) is what issue #22's blur setting visibly does now - blurring a one-
///     pixel-per-cell colored bitmap was next to invisible once stretched across a large map.
/// </summary>
public sealed partial class MapDisplayViewModel : ObservableObject
{
    private readonly List<MapDisplayFloor> _floors = [];

    [ObservableProperty] private bool _isShowingMap;
    [ObservableProperty] private string _mapName = string.Empty;
    [ObservableProperty] private int _currentFloorIndex;
    [ObservableProperty] private Bitmap? _currentFloorImageBitmap;
    [ObservableProperty] private IBrush? _maskBrush;
    [ObservableProperty] private string _currentFloorName = string.Empty;

    /// <summary>Player-side fog render style (see issue #22) - one global GM preference, applied
    ///     via ApplyRenderStyle by both the Host (from its own settings) and the PlayerClient
    ///     (from session.snapshot/map.renderStyleChanged).</summary>
    [ObservableProperty] private Color _hiddenColor = FogOverlayRenderer.PlayerHiddenColor;

    /// <summary>Blur radius (device-independent pixels) applied to the blurred map-image layer -
    ///     bound directly to a BlurEffect in MapDisplayView.axaml, no bitmap rebuild needed.</summary>
    [ObservableProperty] private double _blurRadius;

    /// <summary>Solid-color brush for the tint layer, kept in sync with HiddenColor.</summary>
    [ObservableProperty] private IBrush _tintBrush = new SolidColorBrush(FogOverlayRenderer.PlayerHiddenColor);

    partial void OnHiddenColorChanged(Color value)
    {
        TintBrush = new SolidColorBrush(value);
    }

    public void ApplyRenderStyle(Color color, double blurRadius)
    {
        HiddenColor = color;
        BlurRadius = blurRadius;
    }

    public bool HasMultipleFloors => _floors.Count > 1;

    [RelayCommand]
    private void PreviousFloor()
    {
        Navigate(-1);
    }

    [RelayCommand]
    private void NextFloor()
    {
        Navigate(1);
    }

    public void ShowMap(string mapName, IReadOnlyList<MapDisplayFloor> floors)
    {
        _floors.Clear();
        _floors.AddRange(floors);
        MapName = mapName;
        CurrentFloorIndex = 0;
        IsShowingMap = true;
        OnPropertyChanged(nameof(HasMultipleFloors));
        DisplayCurrentFloor();
    }

    public void HideMap()
    {
        IsShowingMap = false;
        _floors.Clear();
        CurrentFloorImageBitmap = null;
        MaskBrush = null;
        CurrentFloorName = string.Empty;
    }

    /// <summary>
    ///     Applies reveal/hide cells to a floor's own FogMask and refreshes the mask if it's
    ///     currently displayed - for a caller whose FogMask is an independent copy that must be
    ///     told about each change explicitly (the PlayerClient, after deserializing from the
    ///     network).
    /// </summary>
    public void ApplyFogCells(Guid floorId, IEnumerable<FogCellDto> cells)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor?.CurrentFog is null) return;

        foreach (var cell in cells) floor.CurrentFog.SetRevealed(cell.X, cell.Y, cell.Revealed);
        RefreshIfCurrent(floor);
    }

    public void ResetFloorFog(Guid floorId, FogMask startingFog)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is null) return;

        floor.CurrentFog = startingFog;
        RefreshIfCurrent(floor);
    }

    /// <summary>
    ///     Tells the display to re-render a floor because its FogMask was already mutated
    ///     directly - for a caller that shares the exact same FogMask instance (the Host, whose
    ///     MapEditorWindow paints straight into MainWindowViewModel.GetLiveFog's object).
    /// </summary>
    public void NotifyFogChanged(Guid floorId)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is not null) RefreshIfCurrent(floor);
    }

    /// <summary>
    ///     Updates a floor's map image after the fact - covers the case where the image arrives
    ///     (or is decoded) after ShowMap already ran for this map, so the display self-heals
    ///     instead of staying blank until the next full resync.
    /// </summary>
    public void UpdateFloorImage(Guid floorId, Bitmap? image)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is null) return;

        floor.Image = image;
        if (_floors.IndexOf(floor) == CurrentFloorIndex) CurrentFloorImageBitmap = image;
    }

    private void RefreshIfCurrent(MapDisplayFloor floor)
    {
        if (_floors.IndexOf(floor) == CurrentFloorIndex) RefreshMask();
    }

    private void Navigate(int direction)
    {
        if (_floors.Count == 0) return;

        CurrentFloorIndex = (CurrentFloorIndex + direction + _floors.Count) % _floors.Count;
        DisplayCurrentFloor();
    }

    private void DisplayCurrentFloor()
    {
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floor = _floors[CurrentFloorIndex];
        CurrentFloorName = floor.Name;
        CurrentFloorImageBitmap = floor.Image;
        RefreshMask();
    }

    private void RefreshMask()
    {
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floor = _floors[CurrentFloorIndex];
        MaskBrush = floor.CurrentFog is null
            ? null
            : new ImageBrush(FogOverlayRenderer.BuildMaskBitmap(floor.CurrentFog)) { Stretch = Stretch.Fill };
    }
}

/// <summary>One floor's data as shown by MapDisplayViewModel (not the editing/authoring shape -
///     see MapFloorItemViewModel on the Host for that).</summary>
public sealed class MapDisplayFloor
{
    public Guid FloorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Bitmap? Image { get; set; }
    public FogMask? CurrentFog { get; set; }
}
