using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

/// <summary>
///     SL-only fog editor for one map, opened by its "Show" button: brush reveal/hide directly
///     into the live/broadcast fog (see MainWindowViewModel.GetLiveFog), "Reset to Prepared"
///     (pulls MapPrepareWindow's saved template into live), and open/close to players. The
///     floor-selector/brush/canvas are the shared MapEditCanvasControl; this window owns only
///     what's specific to the live/broadcast fog (network flush, Reset, Open toggle) plus a local
///     preview using the real player-facing render (MapDisplayViewModel/MapDisplayView - the same
///     component the Host's own PlayerWindow and the real PlayerClient use), fed the exact same
///     FogMask instance being painted into, so it stays in sync via NotifyFogChanged with no data
///     copy. Not bound via DataContext to MainWindowViewModel like the main tabs - constructed
///     directly with the map/viewmodel it edits, matching the IconPickerWindow/
///     MediaLibraryPickerWindow pattern for focused child windows.
/// </summary>
public partial class MapLiveWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly MapItemViewModel _map;
    private readonly List<FogCellDto> _pendingCells = [];
    private readonly DispatcherTimer _flushTimer;
    private readonly MapDisplayViewModel _previewDisplay = new();

    public MapLiveWindow(MainWindowViewModel vm, MapItemViewModel map)
    {
        InitializeComponent();

        _vm = vm;
        _map = map;
        Title = string.Format(LocalizationService.Get("MapLiveWindow.Title"), map.Name);
        OpenToPlayersToggle.IsChecked = _vm.IsMapOpenToPlayers && _vm.OpenMap == map;

        PreviewDisplay.DataContext = _previewDisplay;

        EditCanvas.Configure(_vm.GetLiveFog, FogOverlayRenderer.EditorHiddenColor);
        EditCanvas.FloorChanged += OnFloorChanged;
        EditCanvas.CellsPainted += OnCellsPainted;
        EditCanvas.SetFloors(map.Floors, _vm.EditingFloor is not null && map.Floors.Contains(_vm.EditingFloor)
            ? _vm.EditingFloor
            : map.Floors.Count > 0 ? map.Floors[0] : null);

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => _ = FlushPendingAsync();
        _flushTimer.Start();
    }

    private void OnFloorChanged(MapFloorItemViewModel? floor)
    {
        _vm.EditingFloor = floor;
        RefreshPreviewFloor(floor);
    }

    private void OnCellsPainted(MapFloorItemViewModel floor, IReadOnlyList<FogCellDto> cells)
    {
        _pendingCells.AddRange(cells);
        // Same FogMask instance as the preview/local player-window preview (MainWindowViewModel.MapDisplay)
        // already use - just tell them to re-render, no cell list to hand over.
        _vm.MapDisplay.NotifyFogChanged(floor.Id);
        _previewDisplay.NotifyFogChanged(floor.Id);
    }

    /// <summary>Rebuilds the preview's single-floor state for the newly selected floor - cheap
    ///     (one floor, only on floor switch/reset/rescale) and necessary since the FogMask instance
    ///     for a floor can be replaced entirely (RescaleFloorCellSizeAsync), not just mutated.</summary>
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
            new MapDisplayFloor { FloorId = floor.Id, Name = floor.Name, Image = image, CurrentFog = _vm.GetLiveFog(floor) }
        ]);

        var style = _vm.GetEffectiveFogStyle(_map);
        _previewDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(style.ColorHex, style.OpacityPercent),
            style.BlurRadius, style.BlurEnabled);
    }

    private async Task FlushPendingAsync()
    {
        if (_pendingCells.Count == 0 || EditCanvas.CurrentFloor is not { } floor) return;

        var cells = new List<FogCellDto>(_pendingCells);
        _pendingCells.Clear();
        await _vm.BroadcastFogCellsAsync(floor.Id, cells);
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (EditCanvas.CurrentFloor is not { } floor) return;

        await _vm.ResetFloorFogToStartingAsync(floor);
        EditCanvas.RefreshOverlay();
        _previewDisplay.NotifyFogChanged(floor.Id);
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
