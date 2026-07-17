using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private PointOfInterestLibraryItemViewModel? _selectedPointOfInterest;
    // ==================== Points of Interest library (#67) ====================
    // Own top-level library, not nested under anything - see PointOfInterestLibraryItemViewModel's
    // doc comment for why it doesn't derive from LibraryItemViewModelBase<TSelf> like Media/Sound.
    // Came out of the map-tokens design round: a token needs something to link to for a
    // non-Character marker (a chest, a trap, a signpost).

    public ObservableCollection<PointOfInterestLibraryItemViewModel> PointOfInterestLibrary { get; } = [];

    public bool HasNoPointsOfInterest => PointOfInterestLibrary.Count == 0;

    [RelayCommand]
    private async Task AddPointOfInterestAsync()
    {
        var scope = await ResolveScopeForNewItemAsync();
        var poi = new PointOfInterestLibraryItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewPointOfInterestName"),
            RemovePointOfInterest, OnPointOfInterestLibraryItemChanged, scope);
        PointOfInterestLibrary.Add(poi);
        SelectedPointOfInterest = poi;
        OnPropertyChanged(nameof(HasNoPointsOfInterest));
        SavePointOfInterestLibrarySettings();
    }

    private void RemovePointOfInterest(PointOfInterestLibraryItemViewModel poi)
    {
        PointOfInterestLibrary.Remove(poi);
        if (SelectedPointOfInterest == poi) SelectedPointOfInterest = null;
        OnPropertyChanged(nameof(HasNoPointsOfInterest));
        SavePointOfInterestLibrarySettings();
    }

    private void OnPointOfInterestLibraryItemChanged(PointOfInterestLibraryItemViewModel poi)
    {
        SavePointOfInterestLibrarySettings();
    }

    /// <summary>See MoveNpcLibraryItemToScope's doc comment - same reasoning: no owned file to relocate.</summary>
    public void MovePointOfInterestLibraryItemToScope(PointOfInterestLibraryItemViewModel poi,
        LibraryScope targetScope)
    {
        MoveLibraryItemToScope(poi.Scope, targetScope, "PointOfInterest", poi.Name, () =>
        {
            poi.Scope = targetScope;
            SavePointOfInterestLibrarySettings();
        });
    }

    [RelayCommand]
    private void MovePointOfInterestLibraryItemToShared(PointOfInterestLibraryItemViewModel poi)
    {
        MovePointOfInterestLibraryItemToScope(poi, LibraryScope.Shared);
    }

    [RelayCommand]
    private void MovePointOfInterestLibraryItemToSession(PointOfInterestLibraryItemViewModel poi)
    {
        MovePointOfInterestLibraryItemToScope(poi, LibraryScope.SessionLocal);
    }

    /// <summary>
    ///     Who references a Media Library item by Id as a Point of Interest's icon - registered
    ///     into _usageRegistry so deleting that item goes through the same 3-way confirm-delete
    ///     flow as any other in-use item.
    /// </summary>
    private IEnumerable<string> FindPointOfInterestUsagesById(Guid id)
    {
        foreach (var poi in PointOfInterestLibrary)
            if (poi.IconImage?.Id == id)
                yield return poi.Name;
    }

    private void ClearPointOfInterestReferencesById(Guid id)
    {
        foreach (var poi in PointOfInterestLibrary)
            if (poi.IconImage?.Id == id)
                poi.IconImage = null;
    }

    private static PointOfInterestLibraryEntryDto ToPointOfInterestLibraryEntryDto(
        PointOfInterestLibraryItemViewModel poi)
    {
        return new PointOfInterestLibraryEntryDto
        {
            Id = poi.Id,
            Name = poi.Name,
            Description = poi.Description,
            PlayerInfo = poi.PlayerInfo,
            IconImageId = poi.IconImage?.Id,
            IconGlyph = poi.IconGlyph,
            PlayerVisibleName = poi.PlayerVisibleName,
            PlayerVisibleDescription = poi.PlayerVisibleDescription,
            PlayerVisiblePlayerInfo = poi.PlayerVisiblePlayerInfo,
            TagIds = poi.TagIds.ToList()
        };
    }

    /// <summary>
    ///     Wrapped in BeginBulkLoad/EndBulkLoad - see NpcLibraryItemViewModel's identical concern:
    ///     this entry isn't in the PointOfInterestLibrary collection yet while this method builds
    ///     it, so every property-set below would otherwise fire a save that serializes
    ///     PointOfInterestLibrary as it stood before this (and any later) entry was added.
    /// </summary>
    private PointOfInterestLibraryItemViewModel FromPointOfInterestLibraryEntryDto(
        PointOfInterestLibraryEntryDto entry, LibraryScope scope)
    {
        var poi = new PointOfInterestLibraryItemViewModel(entry.Id, entry.Name,
            RemovePointOfInterest, OnPointOfInterestLibraryItemChanged, scope, entry.TagIds);
        poi.BeginBulkLoad();
        try
        {
            poi.Description = entry.Description;
            poi.PlayerInfo = entry.PlayerInfo;
            poi.IconImage = entry.IconImageId is { } imageId
                ? MediaLibrary.FirstOrDefault(m => m.Id == imageId)
                : null;
            poi.IconGlyph = entry.IconGlyph;
            poi.PlayerVisibleName = entry.PlayerVisibleName;
            poi.PlayerVisibleDescription = entry.PlayerVisibleDescription;
            poi.PlayerVisiblePlayerInfo = entry.PlayerVisiblePlayerInfo;
            // Loaded from disk starts in preview - see IsPlayerInfoPreviewMode's doc comment
            // (matches NpcLibrary's identical convention for the same field).
            poi.IsPlayerInfoPreviewMode = !string.IsNullOrWhiteSpace(entry.PlayerInfo);
            return poi;
        }
        finally
        {
            poi.EndBulkLoad();
        }
    }

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    private void SavePointOfInterestLibrarySettings()
    {
        SaveLibrarySettings(PointOfInterestLibrary, p => p.Scope, ToPointOfInterestLibraryEntryDto,
            (settings, list) => settings.PointOfInterestLibrary = list,
            (sessionLibrary, list) => sessionLibrary.PointOfInterestLibrary = list);
    }
}
