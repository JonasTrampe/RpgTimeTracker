using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Shared floor-selector + reveal/brush/zoom toolbar + paintable canvas, extracted from
///     MapLiveWindow/MapPrepareWindow (which were otherwise near-identical): both windows differ
///     only in which FogMask a stroke writes into (live vs. prepare), the overlay tint, and what
///     happens with painted cells afterward (broadcast+dirty-flag vs. debounced-save) - all of
///     which stay in the owning window. This control owns floor selection, brush sizing/cursor,
///     zoom, and the pointer-to-image-space paint math only.
/// </summary>
public partial class MapEditCanvasControl : UserControl
{
    /// <summary>
    ///     BrushSizeSlider.Value is a physical brush radius expressed in reference cells at
    ///     this size (the app's default cell size), not a raw cell count - so the brush's
    ///     on-screen/on-image footprint stays constant when a floor's own CellSizePx is smaller or
    ///     larger (GM-editable per floor). Without this, the same slider value would paint a much
    ///     larger or smaller physical area depending on which floor/cell size is currently active.
    /// </summary>
    private const int BrushReferenceCellSizePx = 8;

    private const double ZoomMin = 0.25;
    private const double ZoomMax = 4.0;
    private const double ZoomStep = 0.25;

    private const double TokenMarkerSizePx = 32;

    private Func<MapFloorItemViewModel, FogMask>? _getFog;
    private Color _hiddenColor = FogOverlayRenderer.EditorHiddenColor;
    private bool _isPainting;
    private Point? _lastPointerPosition;

    private MainWindowViewModel? _vm;
    private MapItemViewModel? _map;
    private MapTokenViewModel? _draggingToken;
    private bool _dragMoved;

    /// <summary>
    ///     Image-pixels-to-screen-pixels scale currently in effect - EditorCanvas is given
    ///     this exact explicit size (imageSize * _zoom), so unlike before there's no separate
    ///     "letterboxing" offset to account for in the paint/cursor math: the Panel's bounds
    ///     always match the image exactly.
    /// </summary>
    private double _zoom = 1.0;

    /// <summary>
    ///     Whether zoom should keep auto-fitting the viewport (the original Stretch="Uniform"
    ///     behavior) - true until the GM manually zooms in/out, at which point it stays fixed even
    ///     if the window is resized, matching how a real image editor's explicit zoom behaves.
    /// </summary>
    private bool _zoomIsFit = true;

    public MapEditCanvasControl()
    {
        InitializeComponent();

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _lastPointerPosition is { } position)
                UpdateBrushCursor(position);
        };

        ScrollHost.SizeChanged += (_, _) =>
        {
            if (_zoomIsFit) ApplyZoom(ComputeFitZoom(), true);
        };
    }

    public MapFloorItemViewModel? CurrentFloor { get; private set; }

    /// <summary>
    ///     Fired after LoadFloor completes, whether from the floor selector or SetFloors -
    ///     the owner uses this to update MainWindowViewModel.EditingFloor, refresh its preview, etc.
    /// </summary>
    public event Action<MapFloorItemViewModel?>? FloorChanged;

    /// <summary>
    ///     Fired synchronously after a single PaintAt call applies a non-empty set of cell
    ///     changes to the current floor's FogMask - NOT debounced, since the two owners want to
    ///     react differently (Live broadcasts cells over the network on its own debounce timer,
    ///     Prepare just marks itself dirty for a periodic file save).
    /// </summary>
    public event Action<MapFloorItemViewModel, IReadOnlyList<FogCellDto>>? CellsPainted;

    /// <summary>
    ///     Must be called once before SetFloors - supplies the FogMask to paint into
    ///     (MainWindowViewModel.GetLiveFog or GetPrepareFog) and the overlay tint color
    ///     distinguishing the two editors at a glance.
    /// </summary>
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

    /// <summary>
    ///     Re-reads the current floor's FogMask (via the Configure'd delegate) and repaints
    ///     the overlay - for the owner to call after an external mutation it made itself (e.g.
    ///     MapLiveWindow's "Reset to Prepared", or after MainWindowViewModel.RescaleFloorCellSizeAsync
    ///     replaces the FogMask instance for the current floor).
    /// </summary>
    public void RefreshOverlay()
    {
        if (CurrentFloor is null || _getFog is null) return;

        var fog = _getFog(CurrentFloor);
        FogOverlayControl.Source = FogOverlayRenderer.BuildColoredOverlayBitmap(fog, _hiddenColor);
    }

    /// <summary>
    ///     Fired when a token marker is clicked (press+release without a drag) - the owner
    ///     uses this to highlight the matching row in its own token list panel (#32); this
    ///     control has no side panel of its own.
    /// </summary>
    public event Action<MapTokenViewModel>? TokenSelected;

    /// <summary>
    ///     Must be called once (any time before/after SetMap) - the resolver methods on
    ///     MainWindowViewModel (ResolveTokenName/Portrait/IconGlyph/Initials) are how a linked
    ///     token's marker knows what to render without this control needing its own copy of the
    ///     Character/PointOfInterest lookup logic.
    /// </summary>
    public void ConfigureTokens(MainWindowViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>
    ///     Tracks which Map's Tokens this control currently renders - re-subscribes to
    ///     CollectionChanged so adding/removing a token from anywhere (this control's own drag
    ///     handling, or a MapTokenPanelView row) re-renders the overlay.
    /// </summary>
    public void SetMap(MapItemViewModel? map)
    {
        if (_map is not null) _map.Tokens.CollectionChanged -= OnTokensCollectionChanged;

        _map = map;
        if (_map is not null) _map.Tokens.CollectionChanged += OnTokensCollectionChanged;

        RenderTokens();
    }

    /// <summary>
    ///     Re-renders the token overlay - call after mutating a token from outside this
    ///     control (e.g. MapTokenPanelView's "move to floor"/reveal-mode editors), since only
    ///     Tokens' own Add/Remove is observed automatically (see SetMap).
    /// </summary>
    public void RefreshTokens()
    {
        RenderTokens();
    }

    private void OnTokensCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderTokens();
    }

    private void RenderTokens()
    {
        TokenLayer.Children.Clear();
        if (_map is null || CurrentFloor is null || _vm is null) return;

        foreach (var token in _map.Tokens.Where(t => t.FloorId == CurrentFloor.Id))
            TokenLayer.Children.Add(CreateTokenMarker(token));
    }

    private Control CreateTokenMarker(MapTokenViewModel token)
    {
        var portrait = _vm!.ResolveTokenPortrait(token);
        var iconGlyph = _vm.ResolveTokenIconGlyph(token);

        Control content = portrait?.Thumbnail is { } thumbnail
            ? new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            : iconGlyph is not null
                ? new Avalonia.Controls.Shapes.Path
                {
                    Data = VisualItemHelper.IconGeometry(iconGlyph), Fill = Brushes.White, Stretch = Stretch.Uniform,
                    Width = TokenMarkerSizePx * 0.55, Height = TokenMarkerSizePx * 0.55
                }
                : new TextBlock
                {
                    Text = _vm.ResolveTokenInitials(token), FontSize = 12, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };

        var marker = new Border
        {
            Width = TokenMarkerSizePx,
            Height = TokenMarkerSizePx,
            CornerRadius = new CornerRadius(TokenMarkerSizePx / 2),
            ClipToBounds = true,
            BorderBrush = ResolveMarkerBorderBrush(token),
            BorderThickness = new Thickness(2),
            Background = Brushes.Black,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content
        };
        ToolTip.SetTip(marker, BuildTokenTooltip(token));

        marker.PointerPressed += (_, e) => OnTokenPointerPressed(token, marker, e);
        marker.PointerMoved += (_, e) => OnTokenPointerMoved(token, marker, e);
        marker.PointerReleased += (_, e) => OnTokenPointerReleased(token, e);

        PositionMarker(marker, token);
        return marker;
    }

    /// <summary>
    ///     A quick color cue for the token's Reveal mode (#31's TokenRevealMode) - green for
    ///     AlwaysVisible, amber for HiddenUntilRevealed, red for GmOnly - so the GM can tell at a
    ///     glance without opening the settings flyout.
    /// </summary>
    private static IBrush ResolveMarkerBorderBrush(MapTokenViewModel token)
    {
        return token.RevealMode switch
        {
            TokenRevealMode.AlwaysVisible => Brushes.LimeGreen,
            TokenRevealMode.HiddenUntilRevealed => Brushes.Orange,
            TokenRevealMode.GmOnly => Brushes.OrangeRed,
            _ => Brushes.Gray
        };
    }

    /// <summary>
    ///     Full info regardless of player-visibility toggles - the GM placed this token, so
    ///     nothing is hidden from the GM (see the "mouseover info tooltip for every token" design
    ///     decision; the PlayerClient-side equivalent only shows whatever the Host already
    ///     resolved into that token's network payload, added in #33).
    /// </summary>
    private string BuildTokenTooltip(MapTokenViewModel token)
    {
        var name = _vm!.ResolveTokenName(token);
        var detail = _vm.ResolveTokenDetail(token);
        return string.IsNullOrWhiteSpace(detail) ? name : $"{name}\n{detail}";
    }

    private void PositionMarker(Control marker, MapTokenViewModel token)
    {
        Canvas.SetLeft(marker, token.X * _zoom - TokenMarkerSizePx / 2);
        Canvas.SetTop(marker, token.Y * _zoom - TokenMarkerSizePx / 2);
    }

    private void OnTokenPointerPressed(MapTokenViewModel token, Control marker, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;

        _draggingToken = token;
        _dragMoved = false;
        e.Pointer.Capture(marker);
        e.Handled = true;
    }

    private void OnTokenPointerMoved(MapTokenViewModel token, Control marker, PointerEventArgs e)
    {
        if (!ReferenceEquals(_draggingToken, token) || !ReferenceEquals(e.Pointer.Captured, marker)) return;

        var position = e.GetPosition(EditorCanvas);
        token.X = position.X / _zoom;
        token.Y = position.Y / _zoom;
        PositionMarker(marker, token);
        _dragMoved = true;
        e.Handled = true;
    }

    private void OnTokenPointerReleased(MapTokenViewModel token, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_draggingToken, token)) return;

        e.Pointer.Capture(null);
        _draggingToken = null;
        e.Handled = true;

        if (_dragMoved) _vm?.NotifyMapTokensChanged(_map!);
        else TokenSelected?.Invoke(token);
    }

    private void OnFloorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
    }

    private void LoadFloor(MapFloorItemViewModel? floor)
    {
        CurrentFloor = floor;
        if (floor is null)
        {
            FloorImageControl.Source = null;
            FogOverlayControl.Source = null;
            TokenLayer.Children.Clear();
            FloorChanged?.Invoke(null);
            return;
        }

        using (var stream = File.OpenRead(floor.ImagePath))
        {
            FloorImageControl.Source = new Bitmap(stream);
        }

        RefreshOverlay();
        // A newly loaded floor always starts fit-to-window, same as the old Stretch="Uniform"
        // default - the GM can then zoom in explicitly if they want to.
        _zoomIsFit = true;
        ApplyZoom(ComputeFitZoom(), true);
        FloorChanged?.Invoke(floor);
    }

    private double ComputeFitZoom()
    {
        if (FloorImageControl.Source is not Bitmap bitmap) return 1.0;

        var viewport = ScrollHost.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return _zoom;

        var imageSize = bitmap.PixelSize;
        return Math.Min(viewport.Width / imageSize.Width, viewport.Height / imageSize.Height);
    }

    /// <summary>
    ///     Applies a new zoom factor, resizing EditorCanvas to imageSize * zoom and keeping
    ///     the point currently centered in the viewport centered afterward too, so zooming in/out
    ///     doesn't visually jump to the top-left corner.
    /// </summary>
    private void ApplyZoom(double zoom, bool keepFit)
    {
        if (FloorImageControl.Source is not Bitmap bitmap) return;

        zoom = Math.Clamp(zoom, ZoomMin, ZoomMax);
        _zoomIsFit = keepFit;

        var viewport = ScrollHost.Viewport;
        var oldOffset = ScrollHost.Offset;
        var imageCenterX = (oldOffset.X + viewport.Width / 2) / _zoom;
        var imageCenterY = (oldOffset.Y + viewport.Height / 2) / _zoom;

        _zoom = zoom;
        var imageSize = bitmap.PixelSize;
        EditorCanvas.Width = imageSize.Width * _zoom;
        EditorCanvas.Height = imageSize.Height * _zoom;

        ScrollHost.Offset = new Vector(
            imageCenterX * _zoom - viewport.Width / 2,
            imageCenterY * _zoom - viewport.Height / 2);

        UpdateZoomLabel();
        if (_lastPointerPosition is { } position) UpdateBrushCursor(position);
        RenderTokens();
    }

    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = $"{_zoom * 100:0}%";
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        ApplyZoom(_zoom + ZoomStep, false);
    }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        ApplyZoom(_zoom - ZoomStep, false);
    }

    private void OnZoomFitClick(object? sender, RoutedEventArgs e)
    {
        ApplyZoom(ComputeFitZoom(), true);
    }

    /// <summary>
    ///     Ctrl+wheel zooms (in ZoomStep increments); a plain wheel is left unhandled so the
    ///     ScrollViewer keeps scrolling normally, matching the Ctrl+scroll-to-zoom convention used
    ///     by most image/map editors.
    /// </summary>
    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        ApplyZoom(_zoom + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep), false);
        e.Handled = true;
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

    /// <summary>
    ///     Sizes/positions the brush-outline Ellipse using the same image-space ↔
    ///     control-space scale (_zoom) as PaintAt, so the visible ring matches exactly what a
    ///     stroke there would affect. Positioned via RenderTransform (not Margin): Margin
    ///     participates in EditorCanvas's own layout measurement, so a large left/top margin near
    ///     the canvas edges kept growing the Panel itself and made the ScrollViewer's viewport jump
    ///     around as the cursor approached the right/bottom edge. RenderTransform is a purely
    ///     visual offset applied after layout, so it can't feed back into the panel's measured
    ///     size.
    /// </summary>
    private void UpdateBrushCursor(Point position)
    {
        if (CurrentFloor is null || FloorImageControl.Source is null)
        {
            BrushCursor.IsVisible = false;
            return;
        }

        var radiusCells = GetBrushRadiusCells(CurrentFloor);
        var diameterPx = (2 * radiusCells + 1) * CurrentFloor.CellSizePx * _zoom;
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
        if (CurrentFloor is null || _getFog is null || FloorImageControl.Source is not Bitmap bitmap) return;

        var imageSize = bitmap.PixelSize;
        var imageX = position.X / _zoom;
        var imageY = position.Y / _zoom;
        if (imageX < 0 || imageY < 0 || imageX >= imageSize.Width || imageY >= imageSize.Height) return;

        var centerX = (int)(imageX / CurrentFloor.CellSizePx);
        var centerY = (int)(imageY / CurrentFloor.CellSizePx);
        var radius = GetBrushRadiusCells(CurrentFloor);
        var revealed = RevealModeToggle.IsChecked == true;

        var fog = _getFog(CurrentFloor);
        var changedCells = new List<FogCellDto>();
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            if (x < 0 || y < 0 || x >= CurrentFloor.GridWidth || y >= CurrentFloor.GridHeight) continue;
            if (fog.IsRevealed(x, y) == revealed) continue;

            // Painted directly into the FogMask for immediate visual feedback - the owner only
            // needs the changed-cell list for whatever it does afterward (broadcast/save).
            fog.SetRevealed(x, y, revealed);
            changedCells.Add(new FogCellDto { X = x, Y = y, Revealed = revealed });
        }

        if (changedCells.Count > 0)
        {
            RefreshOverlay();
            CellsPainted?.Invoke(CurrentFloor, changedCells);
        }
    }
}