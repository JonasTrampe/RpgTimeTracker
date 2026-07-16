using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly DispatcherTimer _flushTimer;
    private readonly MapItemViewModel _map;
    private readonly List<FogCellDto> _pendingCells = [];
    private readonly MapDisplayViewModel _previewDisplay = new();
    private readonly MainWindowViewModel _vm;

    public MapLiveWindow(MainWindowViewModel vm, MapItemViewModel map)
    {
        InitializeComponent();

        _vm = vm;
        _map = map;
        Title = string.Format(LocalizationService.Get("MapLiveWindow.Title"), map.Name);
        OpenToPlayersToggle.IsChecked = _vm.IsMapOpenToPlayers && _vm.OpenMap == map;
        AutoZoomToggle.IsChecked = _vm.AutoZoomEnabled;

        PreviewDisplay.DataContext = _previewDisplay;

        EditCanvas.Configure(_vm.GetLiveFog, FogOverlayRenderer.EditorHiddenColor);
        EditCanvas.FloorChanged += OnFloorChanged;
        EditCanvas.CellsPainted += OnCellsPainted;
        EditCanvas.SetFloors(map.Floors, _vm.EditingFloor is not null && map.Floors.Contains(_vm.EditingFloor)
            ? _vm.EditingFloor
            : map.Floors.Count > 0
                ? map.Floors[0]
                : null);
        EditCanvas.ConfigureTokens(_vm);
        EditCanvas.SetMap(_map);
        EditCanvas.TokenSelected += token => TokenPanel.SelectedToken = token;
        EditCanvas.TokenMoved += _ => RefreshPreviewTokens();
        TokenPanel.Configure(_vm, _map);
        TokenPanel.TokensMutated += EditCanvas.RefreshTokens;
        TokenPanel.TokensMutated += RefreshPreviewTokens;

        InitiativePanel.Configure(_vm, _map);
        _vm.InitiativeTurnChanged += OnInitiativeTurnChanged;

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => _ = FlushPendingAsync();
        _flushTimer.Start();
    }

    private void OnFloorChanged(MapFloorItemViewModel? floor)
    {
        _vm.EditingFloor = floor;
        RefreshPreviewFloor(floor);
        RefreshPreviewTokens();
    }

    private void OnCellsPainted(MapFloorItemViewModel floor, IReadOnlyList<FogCellDto> cells)
    {
        _pendingCells.AddRange(cells);
        // Same FogMask instance as the preview/local player-window preview (MainWindowViewModel.MapDisplay)
        // already use - just tell them to re-render, no cell list to hand over.
        _vm.MapDisplay.NotifyFogChanged(floor.Id);
        _previewDisplay.NotifyFogChanged(floor.Id);
        // A HiddenUntilRevealed token sitting in one of the just-painted cells may have just
        // become visible (or hidden again) - re-evaluate just those tokens' network/preview state.
        _vm.NotifyTokensAffectedByFogChange(_map, floor, cells);
        RefreshPreviewTokens();
    }

    /// <summary>
    ///     Re-resolves this preview's full token set from scratch (see MapDisplayViewModel.
    ///     ReplaceAllTokens) - this window's own "Vorschau" MapDisplayViewModel is a separate
    ///     instance from MainWindowViewModel.MapDisplay and isn't kept in sync by
    ///     RefreshTokenPlayerState (which only targets whichever map is open to players), so it
    ///     needs its own refresh on every token add/edit/delete/drag and on every floor
    ///     switch (ShowMap already clears _previewDisplay's tokens) or fog reveal change.
    /// </summary>
    private void RefreshPreviewTokens()
    {
        _previewDisplay.ReplaceAllTokens(_vm.GetVisibleTokenSnapshots(_map));
    }

    /// <summary>
    ///     Rebuilds the preview's single-floor state for the newly selected floor - cheap
    ///     (one floor, only on floor switch/reset/rescale) and necessary since the FogMask instance
    ///     for a floor can be replaced entirely (RescaleFloorCellSizeAsync), not just mutated.
    /// </summary>
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
            new MapDisplayFloor
                { FloorId = floor.Id, Name = floor.Name, Image = image, CurrentFog = _vm.GetLiveFog(floor) }
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

    /// <summary>
    ///     Same setting the Settings tab's "Auto-zoom to active character" checkbox controls
    ///     (MainWindowViewModel.AutoZoomEnabled) - surfaced here too since it's a map-viewing
    ///     behavior a GM would expect to toggle right where they're looking at the map. Also
    ///     gates the GM's own EditCanvas jump (see OnInitiativeTurnChanged) - previously that
    ///     always jumped regardless of any setting.
    /// </summary>
    private void OnAutoZoomToggleClick(object? sender, RoutedEventArgs e)
    {
        _vm.AutoZoomEnabled = AutoZoomToggle.IsChecked == true;
    }

    /// <summary>
    ///     Reacts to the initiative tracker's current-turn token changing (#70) - switches floor
    ///     (a token might be on a different floor than the one currently shown) and pans the
    ///     canvas to it. No-op for a map this window isn't editing, or when there's no placed
    ///     token to jump to (a freeform entry, or a Character with no token on this map).
    /// </summary>
    private void OnInitiativeTurnChanged(MapItemViewModel map, MapTokenViewModel? token)
    {
        if (!_vm.AutoZoomEnabled || !ReferenceEquals(map, _map) || token is null) return;

        if (EditCanvas.CurrentFloor?.Id != token.FloorId)
        {
            var floor = _map.Floors.FirstOrDefault(f => f.Id == token.FloorId);
            if (floor is not null) FloorSelectorSelect(floor);
        }

        EditCanvas.PanToPoint(token.X, token.Y);
    }

    /// <summary>Selects a floor via the same public API SetFloors already exposes, without re-supplying the full list.</summary>
    private void FloorSelectorSelect(MapFloorItemViewModel floor)
    {
        EditCanvas.SetFloors(_map.Floors, floor);
    }

    protected override void OnClosed(EventArgs e)
    {
        _flushTimer.Stop();
        _vm.InitiativeTurnChanged -= OnInitiativeTurnChanged;
        base.OnClosed(e);
    }
}