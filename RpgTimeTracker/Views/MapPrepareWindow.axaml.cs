using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

/// <summary>
///     SL-only editor for one map's floor, opened by a floor's "Edit" button: paints only the
///     in-memory prepare-mode fog (see MainWindowViewModel.GetPrepareFog), which is debounced-
///     saved back to the floor's on-disk starting-fog file (SavePrepareFogAsync) and nothing
///     else - no network broadcast, no open/close-to-players control. Edits here are structurally
///     invisible to players no matter what; MapLiveWindow's "Reset to Prepared" is what actually
///     pushes this template into the live/broadcast fog. The floor-selector/brush/canvas are the
///     shared MapEditCanvasControl; this window owns only what's specific to preparing (fog-style
///     override, cell size, debounced file save) plus a local preview using the real
///     player-facing render (MapDisplayViewModel/MapDisplayView - same component the Host's own
///     PlayerWindow and the real PlayerClient use) simulating what this map would look like live
///     right now, entirely independent of whether it actually is. Not bound via DataContext to
///     MainWindowViewModel like the main tabs - constructed directly with the map/viewmodel it
///     edits, matching the IconPickerWindow/MediaLibraryPickerWindow pattern for focused child
///     windows.
/// </summary>
public partial class MapPrepareWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly MapItemViewModel _map;
    private readonly DispatcherTimer _flushTimer;
    private readonly MapDisplayViewModel _previewDisplay = new();
    private bool _isDirty;

    public MapPrepareWindow(MainWindowViewModel vm, MapItemViewModel map)
    {
        InitializeComponent();

        _vm = vm;
        _map = map;
        Title = string.Format(LocalizationService.Get("MapPrepareWindow.Title"), map.Name);

        PreviewDisplay.DataContext = _previewDisplay;

        UpdateOpenStatusLabel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NotifyMapPrepareWindowOpened(map.Id);

        EditCanvas.Configure(_vm.GetPrepareFog, FogOverlayRenderer.PrepareHiddenColor);
        EditCanvas.FloorChanged += OnFloorChanged;
        EditCanvas.CellsPainted += OnCellsPainted;
        EditCanvas.SetFloors(map.Floors, _vm.EditingFloor is not null && map.Floors.Contains(_vm.EditingFloor)
            ? _vm.EditingFloor
            : map.Floors.Count > 0 ? map.Floors[0] : null);

        InitializeFogStyleControls();
        InitializeCellSizeControl();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => _ = FlushPendingAsync();
        _flushTimer.Start();
    }

    private void OnFloorChanged(MapFloorItemViewModel? floor)
    {
        _ = FlushPendingAsync();
        CellSizeUpDown.Value = floor?.CellSizePx ?? _map.DefaultCellSizePx;
        UpdateCellSizeWarning();
        RefreshPreviewFloor(floor);
    }

    private void OnCellsPainted(MapFloorItemViewModel floor, IReadOnlyList<FogCellDto> cells)
    {
        _isDirty = true;
        _previewDisplay.NotifyFogChanged(floor.Id);
    }

    /// <summary>Rebuilds the preview's single-floor state for the newly selected floor - cheap
    ///     (one floor, only on floor switch/rescale) and necessary since a floor's FogMask
    ///     instance can be replaced entirely (RescaleFloorCellSizeAsync), not just mutated.</summary>
    private void RefreshPreviewFloor(MapFloorItemViewModel? floor)
    {
        if (floor is null)
        {
            _previewDisplay.HideMap();
            return;
        }

        using var stream = File.OpenRead(floor.ImagePath);
        var image = new Bitmap(stream);
        _previewDisplay.ShowMap(_map.Name, [
            new MapDisplayFloor { FloorId = floor.Id, Name = floor.Name, Image = image, CurrentFog = _vm.GetPrepareFog(floor) }
        ]);
        RefreshPreviewStyle();
    }

    private void RefreshPreviewStyle()
    {
        var style = _vm.GetEffectiveFogStyle(_map);
        _previewDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(style.ColorHex, style.OpacityPercent),
            style.BlurRadius, style.BlurEnabled);
    }

    /// <summary>Seeds the fog-style controls from the map's override (falling back to the global
    ///     value when unset) and wires them to update the override - and the local preview - live
    ///     on change.</summary>
    private void InitializeFogStyleControls()
    {
        var colorHex = _map.FogColorHex ?? _vm.FogColorHex;
        FogColorPickerControl.Color = Color.TryParse(colorHex, out var parsed) ? parsed : Colors.Black;
        FogOpacitySlider.Value = _map.FogOpacityPercent ?? _vm.FogOpacityPercent;
        FogBlurEnabledCheckBox.IsChecked = _map.FogBlurEnabled ?? _vm.FogBlurEnabled;
        FogBlurRadiusSlider.Value = _map.FogBlurRadius ?? _vm.FogBlurRadius;

        FogColorPickerControl.PropertyChanged += (_, e) =>
        {
            if (e.Property != ColorView.ColorProperty) return;
            _map.FogColorHex = FogColorPickerControl.Color.ToString();
            RefreshPreviewStyle();
        };
        FogOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _map.FogOpacityPercent = (int)FogOpacitySlider.Value;
            RefreshPreviewStyle();
        };
        FogBlurEnabledCheckBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleButton.IsCheckedProperty) return;
            _map.FogBlurEnabled = FogBlurEnabledCheckBox.IsChecked;
            RefreshPreviewStyle();
        };
        FogBlurRadiusSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _map.FogBlurRadius = FogBlurRadiusSlider.Value;
            RefreshPreviewStyle();
        };
    }

    private void OnUseGlobalFogDefaultClick(object? sender, RoutedEventArgs e)
    {
        _map.FogColorHex = null;
        _map.FogOpacityPercent = null;
        _map.FogBlurRadius = null;
        _map.FogBlurEnabled = null;
        InitializeFogStyleControls();
        RefreshPreviewStyle();
    }

    private void InitializeCellSizeControl()
    {
        CellSizeUpDown.Value = EditCanvas.CurrentFloor?.CellSizePx ?? _map.DefaultCellSizePx;
        UpdateCellSizeWarning();

        CellSizeUpDown.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.ValueProperty || EditCanvas.CurrentFloor is not { } floor ||
                CellSizeUpDown.Value is not { } value) return;

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
            if (newCellSizePx != floor.CellSizePx) _ = RescaleAndRefreshAsync(floor, newCellSizePx);
        };
    }

    private async Task RescaleAndRefreshAsync(MapFloorItemViewModel floor, int newCellSizePx)
    {
        await _vm.RescaleFloorCellSizeAsync(_map, floor, newCellSizePx);
        EditCanvas.RefreshOverlay();
        RefreshPreviewFloor(floor);
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

    private async Task FlushPendingAsync()
    {
        if (!_isDirty || EditCanvas.CurrentFloor is not { } floor) return;

        _isDirty = false;
        await _vm.SavePrepareFogAsync(floor);
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
