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
///     (ClientMainWindowViewModel.MapDisplay) - floor image + fog overlay + local floor
///     navigation, identical in both places. The two owners differ only in how they feed data
///     in: the PlayerClient deserializes fog from the network, the Host shares the exact same
///     FogMask instance it's painting into (see NotifyFogChanged) - see ApplyFogCells vs
///     NotifyFogChanged below.
/// </summary>
public sealed partial class MapDisplayViewModel : ObservableObject
{
    private readonly List<MapDisplayFloor> _floors = [];

    [ObservableProperty] private bool _isShowingMap;
    [ObservableProperty] private string _mapName = string.Empty;
    [ObservableProperty] private int _currentFloorIndex;
    [ObservableProperty] private Bitmap? _currentFloorImageBitmap;
    [ObservableProperty] private WriteableBitmap? _currentFloorOverlayBitmap;
    [ObservableProperty] private string _currentFloorName = string.Empty;

    /// <summary>Player-side fog render style (see issue #22) - one global GM preference, applied
    ///     via ApplyRenderStyle by both the Host (from its own settings) and the PlayerClient
    ///     (from session.snapshot/map.renderStyleChanged).</summary>
    [ObservableProperty] private Color _hiddenColor = FogOverlayRenderer.PlayerHiddenColor;

    /// <summary>Softening radius in grid cells (0 = crisp per-cell edges), baked directly into
    ///     the overlay bitmap - see FogOverlayRenderer.BuildOverlayBitmap.</summary>
    [ObservableProperty] private double _blurRadius;

    partial void OnHiddenColorChanged(Color value)
    {
        RefreshOverlay();
    }

    partial void OnBlurRadiusChanged(double value)
    {
        RefreshOverlay();
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
        CurrentFloorOverlayBitmap = null;
        CurrentFloorName = string.Empty;
    }

    /// <summary>
    ///     Applies reveal/hide cells to a floor's own FogMask and refreshes the overlay if it's
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

    private void RefreshIfCurrent(MapDisplayFloor floor)
    {
        if (_floors.IndexOf(floor) == CurrentFloorIndex) RefreshOverlay();
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
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floor = _floors[CurrentFloorIndex];
        CurrentFloorOverlayBitmap = floor.CurrentFog is null
            ? null
            : FogOverlayRenderer.BuildOverlayBitmap(floor.CurrentFog, HiddenColor, BlurRadius);
    }
}

/// <summary>One floor's data as shown by MapDisplayViewModel (not the editing/authoring shape -
///     see MapFloorItemViewModel on the Host for that).</summary>
public sealed class MapDisplayFloor
{
    public Guid FloorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }
    public FogMask? CurrentFog { get; set; }
}
