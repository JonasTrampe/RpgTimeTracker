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

    /// <summary>Every currently-active SemiPermanent/Permanent line across all floors - see VisibleLines for the current-floor filter.</summary>
    private readonly List<MapLineSnapshotDto> _lines = [];

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

    /// <summary>
    ///     Whether this player window is currently allowed to draw map annotation strokes (GM
    ///     per-window toggle, see TcpPlayerServerService.SetClientCanAnnotate) - MapDisplayView.axaml.cs
    ///     consults this before starting a Shift-drag stroke, so a disabled window doesn't draw a
    ///     stroke locally that would then silently be dropped server-side. Always true on the GM's
    ///     own views, which never draw strokes themselves anyway (see PingRequested's doc comment).
    /// </summary>
    [ObservableProperty] private bool _canAnnotate = true;

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

    /// <summary>
    ///     SemiPermanent/Permanent lines on the currently displayed floor only - MapDisplayView.axaml.cs
    ///     subscribes to CollectionChanged to draw/remove a Polyline per entry (no fade, unlike the
    ///     ephemeral map.annotationBroadcast stroke - see AnnotationReceived).
    /// </summary>
    public ObservableCollection<MapLineSnapshotDto> VisibleLines { get; } = [];

    /// <summary>
    ///     Whether a turn changing to a new Character-linked token should snap this view to
    ///     AutoZoomLevel, centered on it (a GM setting, see MainWindowViewModel.AutoZoomEnabled) -
    ///     a plain settable property (not [ObservableProperty]) since nothing in the View binds to
    ///     it directly; the Host sets it locally from its own setting, the PlayerClient sets it
    ///     from map.autoZoomChanged/session.snapshot.
    /// </summary>
    public bool AutoZoomEnabled { get; set; }

    /// <summary>Only meaningful while AutoZoomEnabled - see AutoZoomEnabled's doc comment.</summary>
    public double AutoZoomLevel { get; set; } = 2.0;

    /// <summary>
    ///     Fired from UpsertToken when a token's IsCurrentTurn just flipped to true and
    ///     AutoZoomEnabled is on - MapDisplayView.axaml.cs subscribes to zoom+pan to (x,y), the
    ///     same two-call sequence MapLiveWindow already uses GM-side via
    ///     MapEditCanvasControl.PanToPoint for the analogous GM-only convenience.
    /// </summary>
    public event Action<double, double>? ActiveCharacterZoomRequested;

    /// <summary>
    ///     Fired from NotifyPingReceived when a ping arrives for whichever floor this view
    ///     currently has displayed - MapDisplayView.axaml.cs subscribes to render the ripple
    ///     animation at (x, y).
    /// </summary>
    public event Action<double, double>? PingReceived;

    /// <summary>
    ///     A map ping (#28's "point at something on the map") arrived for the given floor -
    ///     dropped silently if that isn't the floor currently displayed here, since image-space
    ///     (x, y) only means something relative to the floor it was captured on (see
    ///     RpcMethods.MapPing's doc comment) and there's no sensible way to show a ping for a
    ///     floor the viewer isn't looking at.
    /// </summary>
    public void NotifyPingReceived(Guid floorId, double x, double y)
    {
        if (CurrentFloorId != floorId) return;

        PingReceived?.Invoke(x, y);
    }

    /// <summary>Id of the floor currently displayed, if any - for a caller sending a ping to tag with the right floor.</summary>
    public Guid? CurrentFloorId =>
        CurrentFloorIndex >= 0 && CurrentFloorIndex < _floors.Count ? _floors[CurrentFloorIndex].FloorId : null;

    /// <summary>
    ///     Fired from NotifyAnnotationReceived when a player's freehand stroke arrives for
    ///     whichever floor this view currently has displayed - MapDisplayView.axaml.cs subscribes
    ///     to render it as a fading, per-painter-colored polyline. Reaches every connected view
    ///     (GM and every other player - see RpcMethods.MapAnnotationBroadcast), not just the GM's
    ///     own, but lives here rather than duplicated per-owner since MapDisplayViewModel already
    ///     is that shared "what does this view show right now" state. The originating player's
    ///     ClientId is carried alongside the points so the view can derive a color/tag for it (see
    ///     PainterTagHelper) without the wire payload needing either.
    /// </summary>
    public event Action<IReadOnlyList<AnnotationPoint>, string>? AnnotationReceived;

    /// <summary>
    ///     A player's freehand annotation stroke arrived for the given floor - dropped silently
    ///     if that isn't the floor currently displayed here, same reasoning as NotifyPingReceived.
    /// </summary>
    public void NotifyAnnotationReceived(Guid floorId, IReadOnlyList<AnnotationPoint> points, string clientId)
    {
        if (CurrentFloorId != floorId) return;

        AnnotationReceived?.Invoke(points, clientId);
    }

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
        _lines.Clear();
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
        _lines.Clear();
        VisibleTokens.Clear();
        VisibleLines.Clear();
        CurrentFloorImageBitmap = null;
        MaskBrush = null;
        HasFogMask = false;
        CurrentFloorName = string.Empty;
    }

    /// <summary>Wholesale load for map.show's full resync (see MapShowParams.Lines) - replaces every line across all floors at once.</summary>
    public void ReplaceAllLines(IReadOnlyList<MapLineSnapshotDto> lines)
    {
        _lines.Clear();
        _lines.AddRange(lines);
        RefreshVisibleLines();
    }

    /// <summary>Adds a line, or fully replaces an existing one with the same Id (see RpcMethods.MapLineUpsert).</summary>
    public void UpsertLine(MapLineSnapshotDto line)
    {
        var index = _lines.FindIndex(l => l.Id == line.Id);
        if (index >= 0) _lines[index] = line;
        else _lines.Add(line);

        RefreshVisibleLines();
    }

    /// <summary>See RpcMethods.MapLineRemove's doc comment.</summary>
    public void RemoveLine(Guid lineId)
    {
        _lines.RemoveAll(l => l.Id == lineId);
        RefreshVisibleLines();
    }

    /// <summary>GM's "Erase all" for one floor - see RpcMethods.MapLineClearAll.</summary>
    public void ClearLinesForFloor(Guid floorId)
    {
        _lines.RemoveAll(l => l.FloorId == floorId);
        RefreshVisibleLines();
    }

    private void RefreshVisibleLines()
    {
        VisibleLines.Clear();
        if (CurrentFloorIndex < 0 || CurrentFloorIndex >= _floors.Count) return;

        var floorId = _floors[CurrentFloorIndex].FloorId;
        foreach (var line in _lines.Where(l => l.FloorId == floorId)) VisibleLines.Add(line);
    }

    /// <summary>Adds a token, or fully replaces an existing one with the same Id (see RpcMethods.MapTokenUpsert).</summary>
    public void UpsertToken(MapTokenSnapshotDto token)
    {
        var index = _tokens.FindIndex(t => t.Id == token.Id);
        var wasCurrentTurn = index >= 0 && _tokens[index].IsCurrentTurn;
        if (index >= 0) _tokens[index] = token;
        else _tokens.Add(token);

        RefreshVisibleTokens();

        if (AutoZoomEnabled && token.IsCurrentTurn && !wasCurrentTurn)
        {
            ShowFloorIfDifferent(token.FloorId);
            ActiveCharacterZoomRequested?.Invoke(token.X, token.Y);
        }
    }

    /// <summary>
    ///     Switches to the floor a token just became "current turn" on, if it isn't already the
    ///     one shown - a token's (X,Y) is only meaningful within its own floor's image space, so
    ///     the auto-zoom pan below only makes sense after this runs (see UpsertToken).
    /// </summary>
    private void ShowFloorIfDifferent(Guid floorId)
    {
        var index = _floors.FindIndex(f => f.FloorId == floorId);
        if (index < 0 || index == CurrentFloorIndex) return;

        CurrentFloorIndex = index;
        DisplayCurrentFloor();
    }

    public void RemoveToken(Guid tokenId)
    {
        _tokens.RemoveAll(t => t.Id == tokenId);
        RefreshVisibleTokens();
    }

    /// <summary>
    ///     Wholesale replace, for an owner (MapLiveWindow's own preview) that doesn't get live
    ///     per-token Upsert/Remove calls and instead just re-resolves the full set on every
    ///     relevant change (a token add/edit/delete/drag, or a fog reveal affecting a
    ///     HiddenUntilRevealed token's visibility). Doesn't raise ActiveCharacterZoomRequested -
    ///     that's a player-facing reaction to a token's turn *just* starting, not meaningful for a
    ///     full resync.
    /// </summary>
    public void ReplaceAllTokens(IReadOnlyList<MapTokenSnapshotDto> tokens)
    {
        _tokens.Clear();
        _tokens.AddRange(tokens);
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
        RefreshVisibleLines();
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