using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Shared floor-selector + reveal/brush toolbar + paintable canvas, extracted from
///     MapLiveWindow/MapPrepareWindow (which were otherwise near-identical): both windows differ
///     only in which FogMask a stroke writes into (live vs. prepare), the overlay tint, and what
///     happens with painted cells afterward (broadcast+dirty-flag vs. debounced-save) - all of
///     which stay in the owning window. This control owns floor selection, brush sizing/cursor,
///     and the pointer-to-image-space paint math only.
/// </summary>
public partial class MapEditCanvasControl : UserControl
{
    /// <summary>BrushSizeSlider.Value is a physical brush radius expressed in reference cells at
    ///     this size (the app's default cell size), not a raw cell count - so the brush's
    ///     on-screen/on-image footprint stays constant when a floor's own CellSizePx is smaller or
    ///     larger (GM-editable per floor). Without this, the same slider value would paint a much
    ///     larger or smaller physical area depending on which floor/cell size is currently active.</summary>
    private const int BrushReferenceCellSizePx = 8;

    private Func<MapFloorItemViewModel, FogMask>? _getFog;
    private Color _hiddenColor = FogOverlayRenderer.EditorHiddenColor;
    private MapFloorItemViewModel? _floor;
    private bool _isPainting;
    private Point? _lastPointerPosition;

    public MapEditCanvasControl()
    {
        InitializeComponent();

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _lastPointerPosition is { } position)
                UpdateBrushCursor(position);
        };
    }

    /// <summary>Fired after LoadFloor completes, whether from the floor selector or SetFloors -
    ///     the owner uses this to update MainWindowViewModel.EditingFloor, refresh its preview, etc.</summary>
    public event Action<MapFloorItemViewModel?>? FloorChanged;

    /// <summary>Fired synchronously after a single PaintAt call applies a non-empty set of cell
    ///     changes to the current floor's FogMask - NOT debounced, since the two owners want to
    ///     react differently (Live broadcasts cells over the network on its own debounce timer,
    ///     Prepare just marks itself dirty for a periodic file save).</summary>
    public event Action<MapFloorItemViewModel, IReadOnlyList<FogCellDto>>? CellsPainted;

    public MapFloorItemViewModel? CurrentFloor => _floor;

    /// <summary>Must be called once before SetFloors - supplies the FogMask to paint into
    ///     (MainWindowViewModel.GetLiveFog or GetPrepareFog) and the overlay tint color
    ///     distinguishing the two editors at a glance.</summary>
    public void Configure(Func<MapFloorItemViewModel, FogMask> getFog, Color hiddenColor)
    {
        _getFog = getFog;
        _hiddenColor = hiddenColor;
    }

    public void SetFloors(IEnumerable<MapFloorItemViewModel> floors, MapFloorItemViewModel? initialFloor)
    {
        FloorSelector.ItemsSource = floors;
        FloorSelector.SelectedItem = initialFloor;
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
    }

    /// <summary>Re-reads the current floor's FogMask (via the Configure'd delegate) and repaints
    ///     the overlay - for the owner to call after an external mutation it made itself (e.g.
    ///     MapLiveWindow's "Reset to Prepared", or after MainWindowViewModel.RescaleFloorCellSizeAsync
    ///     replaces the FogMask instance for the current floor).</summary>
    public void RefreshOverlay()
    {
        if (_floor is null || _getFog is null) return;

        var fog = _getFog(_floor);
        FogOverlayControl.Source = FogOverlayRenderer.BuildColoredOverlayBitmap(fog, _hiddenColor);
    }

    private void OnFloorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
    }

    private void LoadFloor(MapFloorItemViewModel? floor)
    {
        _floor = floor;
        if (floor is null)
        {
            FloorImageControl.Source = null;
            FogOverlayControl.Source = null;
            FloorChanged?.Invoke(null);
            return;
        }

        using (var stream = File.OpenRead(floor.ImagePath))
            FloorImageControl.Source = new Bitmap(stream);
        RefreshOverlay();
        FloorChanged?.Invoke(floor);
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EditorCanvas).Properties.IsLeftButtonPressed) return;

        _isPainting = true;
        PaintAt(e.GetPosition(EditorCanvas));
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(EditorCanvas);
        _lastPointerPosition = position;
        UpdateBrushCursor(position);

        if (!_isPainting) return;

        PaintAt(position);
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPainting = false;
    }

    private void OnCanvasPointerExited(object? sender, PointerEventArgs e)
    {
        _lastPointerPosition = null;
        BrushCursor.IsVisible = false;
    }

    /// <summary>Sizes/positions the brush-outline Ellipse using the same image-space ↔
    ///     control-space scale math as PaintAt, so the visible ring matches exactly what a stroke
    ///     there would affect. Positioned via RenderTransform (not Margin): Margin participates in
    ///     EditorCanvas's own layout measurement, so a large left/top margin near the canvas edges
    ///     kept growing the Panel itself and made the ScrollViewer's viewport jump around as the
    ///     cursor approached the right/bottom edge. RenderTransform is a purely visual offset
    ///     applied after layout, so it can't feed back into the panel's measured size.</summary>
    private void UpdateBrushCursor(Point position)
    {
        if (_floor is null || FloorImageControl.Source is not Bitmap bitmap)
        {
            BrushCursor.IsVisible = false;
            return;
        }

        var controlSize = EditorCanvas.Bounds.Size;
        if (controlSize.Width <= 0 || controlSize.Height <= 0)
        {
            BrushCursor.IsVisible = false;
            return;
        }

        var imageSize = bitmap.PixelSize;
        var scale = Math.Min(controlSize.Width / imageSize.Width, controlSize.Height / imageSize.Height);

        var radiusCells = GetBrushRadiusCells(_floor);
        var diameterPx = (2 * radiusCells + 1) * _floor.CellSizePx * scale;
        BrushCursor.Width = diameterPx;
        BrushCursor.Height = diameterPx;
        BrushCursor.RenderTransform = new TranslateTransform(position.X - diameterPx / 2, position.Y - diameterPx / 2);
        BrushCursor.IsVisible = true;
    }

    private int GetBrushRadiusCells(MapFloorItemViewModel floor)
    {
        return Math.Max(0, (int)Math.Round(BrushSizeSlider.Value * BrushReferenceCellSizePx / floor.CellSizePx));
    }

    private void PaintAt(Point position)
    {
        if (_floor is null || _getFog is null || FloorImageControl.Source is not Bitmap bitmap) return;

        var controlSize = EditorCanvas.Bounds.Size;
        if (controlSize.Width <= 0 || controlSize.Height <= 0) return;

        var imageSize = bitmap.PixelSize;
        var scale = Math.Min(controlSize.Width / imageSize.Width, controlSize.Height / imageSize.Height);
        var displayedWidth = imageSize.Width * scale;
        var displayedHeight = imageSize.Height * scale;
        var offsetX = (controlSize.Width - displayedWidth) / 2;
        var offsetY = (controlSize.Height - displayedHeight) / 2;

        var imageX = (position.X - offsetX) / scale;
        var imageY = (position.Y - offsetY) / scale;
        if (imageX < 0 || imageY < 0 || imageX >= imageSize.Width || imageY >= imageSize.Height) return;

        var centerX = (int)(imageX / _floor.CellSizePx);
        var centerY = (int)(imageY / _floor.CellSizePx);
        var radius = GetBrushRadiusCells(_floor);
        var revealed = RevealModeToggle.IsChecked == true;

        var fog = _getFog(_floor);
        var changedCells = new List<FogCellDto>();
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            if (x < 0 || y < 0 || x >= _floor.GridWidth || y >= _floor.GridHeight) continue;
            if (fog.IsRevealed(x, y) == revealed) continue;

            // Painted directly into the FogMask for immediate visual feedback - the owner only
            // needs the changed-cell list for whatever it does afterward (broadcast/save).
            fog.SetRevealed(x, y, revealed);
            changedCells.Add(new FogCellDto { X = x, Y = y, Revealed = revealed });
        }

        if (changedCells.Count > 0)
        {
            RefreshOverlay();
            CellsPainted?.Invoke(_floor, changedCells);
        }
    }
}
