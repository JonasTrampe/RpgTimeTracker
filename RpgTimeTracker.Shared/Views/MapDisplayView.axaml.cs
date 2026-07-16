using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    private double _zoom = 1.0;
    private bool _zoomIsFit = true;
    private MapDisplayViewModel? _viewModel;

    /// <summary>Right- or middle-button drag pan state - left button is left free for whatever future token/marker interaction players might get.</summary>
    private Point? _dragStartPointerPosition;

    private Vector _dragStartOffset;

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

    /// <summary>Right/middle-button drag pans the map - left click is left free, and there are no scrollbars/wheel-scroll to pan with otherwise since wheel now zooms directly (see OnPointerWheelChanged).</summary>
    private void OnScrollHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(ScrollHost);
        if (!point.Properties.IsRightButtonPressed && !point.Properties.IsMiddleButtonPressed) return;

        _dragStartPointerPosition = e.GetPosition(ScrollHost);
        _dragStartOffset = ScrollHost.Offset;
        e.Pointer.Capture(ScrollHost);
        e.Handled = true;
    }

    private void OnScrollHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPointerPosition is not { } start || !ReferenceEquals(e.Pointer.Captured, ScrollHost)) return;

        var delta = e.GetPosition(ScrollHost) - start;
        ScrollHost.Offset = new Vector(_dragStartOffset.X - delta.X, _dragStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    private void OnScrollHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
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
        }

        _viewModel = DataContext as MapDisplayViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ActiveCharacterZoomRequested += OnActiveCharacterZoomRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    ///     Re-fits whenever a new floor image is shown (ShowMap, floor navigation, or an
    ///     auto-zoom-triggered floor switch all funnel through
    ///     MapDisplayViewModel.CurrentFloorImageBitmap) - matches
    ///     MapEditCanvasControl.LoadFloor's "a newly loaded floor always starts fit-to-window"
    ///     convention, except the auto-zoom sequence below immediately overrides this with the
    ///     configured zoom level when that's what triggered the floor switch.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapDisplayViewModel.CurrentFloorImageBitmap)) return;

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
