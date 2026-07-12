using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

/// <summary>
///     SL-only fog editor for one map, opened by its "Show" button: brush reveal/hide directly
///     into the live/broadcast fog (see MainWindowViewModel.GetLiveFog), "Reset to Prepared"
///     (pulls MapPrepareWindow's saved template into live), and open/close to players. Not bound
///     via DataContext to MainWindowViewModel like the main tabs - constructed directly with the
///     map/viewmodel it edits, matching the IconPickerWindow/MediaLibraryPickerWindow pattern for
///     focused child windows.
/// </summary>
public partial class MapLiveWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly MapItemViewModel _map;
    private readonly List<FogCellDto> _pendingCells = [];
    private readonly DispatcherTimer _flushTimer;
    private MapFloorItemViewModel? _floor;
    private bool _isPainting;
    private Point? _lastPointerPosition;

    public MapLiveWindow(MainWindowViewModel vm, MapItemViewModel map)
    {
        InitializeComponent();

        _vm = vm;
        _map = map;
        Title = string.Format(LocalizationService.Get("MapLiveWindow.Title"), map.Name);

        FloorSelector.ItemsSource = map.Floors;
        FloorSelector.SelectedItem = _vm.EditingFloor is not null && map.Floors.Contains(_vm.EditingFloor)
            ? _vm.EditingFloor
            : map.Floors.Count > 0 ? map.Floors[0] : null;
        OpenToPlayersToggle.IsChecked = _vm.IsMapOpenToPlayers && _vm.OpenMap == map;

        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _lastPointerPosition is { } position)
                UpdateBrushCursor(position);
        };

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => _ = FlushPendingAsync();
        _flushTimer.Start();
    }

    private void OnFloorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
    }

    private void LoadFloor(MapFloorItemViewModel? floor)
    {
        _floor = floor;
        _vm.EditingFloor = floor;
        if (floor is null)
        {
            FloorImageControl.Source = null;
            FogOverlayControl.Source = null;
            return;
        }

        using var stream = File.OpenRead(floor.ImagePath);
        FloorImageControl.Source = new Bitmap(stream);
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        if (_floor is null) return;

        var fog = _vm.GetLiveFog(_floor);
        FogOverlayControl.Source = FogOverlayRenderer.BuildColoredOverlayBitmap(fog, FogOverlayRenderer.EditorHiddenColor);
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
        _ = FlushPendingAsync();
    }

    private void OnCanvasPointerExited(object? sender, PointerEventArgs e)
    {
        _lastPointerPosition = null;
        BrushCursor.IsVisible = false;
    }

    /// <summary>Sizes/positions the brush-outline Ellipse using the same image-space ↔
    ///     control-space scale math as PaintAt, so the visible ring matches exactly what a stroke
    ///     there would affect.</summary>
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

        var radiusCells = (int)BrushSizeSlider.Value;
        var diameterPx = (2 * radiusCells + 1) * _floor.CellSizePx * scale;
        BrushCursor.Width = diameterPx;
        BrushCursor.Height = diameterPx;
        BrushCursor.Margin = new Thickness(position.X - diameterPx / 2, position.Y - diameterPx / 2, 0, 0);
        BrushCursor.IsVisible = true;
    }

    private void PaintAt(Point position)
    {
        if (_floor is null || FloorImageControl.Source is not Bitmap bitmap) return;

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
        var radius = (int)BrushSizeSlider.Value;
        var revealed = RevealModeToggle.IsChecked == true;

        var fog = _vm.GetLiveFog(_floor);
        var changed = false;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            if (x < 0 || y < 0 || x >= _floor.GridWidth || y >= _floor.GridHeight) continue;
            if (fog.IsRevealed(x, y) == revealed) continue;

            // Painted directly into the live FogMask for immediate visual feedback - FlushPendingAsync
            // only needs to broadcast the already-applied cells to connected players.
            fog.SetRevealed(x, y, revealed);
            _pendingCells.Add(new FogCellDto { X = x, Y = y, Revealed = revealed });
            changed = true;
        }

        if (changed)
        {
            RefreshOverlay();
            // Same FogMask instance as the local player-window preview (MapDisplay) already
            // uses - just tell it to re-render, no cell list to hand over.
            _vm.MapDisplay.NotifyFogChanged(_floor.Id);
        }
    }

    private async Task FlushPendingAsync()
    {
        if (_pendingCells.Count == 0 || _floor is null) return;

        var cells = new List<FogCellDto>(_pendingCells);
        _pendingCells.Clear();
        await _vm.BroadcastFogCellsAsync(_floor.Id, cells);
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_floor is null) return;

        await _vm.ResetFloorFogToStartingAsync(_floor);
        RefreshOverlay();
    }

    private async void OnToggleOpenClick(object? sender, RoutedEventArgs e)
    {
        if (OpenToPlayersToggle.IsChecked == true)
            await _vm.OpenMapToPlayersCommand.ExecuteAsync(_map);
        else
            await _vm.CloseMapToPlayersCommand.ExecuteAsync(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _flushTimer.Stop();
        base.OnClosed(e);
    }
}
