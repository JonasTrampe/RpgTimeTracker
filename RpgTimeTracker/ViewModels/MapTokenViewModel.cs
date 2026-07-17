using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A marker placed on a Map (issue #31) - belongs to the Map, not a Floor (see FloorId,
///     mutable so reassigning it is how a token "teleports" between floors, e.g. taking stairs).
///     No starting/live split, unlike fog: one always-live token list, editable from both the
///     Prepare and Live windows.
///     Single source of truth for a linked token: Name/Portrait/Health (or Description) are read
///     live from the linked Character+Variant (NpcLibraryItemViewModel/NpcVariantViewModel) or
///     PointOfInterestLibraryItemViewModel entry by the editor UI/protocol layers (#32/#33), never
///     copied onto this class - LinkedId/LinkedVariantId are the only state a linked token carries
///     about what it points to. Name/Description/IconImage/IconGlyph below are only meaningful
///     when LinkKind is None (a freeform marker with no library entry behind it, e.g. a trap or
///     signpost that doesn't warrant its own Point of Interest).
///     IconImage references a Media Library entry by Id (not a raw file path) - deliberately
///     following the same reference convention as NpcVariantViewModel.TokenImage/
///     PointOfInterestLibraryItemViewModel.IconImage, rather than MapFloorItemViewModel's raw
///     ImagePath (a floor image is a bespoke per-map asset with no reason to be a shared library
///     item; a token's freeform icon benefits from the same reuse/dedup/usage-registry treatment
///     any other icon picker already gets).
/// </summary>
public sealed partial class MapTokenViewModel : ObservableObject
{
    private readonly Action<MapTokenViewModel> _onDeleteRequested;
    private readonly Action<MapTokenViewModel>? _onChanged;

    [ObservableProperty] private string _description = string.Empty;

    /// <summary>
    ///     Facing direction in degrees, 0 = up/north, clockwise (#70) - only meaningful/shown
    ///     for LinkKind == Character (a freeform marker or Point of Interest has no facing).
    ///     Adjusted in the editor via mouse wheel over the token marker, not drag.
    /// </summary>
    [ObservableProperty] private double _facingDegrees;

    [ObservableProperty] private Guid _floorId;
    [ObservableProperty] private MediaLibraryItemViewModel? _iconImage;
    [ObservableProperty] private string? _iconGlyph;
    [ObservableProperty] private Guid? _linkedId;
    [ObservableProperty] private Guid? _linkedVariantId;
    [ObservableProperty] private TokenLinkKind _linkKind;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _playerVisibleDetail;
    [ObservableProperty] private bool _playerVisibleName;
    [ObservableProperty] private bool _playerVisiblePlayerInfo;
    [ObservableProperty] private bool _playerVisiblePortrait;
    [ObservableProperty] private TokenRevealMode _revealMode = TokenRevealMode.AlwaysVisible;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public MapTokenViewModel(
        Guid id,
        Guid floorId,
        double x,
        double y,
        TokenLinkKind linkKind,
        Guid? linkedId,
        Action<MapTokenViewModel> onDeleteRequested,
        Action<MapTokenViewModel>? onChanged)
    {
        Id = id;
        _floorId = floorId;
        _x = x;
        _y = y;
        _linkKind = linkKind;
        _linkedId = linkedId;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public Guid Id { get; }

    /// <summary>
    ///     Whether this token should reach a connected PlayerClient right now - the
    ///     unit-testable piece of the reveal-mode logic called out in #31. cellRevealed is the
    ///     live fog-mask state of the grid cell the token currently occupies (see
    ///     FogMask/MainWindowViewModel.GetEffectiveFogStyle for the analogous per-cell check);
    ///     irrelevant for AlwaysVisible/GmOnly, only consulted for HiddenUntilRevealed.
    /// </summary>
    public bool IsVisibleToPlayers(bool cellRevealed)
    {
        return RevealMode switch
        {
            TokenRevealMode.AlwaysVisible => true,
            TokenRevealMode.GmOnly => false,
            TokenRevealMode.HiddenUntilRevealed => cellRevealed,
            _ => false
        };
    }

    partial void OnFloorIdChanged(Guid value)
    {
        NotifyChanged();
    }

    partial void OnXChanged(double value)
    {
        NotifyChanged();
    }

    partial void OnYChanged(double value)
    {
        NotifyChanged();
    }

    partial void OnNameChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnDescriptionChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnFacingDegreesChanged(double value)
    {
        NotifyChanged();
    }

    /// <summary>Mutually exclusive with IconGlyph - same convention as PointOfInterestLibraryItemViewModel.</summary>
    partial void OnIconImageChanged(MediaLibraryItemViewModel? value)
    {
        if (value is not null && IconGlyph is not null) IconGlyph = null;
        NotifyChanged();
    }

    partial void OnIconGlyphChanged(string? value)
    {
        if (value is not null && IconImage is not null) IconImage = null;
        NotifyChanged();
    }

    partial void OnRevealModeChanged(TokenRevealMode value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisibleNameChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisiblePortraitChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisibleDetailChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnPlayerVisiblePlayerInfoChanged(bool value)
    {
        NotifyChanged();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    private void NotifyChanged()
    {
        _onChanged?.Invoke(this);
    }
}
