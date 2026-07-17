using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.Shared.Views;

/// <summary>
///     Player-facing zoom/pan/fit for the shared map display (mirrors
///     RpgTimeTracker.Views.Controls.MapEditCanvasControl's own ApplyZoom/ComputeFitZoom/
///     PanToPoint, GM-side) - implemented via a LayoutTransformControl's ScaleTransform instead
///     of manually resizing the content Panel, so every existing native-pixel-space binding
///     (floor image size, token X/Y via the ContentPresenter Style in MapDisplayView.axaml) needs
///     no change at all; only the math here cares about the current zoom factor.
/// </summary>
public partial class MapDisplayView : UserControl
{
    /// <summary>
    ///     Wider range than the GM editor's own MapEditCanvasControl (0.25-4.0) - this control is
    ///     also used for the GM's own small "Vorschau" thumbnail in MapLiveWindow, which can need
    ///     a much smaller fit-zoom for a large map in a tiny preview area; clamping it up to 0.25
    ///     there made "Fit" overflow the preview instead of actually fitting it.
    /// </summary>
    private const double ZoomMin = 0.05;

    private const double ZoomMax = 8.0;
    private const double ZoomStep = 0.1;

    /// <summary>
    ///     False for MapLiveWindow's "Vorschau" thumbnail, which mirrors the GM's own EditCanvas
    ///     viewport live (see SetExternalViewport) rather than being independently zoomable - its
    ///     own zoom/Fit buttons would just get overridden by the next main-canvas zoom/pan, so
    ///     they're hidden there instead of left as dead UI. True (default) for every real
    ///     player-facing use (PlayerWindow/MediaWindow), which does own its zoom.
    /// </summary>
    public static readonly StyledProperty<bool> ShowZoomControlsProperty =
        AvaloniaProperty.Register<MapDisplayView, bool>(nameof(ShowZoomControls), true);

    public bool ShowZoomControls
    {
        get => GetValue(ShowZoomControlsProperty);
        set => SetValue(ShowZoomControlsProperty, value);
    }

    private double _zoom = 1.0;
    private bool _zoomIsFit = true;
    private MapDisplayViewModel? _viewModel;

    /// <summary>Right- or middle-button drag pan state - left button is used for the ping double-click below.</summary>
    private Point? _dragStartPointerPosition;

    private Vector _dragStartOffset;

    /// <summary>
    ///     In-progress freehand stroke (image-space points), non-null only between a Shift+left
    ///     PointerPressed and its matching PointerReleased. Null again once a stroke resolves
    ///     (sent or discarded), doubling as "am I currently drawing" - distinct from
    ///     _dragStartPointerPosition/_dragStartOffset, which track right/middle-button panning.
    /// </summary>
    private List<Point>? _strokeImagePoints;

    private Polyline? _activeStrokeVisual;

    /// <summary>
    ///     Fired on a left-button double-click on the map, image-space (x, y) - the owner
    ///     decides what a ping means here: the real PlayerClient sends it to the host
    ///     (SendMapPingFromPlayerAsync), the Host's own PlayerWindow preview has nothing
    ///     sensible to do with it and can just leave it unhandled.
    /// </summary>
    public event Action<double, double>? PingRequested;

    /// <summary>
    ///     Fired when a Shift+left-drag completes with actual movement (a single click/tap with
    ///     no movement is discarded rather than sent as a one-point "stroke"), image-space points
    ///     in drawing order. Same owner-decides-what-it-means shape as PingRequested - the real
    ///     PlayerClient sends it to the host (SendMapAnnotationFromPlayerAsync).
    /// </summary>
    public event Action<IReadOnlyList<Point>>? AnnotationRequested;

    public MapDisplayView()
    {
        InitializeComponent();

        ScrollHost.SizeChanged += (_, _) =>
        {
            if (_zoomIsFit) ApplyZoom(ComputeFitZoom(), true);
        };
        ScrollHost.PointerPressed += OnScrollHostPointerPressed;
        ScrollHost.PointerMoved += OnScrollHostPointerMoved;
        ScrollHost.PointerReleased += OnScrollHostPointerReleased;

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Converts a ScrollHost-relative screen position to native image-space coordinates - see OnScrollHostPointerPressed's ping comment for why this (not GetPosition on a descendant) is the reliable way through LayoutTransformControl's scale.</summary>
    private Point ToImagePoint(Point screenPosition)
    {
        return new Point(
            (ScrollHost.Offset.X + screenPosition.X) / _zoom,
            (ScrollHost.Offset.Y + screenPosition.Y) / _zoom);
    }

    /// <summary>
    ///     Right/middle-button drag pans the map. A left-button double-click pings instead (see
    ///     PingRequested). Shift+left-drag draws a freehand annotation stroke (see
    ///     AnnotationRequested) - plain single left-clicks are otherwise unclaimed here, so
    ///     neither conflicts with anything.
    /// </summary>
    private void OnScrollHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(ScrollHost);
        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            var imagePoint = ToImagePoint(e.GetPosition(ScrollHost));
            PingRequested?.Invoke(imagePoint.X, imagePoint.Y);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            BeginStroke(ToImagePoint(e.GetPosition(ScrollHost)));
            e.Pointer.Capture(ScrollHost);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsRightButtonPressed && !point.Properties.IsMiddleButtonPressed) return;

        _dragStartPointerPosition = e.GetPosition(ScrollHost);
        _dragStartOffset = ScrollHost.Offset;
        e.Pointer.Capture(ScrollHost);
        e.Handled = true;
    }

    private void OnScrollHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_strokeImagePoints is not null && ReferenceEquals(e.Pointer.Captured, ScrollHost))
        {
            AppendStrokePoint(ToImagePoint(e.GetPosition(ScrollHost)));
            e.Handled = true;
            return;
        }

        if (_dragStartPointerPosition is not { } start || !ReferenceEquals(e.Pointer.Captured, ScrollHost)) return;

        var delta = e.GetPosition(ScrollHost) - start;
        ScrollHost.Offset = new Vector(_dragStartOffset.X - delta.X, _dragStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    /// <summary>
    ///     Starts capturing a freehand stroke, rendered live as the pointer moves - anchored
    ///     Left/Top like the ping ring (see OnPingReceived's comment) since ImageContentPanel is a
    ///     plain Panel, not a Canvas: without it, Polyline's own implicit size (from its Points'
    ///     bounds) would get centered within the whole panel instead of rendering at its actual
    ///     image-space coordinates.
    /// </summary>
    private void BeginStroke(Point firstPoint)
    {
        _strokeImagePoints = [firstPoint];
        _activeStrokeVisual = new Polyline
        {
            Points = [firstPoint],
            Stroke = Brushes.Gold,
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        ImageContentPanel.Children.Add(_activeStrokeVisual);
    }

    private void AppendStrokePoint(Point imagePoint)
    {
        if (_strokeImagePoints is not { } points || _activeStrokeVisual is not { } visual) return;

        points.Add(imagePoint);
        visual.Points!.Add(imagePoint);
    }

    /// <summary>
    ///     Ends stroke capture - a completed stroke (real movement) fades out locally (matching
    ///     the ping ripple's own fade) and is reported via AnnotationRequested; a stroke with no
    ///     movement (a plain click that happened to also hold Shift) is discarded silently rather
    ///     than sent as a meaningless one-point "stroke".
    /// </summary>
    private async void FinishStroke()
    {
        if (_strokeImagePoints is not { } points || _activeStrokeVisual is not { } visual)
        {
            _strokeImagePoints = null;
            _activeStrokeVisual = null;
            return;
        }

        _strokeImagePoints = null;
        _activeStrokeVisual = null;

        if (points.Count < 2)
        {
            ImageContentPanel.Children.Remove(visual);
            return;
        }

        AnnotationRequested?.Invoke(points);
        await FadeOutAndRemoveAsync(visual);
    }

    private void OnScrollHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_strokeImagePoints is not null)
        {
            FinishStroke();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_dragStartPointerPosition is null) return;

        _dragStartPointerPosition = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ActiveCharacterZoomRequested -= OnActiveCharacterZoomRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PingReceived -= OnPingReceived;
            _viewModel.AnnotationReceived -= OnAnnotationReceived;
        }

        _viewModel = DataContext as MapDisplayViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ActiveCharacterZoomRequested += OnActiveCharacterZoomRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.PingReceived += OnPingReceived;
            _viewModel.AnnotationReceived += OnAnnotationReceived;
        }
    }

    /// <summary>
    ///     Renders a generic, un-attributed ripple at the given image-space point (#28's "map
    ///     ping") and removes it again once it's fully faded - fire-and-forget, not part of
    ///     VisibleTokens since there's no persisted state behind it (see
    ///     MapDisplayViewModel.NotifyPingReceived).
    /// </summary>
    private async void OnPingReceived(double x, double y)
    {
        const double diameter = 56;
        var ring = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = Brushes.Gold,
            StrokeThickness = 4,
            IsHitTestVisible = false,
            // ImageContentPanel is a plain Panel (not a Canvas) - with the default Stretch
            // alignment, a child that also has a fixed Width/Height gets CENTERED within the
            // panel's arrange rect instead of anchored at (0,0), which silently threw the
            // TranslateTransform offset below off by half the map image's size. Explicit
            // Left/Top anchors it at (0,0) first, matching how the token markers rely on Canvas
            // positioning instead.
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(x - diameter / 2, y - diameter / 2)
        };
        ImageContentPanel.Children.Add(ring);
        await FadeOutAndRemoveAsync(ring);
    }

    /// <summary>
    ///     Renders a player's freehand annotation stroke, points already in image-space - same
    ///     fade-and-remove treatment as the ping ripple, since it's equally ephemeral (see
    ///     MapDisplayViewModel.NotifyAnnotationReceived). Only the GM's own views ever raise this
    ///     (see RpcMethods.MapAnnotationFromPlayer's doc comment).
    /// </summary>
    private async void OnAnnotationReceived(IReadOnlyList<AnnotationPoint> points)
    {
        var visual = new Polyline
        {
            Points = new Points(points.Select(p => new Point(p.X, p.Y))),
            Stroke = Brushes.Gold,
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        ImageContentPanel.Children.Add(visual);
        await FadeOutAndRemoveAsync(visual);
    }

    /// <summary>Shared fade-out-then-remove for the ping ripple and both annotation-stroke paths (drawn locally and received).</summary>
    private async System.Threading.Tasks.Task FadeOutAndRemoveAsync(Control visual)
    {
        var fadeOut = new Animation
        {
            Duration = TimeSpan.FromSeconds(2.5),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.0) }
                }
            }
        };
        await fadeOut.RunAsync(visual);
        ImageContentPanel.Children.Remove(visual);
    }

    /// <summary>
    ///     Re-fits whenever a new floor image is shown (ShowMap, floor navigation, or an
    ///     auto-zoom-triggered floor switch all funnel through
    ///     MapDisplayViewModel.CurrentFloorImageBitmap) - matches
    ///     MapEditCanvasControl.LoadFloor's "a newly loaded floor always starts fit-to-window"
    ///     convention, except the auto-zoom sequence below immediately overrides this with the
    ///     configured zoom level when that's what triggered the floor switch. Skipped entirely
    ///     when this view mirrors another control's viewport (ShowZoomControls false, see
    ///     SetExternalViewport) - the mirrored zoom/pan already got set from the source control's
    ///     own ViewportChanged before this image swap, and auto-fitting here would immediately
    ///     stomp on it.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapDisplayViewModel.CurrentFloorImageBitmap) || !ShowZoomControls) return;

        _zoomIsFit = true;
        ApplyZoom(ComputeFitZoom(), true);
    }

    private void OnActiveCharacterZoomRequested(double imageX, double imageY)
    {
        if (_viewModel is null) return;

        ApplyZoom(_viewModel.AutoZoomLevel, false);
        PanToPoint(imageX, imageY);
    }

    private double ComputeFitZoom()
    {
        if (_viewModel?.CurrentFloorImageBitmap is not { } bitmap) return 1.0;

        var viewport = ScrollHost.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return _zoom;

        var imageSize = bitmap.PixelSize;
        return Math.Min(viewport.Width / imageSize.Width, viewport.Height / imageSize.Height);
    }

    /// <summary>
    ///     Applies a new zoom factor via the LayoutTransformControl's ScaleTransform, keeping
    ///     the point currently centered in the viewport centered afterward too - same
    ///     center-preserving offset math as MapEditCanvasControl.ApplyZoom, except once the
    ///     transformed content no longer fills the viewport on an axis, that axis's offset is
    ///     left at 0 (letting HorizontalContentAlignment/VerticalContentAlignment="Center" in the
    ///     XAML center it) instead of computed - dividing by a very small _zoom (this preview is
    ///     often used well below "fit") blows the center-preserving math up into an offset far
    ///     outside the actual (shrunk) content, scrolling it out of view entirely.
    /// </summary>
    private void ApplyZoom(double zoom, bool keepFit)
    {
        zoom = Math.Clamp(zoom, ZoomMin, ZoomMax);
        _zoomIsFit = keepFit;

        var viewport = ScrollHost.Viewport;
        var oldOffset = ScrollHost.Offset;
        var imageCenterX = (oldOffset.X + viewport.Width / 2) / _zoom;
        var imageCenterY = (oldOffset.Y + viewport.Height / 2) / _zoom;

        _zoom = zoom;
        var transform = (ScaleTransform)ZoomHost.LayoutTransform!;
        transform.ScaleX = _zoom;
        transform.ScaleY = _zoom;

        var imageSize = _viewModel?.CurrentFloorImageBitmap?.PixelSize;
        var contentWidth = (imageSize?.Width ?? 0) * _zoom;
        var contentHeight = (imageSize?.Height ?? 0) * _zoom;

        var offsetX = contentWidth > viewport.Width ? imageCenterX * _zoom - viewport.Width / 2 : 0;
        var offsetY = contentHeight > viewport.Height ? imageCenterY * _zoom - viewport.Height / 2 : 0;
        ScrollHost.Offset = new Vector(offsetX, offsetY);

        UpdateZoomLabel();
    }

    /// <summary>Centers the viewport on an image-space point - identical to MapEditCanvasControl.PanToPoint.</summary>
    private void PanToPoint(double imageX, double imageY)
    {
        var viewport = ScrollHost.Viewport;
        ScrollHost.Offset = new Vector(imageX * _zoom - viewport.Width / 2, imageY * _zoom - viewport.Height / 2);
    }

    /// <summary>
    ///     Fits the given image-space extent (the region MapEditCanvasControl currently has
    ///     visible) into this view's own viewport and pans to center the given point, for an owner
    ///     mirroring another control's viewport (MapLiveWindow's "Vorschau" tracking
    ///     MapEditCanvasControl). Takes the source's visible WIDTH/HEIGHT rather than its raw zoom
    ///     factor - the source's zoom% is only meaningful relative to its own viewport size, so
    ///     applying the same zoom% verbatim here (a differently-sized, usually much smaller
    ///     viewport) would show a tiny sliver of the map at "true" pixel scale instead of the same
    ///     field of view scaled down to fit, which is the entire point of a live minimap. Computing
    ///     our own fit-zoom from the source's visible extent (exactly like ComputeFitZoom does
    ///     against the whole image) is what actually keeps the two views showing the same content.
    /// </summary>
    public void SetExternalViewport(double visibleImageWidth, double visibleImageHeight, double centerImageX,
        double centerImageY)
    {
        _zoomIsFit = false;

        var viewport = ScrollHost.Viewport;
        _zoom = visibleImageWidth <= 0 || visibleImageHeight <= 0 || viewport.Width <= 0 || viewport.Height <= 0
            ? _zoom
            : Math.Min(viewport.Width / visibleImageWidth, viewport.Height / visibleImageHeight);

        var transform = (ScaleTransform)ZoomHost.LayoutTransform!;
        transform.ScaleX = _zoom;
        transform.ScaleY = _zoom;

        // Can't just call PanToPoint here - unlike ApplyZoom, it always computes an offset
        // assuming the content fills the viewport on both axes. When it doesn't (this preview is
        // often used well below "fit" for a small thumbnail), ZoomHost's own Center alignment is
        // what actually centers the content, and any nonzero Offset PanToPoint computed on top of
        // that just adds a spurious, inconsistently-clamped shift. Same content-vs-viewport guard
        // as ApplyZoom.
        var imageSize = _viewModel?.CurrentFloorImageBitmap?.PixelSize;
        var contentWidth = (imageSize?.Width ?? 0) * _zoom;
        var contentHeight = (imageSize?.Height ?? 0) * _zoom;
        var offsetX = contentWidth > viewport.Width ? centerImageX * _zoom - viewport.Width / 2 : 0;
        var offsetY = contentHeight > viewport.Height ? centerImageY * _zoom - viewport.Height / 2 : 0;
        ScrollHost.Offset = new Vector(offsetX, offsetY);

        UpdateZoomLabel();
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
    ///     Ctrl+wheel zooms (same convention as MapEditCanvasControl); a plain wheel scrolls
    ///     vertically and Shift+wheel scrolls horizontally, both via the ScrollViewer's own
    ///     default wheel handling - left unhandled here so that keeps working. Right/middle-click
    ///     drag (see OnScrollHostPointerPressed) is the other way to pan.
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        ApplyZoom(_zoom + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep), false);
        e.Handled = true;
    }
}
