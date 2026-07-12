using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

/// <summary>
///     SL-only editor for one map's floor, opened by a floor's "Edit" button: paints only the
///     in-memory prepare-mode fog (see MainWindowViewModel.GetPrepareFog), which is debounced-
///     saved back to the floor's on-disk starting-fog file (SavePrepareFogAsync) and nothing
///     else - no network broadcast, no local-preview update, no open/close-to-players control.
///     Edits here are structurally invisible to players no matter what; MapLiveWindow's "Reset to
///     Prepared" is what actually pushes this template into the live/broadcast fog. Not bound via
///     DataContext to MainWindowViewModel like the main tabs - constructed directly with the
///     map/viewmodel it edits, matching the IconPickerWindow/MediaLibraryPickerWindow pattern for
///     focused child windows.
/// </summary>
public partial class MapPrepareWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly MapItemViewModel _map;
    private readonly DispatcherTimer _flushTimer;
    private MapFloorItemViewModel? _floor;
    private bool _isPainting;
    private bool _isDirty;
    private Point? _lastPointerPosition;

    public MapPrepareWindow(MainWindowViewModel vm, MapItemViewModel map)
    {
        InitializeComponent();

        _vm = vm;
        _map = map;
        Title = string.Format(LocalizationService.Get("MapPrepareWindow.Title"), map.Name);

        FloorSelector.ItemsSource = map.Floors;
        FloorSelector.SelectedItem = _vm.EditingFloor is not null && map.Floors.Contains(_vm.EditingFloor)
            ? _vm.EditingFloor
            : map.Floors.Count > 0 ? map.Floors[0] : null;

        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
        UpdateOpenStatusLabel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NotifyMapPrepareWindowOpened(map.Id);

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _lastPointerPosition is { } position)
                UpdateBrushCursor(position);
        };

        InitializeFogStyleControls();
        InitializeCellSizeControl();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => _ = FlushPendingAsync();
        _flushTimer.Start();
    }

    /// <summary>Seeds the fog-style controls from the map's override (falling back to the global
    ///     value when unset) and wires them to update the override live on change.</summary>
    private void InitializeFogStyleControls()
    {
        var colorHex = _map.FogColorHex ?? _vm.FogColorHex;
        FogColorPickerControl.Color = Color.TryParse(colorHex, out var parsed) ? parsed : Colors.Black;
        FogOpacitySlider.Value = _map.FogOpacityPercent ?? _vm.FogOpacityPercent;
        FogBlurEnabledCheckBox.IsChecked = _map.FogBlurEnabled ?? _vm.FogBlurEnabled;
        FogBlurRadiusSlider.Value = _map.FogBlurRadius ?? _vm.FogBlurRadius;

        FogColorPickerControl.PropertyChanged += (_, e) =>
        {
            if (e.Property == ColorView.ColorProperty)
                _map.FogColorHex = FogColorPickerControl.Color.ToString();
        };
        FogOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                _map.FogOpacityPercent = (int)FogOpacitySlider.Value;
        };
        FogBlurEnabledCheckBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
                _map.FogBlurEnabled = FogBlurEnabledCheckBox.IsChecked;
        };
        FogBlurRadiusSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                _map.FogBlurRadius = FogBlurRadiusSlider.Value;
        };
    }

    private void OnUseGlobalFogDefaultClick(object? sender, RoutedEventArgs e)
    {
        _map.FogColorHex = null;
        _map.FogOpacityPercent = null;
        _map.FogBlurRadius = null;
        _map.FogBlurEnabled = null;
        InitializeFogStyleControls();
    }

    private void InitializeCellSizeControl()
    {
        CellSizeUpDown.Value = _floor?.CellSizePx ?? _map.DefaultCellSizePx;
        UpdateCellSizeWarning();

        CellSizeUpDown.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.ValueProperty || _floor is null || CellSizeUpDown.Value is not { } value) return;

            // Kept to even multiples: FogMaskRescaler's ratio math stays clean (no accumulating
            // rounding drift) when repeatedly rescaling between even cell sizes. Setting Value
            // here re-enters this handler recursively; return immediately so the rescale below
            // only runs once, from the recursive call that sees the already-rounded value.
            var newCellSizePx = Math.Max(2, (int)Math.Round(value / 2m) * 2);
            if (newCellSizePx != (int)value)
            {
                CellSizeUpDown.Value = newCellSizePx;
                return;
            }

            UpdateCellSizeWarning();
            if (newCellSizePx != _floor.CellSizePx)
            {
                _ = _vm.RescaleFloorCellSizeAsync(_map, _floor, newCellSizePx);
                if (_lastPointerPosition is { } position) UpdateBrushCursor(position);
            }
        };
    }

    private void UpdateCellSizeWarning()
    {
        CellSizeWarningLabel.IsVisible = CellSizeUpDown.Value is { } value && value < 6;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsMapOpenToPlayers) or nameof(MainWindowViewModel.OpenMap))
            UpdateOpenStatusLabel();
    }

    private void UpdateOpenStatusLabel()
    {
        OpenStatusLabel.Text = _vm.IsMapOpenToPlayers && _vm.OpenMap == _map
            ? LocalizationService.Get("MapPrepareWindow.OpenStatusShown")
            : LocalizationService.Get("MapPrepareWindow.OpenStatusHidden");
    }

    private void OnFloorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = FlushPendingAsync();
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
        CellSizeUpDown.Value = _floor?.CellSizePx ?? _map.DefaultCellSizePx;
        UpdateCellSizeWarning();
    }

    private void LoadFloor(MapFloorItemViewModel? floor)
    {
        _floor = floor;
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

        var fog = _vm.GetPrepareFog(_floor);
        FogOverlayControl.Source = FogOverlayRenderer.BuildColoredOverlayBitmap(fog, FogOverlayRenderer.PrepareHiddenColor);
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

    /// <summary>BrushSizeSlider.Value is a physical brush radius expressed in reference cells at
    ///     BrushReferenceCellSizePx (the app's default cell size), not a raw cell count - so the
    ///     brush's on-screen/on-image footprint stays constant when a floor's own CellSizePx is
    ///     smaller or larger (GM-editable per floor, see the CellSizeUpDown control below).
    ///     Without this, the same slider value would paint a much larger or smaller physical area
    ///     depending on which floor/cell size is currently active.</summary>
    private const int BrushReferenceCellSizePx = 8;

    private int GetBrushRadiusCells(MapFloorItemViewModel floor)
    {
        return Math.Max(0, (int)Math.Round(BrushSizeSlider.Value * BrushReferenceCellSizePx / floor.CellSizePx));
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
        var radius = GetBrushRadiusCells(_floor);
        var revealed = RevealModeToggle.IsChecked == true;

        var fog = _vm.GetPrepareFog(_floor);
        var changed = false;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            if (x < 0 || y < 0 || x >= _floor.GridWidth || y >= _floor.GridHeight) continue;
            if (fog.IsRevealed(x, y) == revealed) continue;

            fog.SetRevealed(x, y, revealed);
            changed = true;
        }

        if (changed)
        {
            RefreshOverlay();
            _isDirty = true;
        }
    }

    private async System.Threading.Tasks.Task FlushPendingAsync()
    {
        if (!_isDirty || _floor is null) return;

        _isDirty = false;
        await _vm.SavePrepareFogAsync(_floor);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.NotifyMapPrepareWindowClosed(_map.Id);
        _flushTimer.Stop();
        _ = FlushPendingAsync();
        base.OnClosed(e);
    }
}
