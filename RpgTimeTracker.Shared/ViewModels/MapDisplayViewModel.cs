using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Shared "what does a map look like right now" display state, used by both the Host's local
///     player-window preview (MainWindowViewModel.MapDisplay) and the real PlayerClient
///     (ClientMainWindowViewModel.MapDisplay) - floor image + fog cutout + local floor
///     navigation, identical in both places. The two owners differ only in how they feed data
///     in: the PlayerClient deserializes fog from the network, the Host shares the exact same
///     FogMask instance it's painting into (see NotifyFogChanged) - see ApplyFogCells vs
///     NotifyFogChanged below.
///     Rendering model (see MapDisplayView.axaml): the sharp floor image is always shown;
///     hidden cells are cut out of a second, blurred copy of the *same* image plus a color-tint
///     layer, both clipped via MaskBrush (an opacity mask built from the live FogMask, see
///     FogOverlayRenderer.BuildMaskBitmap). Blurring the actual map content (rather than a flat
///     fog-colored blob) is what issue #22's blur setting visibly does now - blurring a one-
///     pixel-per-cell colored bitmap was next to invisible once stretched across a large map.
/// </summary>
public sealed partial class MapDisplayViewModel : ObservableObject
{
    private readonly List<MapDisplayFloor> _floors = [];

    /// <summary>Every currently-visible token across all floors - see VisibleTokens for the current-floor filter.</summary>
    private readonly List<MapTokenSnapshotDto> _tokens = [];

    /// <summary>
    ///     Whether the blurred-image layer is used at all - independent of BlurRadius so
    ///     toggling it off/on doesn't lose the configured strength. When false, only the flat
    ///     tint layer obscures hidden cells (no image-content blur).
    /// </summary>
    [ObservableProperty] private bool _blurEnabled = true;

    /// <summary>
    ///     Blur radius (device-independent pixels) applied to the blurred map-image layer -
    ///     bound directly to a BlurEffect in MapDisplayView.axaml, no bitmap rebuild needed. Kept
    ///     even while BlurEnabled is false, so re-enabling restores the previous strength.
    /// </summary>
    [ObservableProperty] private double _blurRadius;

    [ObservableProperty] private Bitmap? _currentFloorImageBitmap;
    [ObservableProperty] private int _currentFloorIndex;
    [ObservableProperty] private string _currentFloorName = string.Empty;

    /// <summary>
    ///     Whether MaskBrush is actually set. Bound to IsVisible on the blurred-image and tint
    ///     layers in MapDisplayView.axaml so they're skipped entirely (never rendered) instead of
    ///     relying on a null OpacityMask to mean "fully transparent" - it doesn't: an element with
    ///     no OpacityMask renders at full, UNCLIPPED opacity, which turned the tint layer (opaque
    ///     by default) into a solid block covering the whole sharp image underneath whenever a
    ///     floor's fog hadn't been resolved yet (e.g. still deserializing on the PlayerClient).
    /// </summary>
    [ObservableProperty] private bool _hasFogMask;

    /// <summary>
    ///     Player-side fog render style (see issue #22) - one global GM preference, applied
    ///     via ApplyRenderStyle by both the Host (from its own settings) and the PlayerClient
    ///     (from session.snapshot/map.renderStyleChanged).
    /// </summary>
    [ObservableProperty] private Color _hiddenColor = FogOverlayRenderer.PlayerHiddenColor;

    [ObservableProperty] private bool _isShowingMap;
    [ObservableProperty] private string _mapName = string.Empty;
    [ObservableProperty] private IBrush? _maskBrush;

    /// <summary>Solid-color brush for the tint layer, kept in sync with HiddenColor.</summary>
    [ObservableProperty] private IBrush _tintBrush = new SolidColorBrush(FogOverlayRenderer.PlayerHiddenColor);

    /// <summary>
    ///     Bound to IsVisible on the blurred-image layer in MapDisplayView.axaml - shown only
    ///     when there's a real fog mask to clip against AND blur is enabled.
    /// </summary>
    public bool ShowBlurLayer => HasFogMask && BlurEnabled;

    public bool HasMultipleFloors => _floors.Count > 1;

    /// <summary>
    ///     Tokens on the currently displayed floor only, already fully resolved/filtered by
    ///     the Host (see MapTokenSnapshotDto) - the client (this class serves both the real
    ///     PlayerClient and the Host's own local preview) has no extra visibility logic of its own
    ///     to apply, it just renders whatever's here.
    /// </summary>
    public ObservableCollection<MapTokenSnapshotDto> VisibleTokens { get; } = [];

    partial void OnHiddenColorChanged(Color value)
    {
        TintBrush = new SolidColorBrush(value);
    }

    partial void OnHasFogMaskChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBlurLayer));
    }

    partial void OnBlurEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBlurLayer));
    }

    public void ApplyRenderStyle(Color color, double blurRadius, bool blurEnabled)
    {
        HiddenColor = color;
        BlurRadius = blurRadius;
        BlurEnabled = blurEnabled;
    }

    [RelayCommand]
    private void PreviousFloor()
    {
        Navigate(-1);
    }

    [RelayCommand]
    private void NextFloor()
    {
        Navigate(1);
    }

    public void ShowMap(string mapName, IReadOnlyList<MapDisplayFloor> floors)
    {
        _floors.Clear();
        _floors.AddRange(floors);
        _tokens.Clear();
        MapName = mapName;
        CurrentFloorIndex = 0;
        IsShowingMap = true;
        OnPropertyChanged(nameof(HasMultipleFloors));
        DisplayCurrentFloor();
    }

    public void HideMap()
    {
        IsShowingMap = false;
        _floors.Clear();
        _tokens.Clear();
        VisibleTokens.Clear();
        CurrentFloorImageBitmap = null;
        MaskBrush = null;
        HasFogMask = false;
        CurrentFloorName = string.Empty;
    }

    /// <summary>Adds a token, or fully replaces an existing one with the same Id (see RpcMethods.MapTokenUpsert).</summary>
    public void UpsertToken(MapTokenSnapshotDto token)
    {
        var index = _tokens.FindIndex(t => t.Id == token.Id);
        if (index >= 0) _tokens[index] = token;
        else _tokens.Add(token);

        RefreshVisibleTokens();
    }

    public void RemoveToken(Guid tokenId)
    {
        _tokens.RemoveAll(t => t.Id == tokenId);
        RefreshVisibleTokens();
    }

    private void RefreshVisibleTokens()
    {
        VisibleTokens.Clear();
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floorId = _floors[CurrentFloorIndex].FloorId;
        foreach (var token in _tokens.Where(t => t.FloorId == floorId)) VisibleTokens.Add(token);
    }

    /// <summary>
    ///     Applies reveal/hide cells to a floor's own FogMask and refreshes the mask if it's
    ///     currently displayed - for a caller whose FogMask is an independent copy that must be
    ///     told about each change explicitly (the PlayerClient, after deserializing from the
    ///     network).
    /// </summary>
    public void ApplyFogCells(Guid floorId, IEnumerable<FogCellDto> cells)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor?.CurrentFog is null) return;

        foreach (var cell in cells) floor.CurrentFog.SetRevealed(cell.X, cell.Y, cell.Revealed);
        RefreshIfCurrent(floor);
    }

    public void ResetFloorFog(Guid floorId, FogMask startingFog)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is null) return;

        floor.CurrentFog = startingFog;
        RefreshIfCurrent(floor);
    }

    /// <summary>
    ///     Tells the display to re-render a floor because its FogMask was already mutated
    ///     directly - for a caller that shares the exact same FogMask instance (the Host, whose
    ///     MapEditorWindow paints straight into MainWindowViewModel.GetLiveFog's object).
    /// </summary>
    public void NotifyFogChanged(Guid floorId)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is not null) RefreshIfCurrent(floor);
    }

    /// <summary>
    ///     Updates a floor's map image after the fact - covers the case where the image arrives
    ///     (or is decoded) after ShowMap already ran for this map, so the display self-heals
    ///     instead of staying blank until the next full resync.
    /// </summary>
    public void UpdateFloorImage(Guid floorId, Bitmap? image)
    {
        var floor = _floors.FirstOrDefault(f => f.FloorId == floorId);
        if (floor is null) return;

        floor.Image = image;
        if (_floors.IndexOf(floor) == CurrentFloorIndex) CurrentFloorImageBitmap = image;
    }

    private void RefreshIfCurrent(MapDisplayFloor floor)
    {
        if (_floors.IndexOf(floor) == CurrentFloorIndex) RefreshMask();
    }

    private void Navigate(int direction)
    {
        if (_floors.Count == 0) return;

        CurrentFloorIndex = (CurrentFloorIndex + direction + _floors.Count) % _floors.Count;
        DisplayCurrentFloor();
    }

    private void DisplayCurrentFloor()
    {
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floor = _floors[CurrentFloorIndex];
        CurrentFloorName = floor.Name;
        CurrentFloorImageBitmap = floor.Image;
        RefreshMask();
        RefreshVisibleTokens();
    }

    private void RefreshMask()
    {
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floor = _floors[CurrentFloorIndex];
        if (floor.CurrentFog is null)
        {
            MaskBrush = null;
            HasFogMask = false;
            return;
        }

        // SourceRect/DestinationRect are set explicitly (not just Stretch) because OpacityMask
        // maps a TileBrush onto the masked element's bounds differently from Fill/Background -
        // without them, this tiny (one-pixel-per-cell) bitmap rendered at its native pixel size
        // in a corner of the image instead of stretching to cover it.
        MaskBrush = new ImageBrush(FogOverlayRenderer.BuildMaskBitmap(floor.CurrentFog))
        {
            Stretch = Stretch.Fill,
            SourceRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative),
            DestinationRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative)
        };
        HasFogMask = true;
    }
}

/// <summary>
///     One floor's data as shown by MapDisplayViewModel (not the editing/authoring shape -
///     see MapFloorItemViewModel on the Host for that).
/// </summary>
public sealed class MapDisplayFloor
{
    public Guid FloorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Bitmap? Image { get; set; }
    public FogMask? CurrentFog { get; set; }
}