using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using RpgTimeTracker.Shared.Services.Visuals;
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

    /// <summary>
    ///     This viewer's own ClientId (see ClientSettingsService.GetOrCreateClientId), set by the
    ///     owner (MediaWindow, the real PlayerClient window) so a stroke drawn locally here uses
    ///     the same color every other participant will see it in once it's broadcast back - empty
    ///     on the GM's own views, which never draw strokes themselves (see PingRequested's doc
    ///     comment: the GM has Ping instead).
    /// </summary>
    public string SelfClientId { get; set; } = string.Empty;

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

        if (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            _viewModel?.CanAnnotate != false)
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
    ///     Starts capturing a freehand stroke, rendered live as the pointer moves, colored by this
    ///     viewer's own SelfClientId so it already matches the color everyone else will see once
    ///     it's broadcast back (see PainterTagHelper).
    /// </summary>
    private void BeginStroke(Point firstPoint)
    {
        _strokeImagePoints = [firstPoint];
        _activeStrokeVisual = new Polyline
        {
            Points = [firstPoint],
            Stroke = PainterTagHelper.BrushFor(SelfClientId),
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        AnnotationOverlay.Children.Add(_activeStrokeVisual);
    }

    /// <summary>
    ///     Minimum image-space distance (native map pixels) between two recorded stroke points -
    ///     PointerMoved fires far more often than the line's visual shape needs, and without this
    ///     a single few-second drag serializes to hundreds of points (each an
    ///     AnnotationPoint {X,Y} in the wire payload), which can push map.annotationFromPlayer past
    ///     TcpPlayerServerService.MaxInboundRpcPayloadBytes and silently kill the connection - a
    ///     real bug hit in practice, not just a hypothetical. Filtering here keeps strokes visually
    ///     identical while bounding their size regardless of how slowly/long someone drags.
    /// </summary>
    private const double MinStrokePointSpacing = 2.0;

    private void AppendStrokePoint(Point imagePoint)
    {
        if (_strokeImagePoints is not { } points || _activeStrokeVisual is not { } visual) return;

        var last = points[^1];
        var dx = imagePoint.X - last.X;
        var dy = imagePoint.Y - last.Y;
        if (dx * dx + dy * dy < MinStrokePointSpacing * MinStrokePointSpacing) return;

        points.Add(imagePoint);
        visual.Points!.Add(imagePoint);
    }

    /// <summary>
    ///     Ends stroke capture - a completed stroke (real movement) fades out locally (see
    ///     StrokeFadeDuration - strokes stay up much longer than the ping ripple, since the whole
    ///     point is for others to have time to actually read what was drawn) and is reported via
    ///     AnnotationRequested; a stroke with no movement (a plain click that happened to also hold
    ///     Shift) is discarded silently rather than sent as a meaningless one-point "stroke".
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
            AnnotationOverlay.Children.Remove(visual);
            return;
        }

        AnnotationRequested?.Invoke(points);
        await FadeOutAndRemoveAsync(visual, StrokeFadeDuration);
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
            _viewModel.VisibleLines.CollectionChanged -= OnVisibleLinesChanged;
        }

        _viewModel = DataContext as MapDisplayViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ActiveCharacterZoomRequested += OnActiveCharacterZoomRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.PingReceived += OnPingReceived;
            _viewModel.AnnotationReceived += OnAnnotationReceived;
            _viewModel.VisibleLines.CollectionChanged += OnVisibleLinesChanged;
        }

        _persistedLineVisuals.Clear();
        RedrawPersistedLines();
    }

    private readonly Dictionary<Guid, Polyline> _persistedLineVisuals = new();

    /// <summary>
    ///     Keeps AnnotationOverlay's persisted-line polylines in sync with
    ///     MapDisplayViewModel.VisibleLines (SemiPermanent/Permanent lines - see RpcMethods.MapLineUpsert/
    ///     MapLineRemove/MapLineClearAll) - unlike the ephemeral player stroke (OnAnnotationReceived),
    ///     these never fade on their own; they only disappear when explicitly removed/cleared or
    ///     the floor changes.
    /// </summary>
    private void OnVisibleLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RedrawPersistedLines();
    }

    private void RedrawPersistedLines()
    {
        foreach (var visual in _persistedLineVisuals.Values) AnnotationOverlay.Children.Remove(visual);
        _persistedLineVisuals.Clear();

        if (_viewModel is null) return;

        foreach (var line in _viewModel.VisibleLines)
        {
            var polyline = new Polyline
            {
                Points = new Points(line.Points.Select(p => new Point(p.X, p.Y))),
                Stroke = new SolidColorBrush(Color.Parse(line.ColorHex)),
                StrokeThickness = 4,
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            AnnotationOverlay.Children.Add(polyline);
            _persistedLineVisuals[line.Id] = polyline;
        }
    }

    private static readonly TimeSpan PingFadeDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>
    ///     Strokes stay up much longer than the ping ripple - a ping is a fleeting "look here"
    ///     pointer, but a stroke is meant to be actually read (an arrow, a circled area, etc.), so
    ///     it needs enough time for everyone else to notice and look at it before it's gone.
    /// </summary>
    private static readonly TimeSpan StrokeFadeDuration = TimeSpan.FromSeconds(8);

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
            RenderTransform = new TranslateTransform(x - diameter / 2, y - diameter / 2)
        };
        AnnotationOverlay.Children.Add(ring);
        await FadeOutAndRemoveAsync(ring, PingFadeDuration);
    }

    /// <summary>
    ///     Renders a freehand annotation stroke, points already in image-space - same fade-and-remove
    ///     treatment as the ping ripple, just much longer (see StrokeFadeDuration). Reaches every
    ///     connected view, not just the GM's (see RpcMethods.MapAnnotationBroadcast). The GM's own
    ///     Temporary-tier Draw-tool stroke (see MainWindowViewModel.BroadcastGmAnnotationAsync) is
    ///     identified via PainterTagHelper.TryGetGmColor rather than falling into ColorFor's "unknown
    ///     painter" look (a red stroke labeled "????") - every real player has a persistent
    ///     non-empty ClientId of a different shape (see ClientSettingsService.GetOrCreateClientId),
    ///     so this is an unambiguous signal. Rendered in the GM's picked line color with no
    ///     player-tag label.
    /// </summary>
    private async void OnAnnotationReceived(IReadOnlyList<AnnotationPoint> points, string clientId)
    {
        var isGm = PainterTagHelper.TryGetGmColor(clientId, out var gmColor);
        var color = isGm ? gmColor : PainterTagHelper.ColorFor(clientId);
        var brush = new SolidColorBrush(color);
        var visual = new Polyline
        {
            Points = new Points(points.Select(p => new Point(p.X, p.Y))),
            Stroke = brush,
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        AnnotationOverlay.Children.Add(visual);

        if (isGm)
        {
            await FadeOutAndRemoveAsync(visual, StrokeFadeDuration);
            return;
        }

        var firstPoint = points[0];
        var label = new Border
        {
            Background = new SolidColorBrush(color, 0.85),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = PainterTagHelper.ShortTagFor(clientId),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            },
            RenderTransform = new TranslateTransform(firstPoint.X + 8, firstPoint.Y - 20)
        };
        AnnotationOverlay.Children.Add(label);

        await Task.WhenAll(FadeOutAndRemoveAsync(visual, StrokeFadeDuration),
            FadeOutAndRemoveAsync(label, StrokeFadeDuration));
    }

    /// <summary>
    ///     Shared fade-out-then-remove for the ping ripple and both annotation-stroke paths (drawn
    ///     locally and received). Removal is driven by a plain Task.Delay rather than awaiting
    ///     Animation.RunAsync's own completion - the same pattern in MapEditCanvasControl's copy of
    ///     this method was observed (via diagnostic logging) to never actually complete, leaving
    ///     stroke visuals piling up forever instead of disappearing. Firing the animation without
    ///     awaiting it and using a plain timer for removal keeps the visual disappearing reliably
    ///     even if the opacity animation itself doesn't finish for whatever reason.
    /// </summary>
    private async Task FadeOutAndRemoveAsync(Control visual, TimeSpan duration)
    {
        var fadeOut = new Animation
        {
            Duration = duration,
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
        _ = fadeOut.RunAsync(visual);
        await Task.Delay(duration);
        AnnotationOverlay.Children.Remove(visual);
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
