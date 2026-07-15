using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named map (e.g. a house), made of one or more floors (MapFloorItemViewModel).
///     Deliberately does not derive from LibraryItemViewModelBase&lt;TSelf&gt; despite the
///     naming similarity to MediaLibraryItemViewModel/SoundLibraryItemViewModel - that
///     base class bakes in a single LocalPath/MimeType per item, which doesn't fit a
///     map (a folder of several floor images, not one file). Rename/delete are
///     re-implemented directly here instead, following the same shape.
/// </summary>
public sealed partial class MapItemViewModel : ObservableObject, ITaggable
{
    /// <summary>
    ///     Persisted format version for this map's data (settings entry + exported
    ///     .rtt-map/.rtt-session manifest) - not used for any migration yet (there's only ever
    ///     been version 1), but every map now carries it explicitly so a future format change can
    ///     detect an older map on load and apply an upgrade step instead of guessing or breaking.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    private readonly Action<MapItemViewModel>? _onChanged;

    private readonly Action<MapItemViewModel> _onDeleteRequested;

    /// <summary>
    ///     Default CellSizePx for new floors added to this map - see
    ///     ThemeSettingsService.MapLibraryEntryDto.DefaultCellSizePx.
    /// </summary>
    [ObservableProperty] private int _defaultCellSizePx;

    [ObservableProperty] private bool? _fogBlurEnabled;
    [ObservableProperty] private double? _fogBlurRadius;

    /// <summary>
    ///     Per-map fog render style override, falling back to the global setting when null -
    ///     see MainWindowViewModel.GetEffectiveFogStyle/ThemeSettingsService.MapLibraryEntryDto.
    /// </summary>
    [ObservableProperty] private string? _fogColorHex;

    [ObservableProperty] private int? _fogOpacityPercent;

    [ObservableProperty] private string _name;

    /// <summary>
    ///     Whether this map lives in the always-present Shared Library or inside the
    ///     currently open Session's own folder - see LibraryScope.
    /// </summary>
    [ObservableProperty] private LibraryScope _scope;

    public MapItemViewModel(
        Guid id,
        string name,
        int defaultCellSizePx,
        Action<MapItemViewModel> onDeleteRequested,
        Action<MapItemViewModel>? onChanged,
        string? fogColorHex = null,
        int? fogOpacityPercent = null,
        double? fogBlurRadius = null,
        bool? fogBlurEnabled = null,
        int formatVersion = CurrentFormatVersion,
        LibraryScope scope = LibraryScope.Shared,
        IEnumerable<Guid>? tagIds = null)
    {
        Id = id;
        _name = name;
        _defaultCellSizePx = defaultCellSizePx;
        _fogColorHex = fogColorHex;
        _fogOpacityPercent = fogOpacityPercent;
        _fogBlurRadius = fogBlurRadius;
        _fogBlurEnabled = fogBlurEnabled;
        FormatVersion = formatVersion;
        _scope = scope;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        Floors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFloors));
        Tokens.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoTokens));
            _onChanged?.Invoke(this);
        };
        if (tagIds is not null)
            foreach (var tagId in tagIds)
                TagIds.Add(tagId);
        TagIds.CollectionChanged += (_, _) => _onChanged?.Invoke(this);
    }

    public Guid Id { get; }

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    /// <summary>
    ///     The format version this map was loaded/imported at - not user-editable, and
    ///     always saved back as CurrentFormatVersion (see MainWindowViewModel.SaveMapLibrarySettings)
    ///     once any future migration step has brought it up to date in memory.
    /// </summary>
    public int FormatVersion { get; }

    public ObservableCollection<MapFloorItemViewModel> Floors { get; } = [];

    public bool HasNoFloors => Floors.Count == 0;

    /// <summary>
    ///     Tokens belong to the Map, not a Floor (see #31/MapTokenViewModel.FloorId) - one
    ///     always-live list, no starting/live split unlike fog.
    /// </summary>
    public ObservableCollection<MapTokenViewModel> Tokens { get; } = [];

    public bool HasNoTokens => Tokens.Count == 0;

    /// <summary>
    ///     Freeform Tag Ids attached to this map (see Tag) - separate from Scene
    ///     membership, a different, explicit mechanism.
    /// </summary>
    public ObservableCollection<Guid> TagIds { get; } = [];

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogColorHexChanged(string? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogOpacityPercentChanged(int? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogBlurRadiusChanged(double? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFogBlurEnabledChanged(bool? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnScopeChanged(LibraryScope value)
    {
        OnPropertyChanged(nameof(IsSessionLocal));
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    /// <summary>
    ///     Floors are layers of the same building/map, so their order is meaningful (unlike
    ///     the map library list itself) - the GM can reorder them via the floor list's up/down
    ///     buttons.
    /// </summary>
    public void MoveFloorUp(MapFloorItemViewModel floor)
    {
        var index = Floors.IndexOf(floor);
        if (index <= 0) return;

        Floors.Move(index, index - 1);
        _onChanged?.Invoke(this);
    }

    public void MoveFloorDown(MapFloorItemViewModel floor)
    {
        var index = Floors.IndexOf(floor);
        if (index < 0 || index >= Floors.Count - 1) return;

        Floors.Move(index, index + 1);
        _onChanged?.Invoke(this);
    }

    /// <summary>
    ///     Adds a new token, or - for a Character/PointOfInterest link - moves the existing
    ///     token already linked to that same entry instead of creating a duplicate (see #31:
    ///     "one token per linked Character/PointOfInterest per map"). Freeform tokens
    ///     (linkKind == None) are never deduplicated this way, since linkedId is null for all of
    ///     them - every call for a freeform token creates a new one.
    /// </summary>
    public MapTokenViewModel AddOrSelectToken(TokenLinkKind linkKind, Guid? linkedId, Guid floorId, double x,
        double y, Action<MapTokenViewModel> onDeleteRequested, Action<MapTokenViewModel>? onChanged)
    {
        if (linkKind != TokenLinkKind.None && linkedId is { } id)
        {
            var existing = Tokens.FirstOrDefault(t => t.LinkKind == linkKind && t.LinkedId == id);
            if (existing is not null)
            {
                existing.FloorId = floorId;
                existing.X = x;
                existing.Y = y;
                return existing;
            }
        }

        var token = new MapTokenViewModel(Guid.NewGuid(), floorId, x, y, linkKind, linkedId,
            onDeleteRequested, onChanged);
        Tokens.Add(token);
        return token;
    }

    public void RemoveToken(MapTokenViewModel token)
    {
        Tokens.Remove(token);
    }
}