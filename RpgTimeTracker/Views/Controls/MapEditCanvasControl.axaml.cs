using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using RpgTimeTracker.Models;
using Persistence = RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;
using Serilog;

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

        LineThicknessSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && _lastPointerPosition is { } position)
                UpdateBrushCursor(position);
        };

        ScrollHost.SizeChanged += (_, _) =>
        {
            if (_zoomIsFit) ApplyZoom(ComputeFitZoom(), true);
        };

        // ApplyZoom/PanToPoint already raise ViewportChanged, but a plain scrollbar drag or
        // trackpad scroll changes ScrollHost.Offset directly without going through either -
        // without this, the mirrored preview (MapLiveWindow's "Vorschau") only ever updated on
        // zoom, not on a pure scroll/pan.
        ScrollHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty) RaiseViewportChanged();
        };

        // ToolCombo's own SelectionChanged fired once already, above, during InitializeComponent -
        // before BrushConfigPanel/FogModeToggle etc. existed, so it no-oped (see
        // SyncToolConfigVisibility). This catches up the default tool's (Fog's) visibility now that
        // everything actually exists.
        SyncToolConfigVisibility();
    }

    private void RaiseViewportChanged()
    {
        if (FloorImageControl.Source is not Bitmap) return;

        var viewport = ScrollHost.Viewport;
        var offset = ScrollHost.Offset;
        var centerX = (offset.X + viewport.Width / 2) / _zoom;
        var centerY = (offset.Y + viewport.Height / 2) / _zoom;
        ViewportChanged?.Invoke(viewport.Width / _zoom, viewport.Height / _zoom, centerX, centerY);
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

    /// <summary>Fired after a token drag completes (position already applied to the model/network) - lets an owner refresh anything else that mirrors token positions, e.g. MapLiveWindow's own preview thumbnail.</summary>
    public event Action<MapTokenViewModel>? TokenMoved;

    /// <summary>
    ///     Fired whenever this canvas's own zoom or pan changes (ApplyZoom/PanToPoint) - lets an
    ///     owner mirror the exact viewport elsewhere, e.g. MapLiveWindow's "Vorschau" thumbnail
    ///     tracking whatever the GM is currently looking at rather than fitting/zooming
    ///     independently. Carries the currently-visible image-space extent (width, height) and its
    ///     center point - NOT the raw zoom factor, which is only meaningful relative to this
    ///     control's own viewport size. A mirrored view almost always has a differently-sized (and
    ///     usually much smaller) viewport; naively applying the same zoom% there shows a tiny sliver
    ///     of the map at "true" pixel scale instead of the same field of view scaled down to fit -
    ///     the whole point of a live minimap. The mirror instead fits this visible extent into its
    ///     own viewport (see MapDisplayView.SetExternalViewport), which is exactly equivalent to
    ///     this control's own ComputeFitZoom logic, just driven by an explicit rectangle instead of
    ///     the whole image.
    /// </summary>
    public event Action<double, double, double, double>? ViewportChanged;

    /// <summary>
    ///     Fired when the GM double-clicks the canvas background (not a token marker) - image-
    ///     space (x, y) on the given floor. The owner broadcasts this to every connected player
    ///     (see MainWindowViewModel.BroadcastMapPingAsync) and shows it locally too.
    /// </summary>
    public event Action<Guid, double, double>? PingRequested;

    /// <summary>
    ///     Fired when a Draw-tool stroke completes with real movement - image-space points plus
    ///     the durability tier chosen in LineDurabilityCombo. The owner (MapLiveWindow/
    ///     MapPrepareWindow) turns this into a MapLineDto and calls MainWindowViewModel.AddMapLine,
    ///     then ShowPersistedLine to render it immediately on this same control.
    /// </summary>
    public event Action<Guid, IReadOnlyList<Point>, Persistence.MapLineDurability>? LineDrawn;

    /// <summary>
    ///     Fired instead of LineDrawn when the completed stroke's durability is Temporary - a
    ///     Temporary line is never added to floor.Lines (see MainWindowViewModel.AddMapLine's doc
    ///     comment), so the owner doesn't build a MapLineDto for it at all; it just broadcasts these
    ///     raw image-space points via the same ephemeral fade-and-forget pipeline a player's own
    ///     stroke uses (MainWindowViewModel.BroadcastGmAnnotationAsync), carrying the color picked
    ///     in LineColorPicker at the time. This control fades and removes its own local visual itself
    ///     (see FinishDrawStroke) rather than asking the owner to.
    /// </summary>
    public event Action<Guid, IReadOnlyList<Point>, string>? TemporaryLineDrawn;

    /// <summary>GM clicked "Erase all" - the owner clears the floor's persisted Lines and calls ClearPersistedLineVisuals.</summary>
    public event Action<MapFloorItemViewModel>? EraseAllLinesRequested;

    /// <summary>
    ///     Fired once per line touched by an Erase-tool brush drag (see EraseAt) - the owner removes
    ///     it via MainWindowViewModel.RemoveMapLine. This control already took its own visual down
    ///     immediately (EraseAt), so the owner doesn't need to call back into ClearPersistedLineVisuals.
    /// </summary>
    public event Action<MapFloorItemViewModel, Guid>? LineEraseRequested;

    /// <summary>
    ///     Fired once per line trimmed by a points-mode Erase-tool drag (see EraseAt) - the line
    ///     still exists (at least 2 points survived) but its Points list was mutated in place. The
    ///     owner re-persists/re-broadcasts it via MainWindowViewModel.UpdateMapLine, the same "full
    ///     upsert by Id" path used for a newly drawn line.
    /// </summary>
    public event Action<MapFloorItemViewModel, Persistence.MapLineDto>? LinePointsErased;

    /// <summary>
    ///     Fired once per extra surviving run when a points-mode Erase-tool drag splits a line into
    ///     two or more disconnected pieces (see SplitIntoUntouchedRuns) - the first run just updates
    ///     the original line in place (LinePointsErased), but every additional run is a brand new
    ///     line with its own Id, already shown locally (ShowPersistedLine) and needing the owner to
    ///     add it via MainWindowViewModel.AddMapLine, same as a freshly drawn line.
    /// </summary>
    public event Action<MapFloorItemViewModel, Persistence.MapLineDto>? LineSplitCreated;

    private List<Point>? _drawImagePoints;
    private Polyline? _activeDrawVisual;
    private Persistence.MapLineDurability _activeDrawDurability;
    private Color _activeDrawColor;
    private bool _isErasing;
    private readonly Dictionary<Guid, Polyline> _persistedLineVisuals = new();

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

        // A Character token also shows a little facing-direction arrow (#70), adjusted via mouse
        // wheel over the marker rather than drag (dragging the marker already moves the token).
        var facingArrow = token.LinkKind == TokenLinkKind.Character ? CreateFacingArrow(token) : null;
        var markerContent = facingArrow is null
            ? content
            : new Panel { Children = { content, facingArrow } };

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
            Child = markerContent
        };
        ToolTip.SetTip(marker, BuildTokenTooltip(token));

        marker.PointerPressed += (_, e) => OnTokenPointerPressed(token, marker, e);
        marker.PointerMoved += (_, e) => OnTokenPointerMoved(token, marker, e);
        marker.PointerReleased += (_, e) => OnTokenPointerReleased(token, e);
        if (facingArrow is not null)
            marker.PointerWheelChanged += (_, e) => OnTokenFacingWheelChanged(token, facingArrow, e);

        PositionMarker(marker, token);
        return marker;
    }

    private const double FacingStepDegrees = 15;

    private static Avalonia.Controls.Shapes.Path CreateFacingArrow(MapTokenViewModel token)
    {
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,-15 L 4,-8 L -4,-8 Z"),
            Fill = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(token.FacingDegrees)
        };
    }

    /// <summary>
    ///     Adjusts a Character token's facing (#70) by a fixed step per wheel notch, wrapping at
    ///     360 - a plain wheel scoped to the marker itself (not Ctrl, unlike the canvas-wide
    ///     zoom in OnCanvasPointerWheelChanged), so it doesn't also scroll/zoom the canvas
    ///     underneath.
    /// </summary>
    private void OnTokenFacingWheelChanged(MapTokenViewModel token, Avalonia.Controls.Shapes.Path facingArrow,
        PointerWheelEventArgs e)
    {
        var step = e.Delta.Y > 0 ? FacingStepDegrees : -FacingStepDegrees;
        token.FacingDegrees = (token.FacingDegrees + step + 360) % 360;
        facingArrow.RenderTransform = new RotateTransform(token.FacingDegrees);
        e.Handled = true;
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

        if (_dragMoved)
        {
            _vm?.NotifyTokenMoved(_map!, token);
            TokenMoved?.Invoke(token);
        }
        else TokenSelected?.Invoke(token);
    }

    private void OnFloorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadFloor(FloorSelector.SelectedItem as MapFloorItemViewModel);
    }

    /// <summary>Shows only the config panel relevant to the newly selected tool - see the sidebar's BrushConfigPanel/DrawConfigPanel/EraseConfigPanel.</summary>
    private void OnToolChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncToolConfigVisibility();
    }

    /// <summary>
    ///     Shared by OnToolChanged and the constructor (see its call site) - ToolCombo's
    ///     SelectionChanged fires once during InitializeComponent, before BrushConfigPanel etc. even
    ///     exist yet, so that very first firing is a no-op and the default tool's (Fog's) config
    ///     panel/toggle would otherwise stay at its XAML-declared IsVisible="False" until the GM
    ///     manually switched tools away and back.
    /// </summary>
    private void SyncToolConfigVisibility()
    {
        if (BrushConfigPanel is null) return; // fires once during InitializeComponent, before the other panels exist yet

        var index = ToolCombo.SelectedIndex;
        // Erase also uses BrushSizeSlider - it doubles as the erase brush radius (see EraseAt) -
        // so both Fog and Erase share the one slider instead of a second, redundant one.
        BrushConfigPanel.IsVisible = index == ToolFog || index == ToolErase;
        FogModeToggle.IsVisible = index == ToolFog;
        DrawConfigPanel.IsVisible = index == ToolDraw;
        EraseConfigPanel.IsVisible = index == ToolErase;
        UpdateBrushCursor(_lastPointerPosition ?? default);
    }

    private void LoadFloor(MapFloorItemViewModel? floor)
    {
        ClearPersistedLineVisuals();
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
        ShowAllPersistedLinesForCurrentFloor();
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
        // Persisted-line Polylines are drawn in screen-space (image point * zoom, see
        // ShowPersistedLine) rather than being affected by any layout transform, so unlike the fog
        // overlay image (which just re-stretches) they were left stuck at their old zoom's pixel
        // positions until the floor was reloaded - redrawing them here keeps them lined up with the
        // map underneath at every zoom level.
        ClearPersistedLineVisuals();
        ShowAllPersistedLinesForCurrentFloor();
        ViewportChanged?.Invoke(viewport.Width / _zoom, viewport.Height / _zoom, imageCenterX, imageCenterY);
    }

    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = $"{_zoom * 100:0}%";
    }

    /// <summary>
    ///     Centers the viewport on an image-space point (#70's "jump to the current turn's
    ///     token") - the same offset math ApplyZoom already uses to keep a point centered across
    ///     a zoom change, just driven by an explicit target instead of the viewport's prior center.
    /// </summary>
    public void PanToPoint(double imageX, double imageY)
    {
        var viewport = ScrollHost.Viewport;
        ScrollHost.Offset = new Vector(imageX * _zoom - viewport.Width / 2, imageY * _zoom - viewport.Height / 2);
        ViewportChanged?.Invoke(viewport.Width / _zoom, viewport.Height / _zoom, imageX, imageY);
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

    // ToolCombo item indices - must match the ComboBoxItem order in MapEditCanvasControl.axaml.
    // Reveal/Hide used to be two separate tools; they're now one Fog tool with FogModeToggle
    // choosing the mode (see IsRevealMode), so the remaining tools each shifted down by one.
    private const int ToolFog = 0;
    private const int ToolPing = 1;
    private const int ToolDraw = 2;
    private const int ToolErase = 3;

    /// <summary>FogModeToggle's current state - true (default) reveals fog, false hides it.</summary>
    private bool IsRevealMode => FogModeToggle.IsChecked != false;

    /// <summary>
    ///     Pinging requires both the Ping tool to be selected AND a double-click - matching the
    ///     player-facing gesture (double-click) while still needing the deliberate tool switch to
    ///     avoid any ambiguity with painting. A single left-click while Ping is selected does
    ///     nothing (not even a ping) rather than painting, since switching to the Ping tool signals
    ///     "I'm done painting for now." Not offered while still on Reveal/Hide (unlike an earlier
    ///     iteration) - painting starts immediately on PointerPressed, so a double-click there would
    ///     still paint one cell on its first press before the second press could be recognized as a
    ///     double-click, and that inconsistent one-cell side effect wasn't worth keeping as a
    ///     shortcut once a dedicated tool already exists.
    /// </summary>
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EditorCanvas).Properties.IsLeftButtonPressed) return;

        if (ToolCombo.SelectedIndex == ToolPing)
        {
            if (e.ClickCount == 2 && CurrentFloor is not null)
            {
                var pingPosition = e.GetPosition(EditorCanvas);
                PingRequested?.Invoke(CurrentFloor.Id, pingPosition.X / _zoom, pingPosition.Y / _zoom);
            }

            e.Handled = true;
            return;
        }

        if (ToolCombo.SelectedIndex == ToolDraw && CurrentFloor is not null)
        {
            BeginDrawStroke(e.GetPosition(EditorCanvas));
            e.Pointer.Capture(EditorCanvas);
            e.Handled = true;
            return;
        }

        if (ToolCombo.SelectedIndex == ToolErase && CurrentFloor is not null)
        {
            _isErasing = true;
            e.Pointer.Capture(EditorCanvas);
            EraseAt(e.GetPosition(EditorCanvas));
            e.Handled = true;
            return;
        }

        _isPainting = true;
        PaintAt(e.GetPosition(EditorCanvas));
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(EditorCanvas);
        _lastPointerPosition = position;
        UpdateBrushCursor(position);

        if (_drawImagePoints is not null && ReferenceEquals(e.Pointer.Captured, EditorCanvas))
        {
            AppendDrawPoint(position);
            return;
        }

        if (_isErasing && ReferenceEquals(e.Pointer.Captured, EditorCanvas))
        {
            EraseAt(position);
            return;
        }

        if (!_isPainting) return;

        PaintAt(position);
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPainting = false;

        if (_isErasing)
        {
            _isErasing = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_drawImagePoints is not null)
        {
            FinishDrawStroke();
            e.Pointer.Capture(null);
        }
    }

    /// <summary>
    ///     Erase-tool brush drag - two modes, chosen via EraseModeToggle:
    ///     whole-line (default) removes any line touched anywhere by the brush, same "paint over it"
    ///     feel as the fog tools; points mode instead deletes only the individual points a drag
    ///     actually passes over (the originally grilled "drag to rub out a region" design), so a line
    ///     can be trimmed down rather than removed outright. Erasing points out of the *middle* of a
    ///     line splits it into separate surviving runs (see SplitIntoUntouchedRuns) instead of just
    ///     dropping the touched points and leaving the two sides visually bridged by a straight line
    ///     across the gap - the first run replaces the original line in place, any further runs
    ///     become new sibling lines. A run of fewer than 2 points can't draw a line and is dropped.
    ///     Each touched line's visual is updated/removed/split immediately (so a drag over several
    ///     lines handles all of them in one gesture) and reported via LineEraseRequested/
    ///     LinePointsErased/LineSplitCreated for the owner to persist/broadcast.
    /// </summary>
    private void EraseAt(Point position)
    {
        if (CurrentFloor is null) return;

        var imageX = position.X / _zoom;
        var imageY = position.Y / _zoom;
        var radiusPx = GetBrushRadiusCells(CurrentFloor) * CurrentFloor.CellSizePx;
        var radiusSquared = radiusPx * radiusPx;

        bool Touched(Persistence.MapLinePointDto p)
        {
            var dx = p.X - imageX;
            var dy = p.Y - imageY;
            return dx * dx + dy * dy <= radiusSquared;
        }

        var pointsMode = EraseModeToggle.IsChecked == true;

        foreach (var line in CurrentFloor.Lines.ToList())
        {
            if (!line.Points.Any(Touched)) continue;

            if (!pointsMode)
            {
                RemoveLine(line.Id);
                continue;
            }

            var runs = SplitIntoUntouchedRuns(line.Points, Touched);
            if (runs.Count == 0)
            {
                RemoveLine(line.Id);
                continue;
            }

            line.Points = runs[0];
            UpdatePersistedLineVisual(line);
            LinePointsErased?.Invoke(CurrentFloor, line);

            foreach (var extraRun in runs.Skip(1))
            {
                var newLine = new Persistence.MapLineDto
                {
                    FloorId = line.FloorId,
                    Points = extraRun,
                    ColorHex = line.ColorHex,
                    Durability = line.Durability,
                    Thickness = line.Thickness,
                    HiddenUntilRevealed = line.HiddenUntilRevealed,
                    OwnerClientId = line.OwnerClientId
                };
                ShowPersistedLine(newLine);
                LineSplitCreated?.Invoke(CurrentFloor, newLine);
            }
        }

        void RemoveLine(Guid lineId)
        {
            if (_persistedLineVisuals.Remove(lineId, out var visual))
                AnnotationLayer.Children.Remove(visual);
            LineEraseRequested?.Invoke(CurrentFloor, lineId);
        }
    }

    /// <summary>
    ///     Splits a line's points into maximal contiguous runs of untouched points, dropping every
    ///     touched point and discarding any run too short to still draw a line (fewer than 2 points)
    ///     - erasing the middle of a line yields two separate runs instead of one list with a gap
    ///     bridged by a straight line across it.
    /// </summary>
    private static List<List<Persistence.MapLinePointDto>> SplitIntoUntouchedRuns(
        List<Persistence.MapLinePointDto> points, Func<Persistence.MapLinePointDto, bool> touched)
    {
        var runs = new List<List<Persistence.MapLinePointDto>>();
        List<Persistence.MapLinePointDto>? current = null;

        foreach (var point in points)
        {
            if (touched(point))
            {
                if (current is { Count: >= 2 }) runs.Add(current);
                current = null;
                continue;
            }

            current ??= [];
            current.Add(point);
        }

        if (current is { Count: >= 2 }) runs.Add(current);
        return runs;
    }

    /// <summary>
    ///     Selected durability tier from LineDurabilityCombo - index 0/1/2 matches
    ///     Temporary/SemiPermanent/Permanent (see the XAML's ComboBoxItem order).
    /// </summary>
    private Persistence.MapLineDurability SelectedLineDurability =>
        (Persistence.MapLineDurability)Math.Max(0, LineDurabilityCombo.SelectedIndex);

    /// <summary>Stroke width for the next Draw-tool line, from LineThicknessSlider - same "brush size" convention as the fog tools' BrushSizeSlider.</summary>
    public double SelectedLineThickness => LineThicknessSlider.Value;

    /// <summary>Hex color for the next Draw-tool line, from LineColorPicker.</summary>
    public string SelectedLineColorHex => LineColorPicker.Color.ToString();

    private void BeginDrawStroke(Point firstPoint)
    {
        // Captured once at stroke start rather than re-read in FinishDrawStroke - the GM could
        // otherwise flip LineDurabilityCombo/LineColorPicker mid-drag and have the stroke silently
        // change tier/color partway through.
        _activeDrawDurability = SelectedLineDurability;
        _activeDrawColor = LineColorPicker.Color;

        var imagePoint = new Point(firstPoint.X / _zoom, firstPoint.Y / _zoom);
        _drawImagePoints = [imagePoint];
        _activeDrawVisual = new Polyline
        {
            Points = [firstPoint],
            Stroke = new SolidColorBrush(_activeDrawColor),
            StrokeThickness = SelectedLineThickness,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        AnnotationLayer.Children.Add(_activeDrawVisual);
        Log.Debug(
            "Draw stroke started: durability={Durability} color={Color} thickness={Thickness} firstPoint={FirstPoint} zoom={Zoom} " +
            "AnnotationLayer[Children={Count} Bounds={LayerBounds} IsVisible={LayerVisible} ZIndex={LayerZIndex} Opacity={LayerOpacity}] " +
            "EditorCanvas[Bounds={CanvasBounds} IsVisible={CanvasVisible}]",
            _activeDrawDurability, _activeDrawColor, SelectedLineThickness, firstPoint, _zoom,
            AnnotationLayer.Children.Count, AnnotationLayer.Bounds, AnnotationLayer.IsVisible, AnnotationLayer.ZIndex, AnnotationLayer.Opacity,
            EditorCanvas.Bounds, EditorCanvas.IsVisible);
    }

    private void AppendDrawPoint(Point screenPosition)
    {
        if (_drawImagePoints is not { } points || _activeDrawVisual is not { } visual) return;

        points.Add(new Point(screenPosition.X / _zoom, screenPosition.Y / _zoom));

        // Reassigning the Points property (not mutating the existing list via .Add) - diagnostic
        // logging showed the Polyline's rendered geometry/Bounds stayed frozen at just the first
        // point forever once .Add was used, since Avalonia's Polyline doesn't react to in-place
        // collection mutation, only to the Points property itself changing. That's why every
        // Temporary-tier stroke silently rendered as an invisible single-point "line" instead of
        // the actual dragged path.
        visual.Points = new Points(visual.Points!.Append(screenPosition));
    }

    private void FinishDrawStroke()
    {
        if (_drawImagePoints is not { } points || _activeDrawVisual is not { } visual)
        {
            _drawImagePoints = null;
            _activeDrawVisual = null;
            return;
        }

        _drawImagePoints = null;
        _activeDrawVisual = null;

        if (points.Count < 2 || CurrentFloor is null)
        {
            AnnotationLayer.Children.Remove(visual);
            return;
        }

        if (_activeDrawDurability == Persistence.MapLineDurability.Temporary)
        {
            // Never added to floor.Lines/persisted (see TemporaryLineDrawn's doc comment) - just
            // fades out locally like the original ephemeral stroke feature.
            Log.Debug(
                "Draw stroke finished (Temporary): points={PointCount} color={Color} thickness={Thickness} " +
                "visual[Opacity={Opacity} IsVisible={IsVisible} Bounds={Bounds} ZIndex={ZIndex} Parent={ParentIsLayer} " +
                "Points.Count={VisualPointCount} FirstScreenPoint={FirstScreenPoint} LastScreenPoint={LastScreenPoint}] " +
                "AnnotationLayer.Children={Count}",
                points.Count, _activeDrawColor, visual.StrokeThickness, visual.Opacity, visual.IsVisible, visual.Bounds, visual.ZIndex,
                ReferenceEquals(visual.Parent, AnnotationLayer), visual.Points?.Count,
                visual.Points is { Count: > 0 } ? visual.Points[0] : (Point?)null,
                visual.Points is { Count: > 0 } ? visual.Points[^1] : (Point?)null,
                AnnotationLayer.Children.Count);
            _ = FadeOutAndRemoveAnnotationAsync(visual);
            TemporaryLineDrawn?.Invoke(CurrentFloor.Id, points, _activeDrawColor.ToString());
            return;
        }

        AnnotationLayer.Children.Remove(visual);
        LineDrawn?.Invoke(CurrentFloor.Id, points, _activeDrawDurability);
    }

    /// <summary>
    ///     Renders a SemiPermanent/Permanent line that's already been added to the floor's Lines
    ///     (see MainWindowViewModel.AddMapLine) - no fade, tracked by Id so RemovePersistedLine can
    ///     take it down again later (erase, or SemiPermanent expiry).
    /// </summary>
    public void ShowPersistedLine(Persistence.MapLineDto line)
    {
        if (CurrentFloor is null || line.FloorId != CurrentFloor.Id) return;
        if (_persistedLineVisuals.ContainsKey(line.Id)) return;

        var lineColor = Color.TryParse(line.ColorHex, out var parsed) ? parsed : Colors.Gold;
        var polyline = new Polyline
        {
            Points = new Points(line.Points.Select(p => new Point(p.X * _zoom, p.Y * _zoom))),
            Stroke = new SolidColorBrush(lineColor),
            StrokeThickness = line.Thickness,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        AnnotationLayer.Children.Add(polyline);
        _persistedLineVisuals[line.Id] = polyline;
    }

    /// <summary>Re-renders an already-shown persisted line whose Points were just mutated in place (see EraseAt's points mode) - unlike ShowPersistedLine, this replaces the existing visual instead of no-oping.</summary>
    public void UpdatePersistedLineVisual(Persistence.MapLineDto line)
    {
        if (_persistedLineVisuals.Remove(line.Id, out var oldVisual))
            AnnotationLayer.Children.Remove(oldVisual);
        ShowPersistedLine(line);
    }

    /// <summary>Redraws every persisted line for the current floor - called after LoadFloor and after ClearPersistedLineVisuals.</summary>
    public void ShowAllPersistedLinesForCurrentFloor()
    {
        if (CurrentFloor is null) return;

        foreach (var line in CurrentFloor.Lines) ShowPersistedLine(line);
    }

    public void ClearPersistedLineVisuals()
    {
        foreach (var visual in _persistedLineVisuals.Values) AnnotationLayer.Children.Remove(visual);
        _persistedLineVisuals.Clear();
    }

    private void OnEraseAllLinesClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentFloor is null) return;

        EraseAllLinesRequested?.Invoke(CurrentFloor);
        ClearPersistedLineVisuals();
    }

    private static readonly TimeSpan AnnotationFadeDuration = TimeSpan.FromSeconds(8);

    /// <summary>
    ///     Renders a player's freehand annotation stroke directly on the GM's main editing canvas
    ///     (not just MapLiveWindow's small mirrored preview, which is easy to miss while actively
    ///     working the big canvas) - dropped silently if it's for a different floor than the one
    ///     currently shown. Points arrive in native image-space (see AnnotationPoint's doc comment)
    ///     but this control's own coordinate space is already zoomed (see TokenLayer's
    ///     token.X * _zoom convention), so every point is scaled the same way here.
    /// </summary>
    public void ShowAnnotationStroke(Guid floorId, IReadOnlyList<AnnotationPoint> points, string clientId)
    {
        if (CurrentFloor is null || CurrentFloor.Id != floorId || points.Count == 0) return;

        var color = PainterTagHelper.ColorFor(clientId);
        var polyline = new Polyline
        {
            Points = new Points(points.Select(p => new Point(p.X * _zoom, p.Y * _zoom))),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        AnnotationLayer.Children.Add(polyline);
        _ = FadeOutAndRemoveAnnotationAsync(polyline);
    }

    /// <summary>
    ///     Fades a stroke's opacity out, then removes it after AnnotationFadeDuration - removal is
    ///     driven by a plain Task.Delay rather than awaiting Animation.RunAsync's own completion.
    ///     Diagnostic logging (see BeginDrawStroke/FinishDrawStroke) showed AnnotationLayer.Children
    ///     growing monotonically across an entire GM session - RunAsync's Task was never completing,
    ///     so strokes never got removed at all (just sitting there, fully opaque, piling up forever).
    ///     Firing the animation without awaiting it and using a plain timer for removal means the
    ///     stroke still disappears reliably from the tree even if the opacity animation itself never
    ///     finishes for whatever reason.
    /// </summary>
    private async Task FadeOutAndRemoveAnnotationAsync(Control visual)
    {
        var fadeOut = new Animation
        {
            Duration = AnnotationFadeDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.0) } }
            }
        };
        _ = fadeOut.RunAsync(visual);
        await Task.Delay(AnnotationFadeDuration);
        AnnotationLayer.Children.Remove(visual);
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
        if (CurrentFloor is null || FloorImageControl.Source is null ||
            (ToolCombo.SelectedIndex != ToolFog && ToolCombo.SelectedIndex != ToolErase &&
             ToolCombo.SelectedIndex != ToolDraw))
        {
            BrushCursor.IsVisible = false;
            return;
        }

        // The Draw tool's "brush" is just the stroke width itself (a plain screen-pixel value,
        // not zoom-scaled - see BeginDrawStroke/SelectedLineThickness), unlike Fog/Erase's brush
        // which covers a radius of map cells and does scale with zoom.
        var diameterPx = ToolCombo.SelectedIndex == ToolDraw
            ? SelectedLineThickness
            : (2 * GetBrushRadiusCells(CurrentFloor) + 1) * CurrentFloor.CellSizePx * _zoom;

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
        var revealed = IsRevealMode;

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