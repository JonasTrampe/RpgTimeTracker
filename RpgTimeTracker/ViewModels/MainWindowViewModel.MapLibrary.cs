using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using Persistence = RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    /// <summary>
    ///     Default grid cell size for a newly added floor - configurable per floor later
    ///     (e.g. via the SL Map Editor Window in a later milestone), not yet exposed in this UI.
    /// </summary>
    private const int DefaultMapCellSizePx = 8;

    private readonly Dictionary<Guid, FogMask> _liveFog = new();

    /// <summary>
    ///     Maps that currently have at least one MapPrepareWindow open (see
    ///     NotifyMapPrepareWindowOpened/Closed) - while a map is being prepared, it must not be
    ///     streamable (see IsSelectedMapBeingPrepared/OpenMapToPlayersAsync), so editing the
    ///     prepare mask (including a CellSizePx rescale) can never coincide with an active
    ///     broadcast of that same mask.
    /// </summary>
    private readonly HashSet<Guid> _mapsBeingPrepared = [];

    /// <summary>
    ///     In-memory cache for MapPrepareWindow, mirroring _liveFog exactly but seeded and
    ///     saved independently - both lazily read the same on-disk floor.FogPath the first time a
    ///     floor is touched, but afterward diverge: this one is only ever written back to
    ///     floor.FogPath (see SavePrepareFogAsync), never broadcast or shown locally, while
    ///     _liveFog is only ever changed via MapLiveWindow's direct edits or an explicit
    ///     Reset-to-Prepared (see ResetFloorFogToStartingAsync). Editing/saving Prepare does NOT
    ///     auto-update an already-cached Live instance - that's intentional, not a bug to "fix".
    /// </summary>
    private readonly Dictionary<Guid, FogMask> _prepareFog = new();

    [ObservableProperty] private MapFloorItemViewModel? _editingFloor;
    [ObservableProperty] private bool _isMapOpenToPlayers;

    // ==================== Map display (open to players, live fog editing) ====================
    // "Starting" fog lives on disk per floor (MapFloorItemViewModel.FogPath, the library
    // template); "current/live" fog is session-only, kept here in memory (not written back to
    // FogPath) and initialized from the starting fog the first time a floor is touched.

    [ObservableProperty] private MapItemViewModel? _openMap;

    [ObservableProperty] private MapItemViewModel? _selectedMap;

    private MapItemViewModel? _selectedMapForFloorsWatch;
    // ==================== Map library (fog-of-war maps, multiple floors each) ====================

    public ObservableCollection<MapItemViewModel> MapLibrary { get; } = [];

    public bool HasNoMaps => MapLibrary.Count == 0;

    /// <summary>
    ///     Whether SelectedMap specifically (not just any map) is currently open to players -
    ///     drives the "Shown to players" indicator next to the Maps tab's "Show" button.
    /// </summary>
    public bool IsSelectedMapOpenToPlayers => IsMapOpenToPlayers && ReferenceEquals(OpenMap, SelectedMap);

    public bool IsSelectedMapBeingPrepared => SelectedMap is not null && _mapsBeingPrepared.Contains(SelectedMap.Id);

    /// <summary>
    ///     Whether the Maps tab's "Show" button should be enabled for SelectedMap - has
    ///     floors, and isn't currently being prepared (see IsSelectedMapBeingPrepared).
    /// </summary>
    public bool CanShowSelectedMap => SelectedMap is { HasNoFloors: false } && !IsSelectedMapBeingPrepared;

    /// <summary>
    ///     Shared "what a player currently sees" display, reused for the local player-window
    ///     preview (PlayerWindow.axaml, see ShouldShowMapLocally) - the exact same component the
    ///     real PlayerClient uses (RpgTimeTracker.Shared.ViewModels.MapDisplayViewModel). Since
    ///     both this and GetLiveFog live in the same process, the Host feeds it the SAME FogMask
    ///     instances it paints into (see MapEditorWindow) rather than a serialized copy, and only
    ///     needs to say "something changed" (NotifyFogChanged) instead of resending cell data.
    /// </summary>
    public MapDisplayViewModel MapDisplay { get; } = new();

    /// <summary>
    ///     The local player window is open - shown regardless of connected clients, same
    ///     as ShouldShowMediaLocally.
    /// </summary>
    public bool ShouldShowMapLocally => MapDisplay.IsShowingMap && IsPlayerWindowOpen;

    public bool HasNoFloorsInEditor => EditingFloor is null;

    [RelayCommand]
    private async Task AddMapAsync()
    {
        var scope = await ResolveScopeForNewItemAsync();
        var map = new MapItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewMapName"), DefaultMapCellSizePx, RemoveMap,
            OnMapLibraryItemChanged, scope: scope);
        MapLibrary.Add(map);
        SelectedMap = map;
        SaveMapLibrarySettings();
    }

    /// <summary>
    ///     Root folder a map's own subfolder (named by its Id) lives under - the Shared
    ///     AppData directory, or the open session's own maps/ folder for a SessionLocal map.
    /// </summary>
    private string GetMapLibraryBaseDirectory(LibraryScope scope)
    {
        return scope == LibraryScope.SessionLocal
            ? _sessionService.MapDirectory
            : ThemeSettingsService.MapLibraryDirectory;
    }

    /// <summary>See GetMapLibraryBaseDirectory's doc comment - same purpose for Media.</summary>
    private string GetMediaLibraryBaseDirectory(LibraryScope scope)
    {
        return scope == LibraryScope.SessionLocal
            ? _sessionService.MediaDirectory
            : ThemeSettingsService.MediaLibraryDirectory;
    }

    /// <summary>See GetMapLibraryBaseDirectory's doc comment - same purpose for Sound.</summary>
    private string GetSoundLibraryBaseDirectory(LibraryScope scope)
    {
        return scope == LibraryScope.SessionLocal
            ? _sessionService.SoundDirectory
            : ThemeSettingsService.SoundLibraryDirectory;
    }

    /// <summary>See GetMapLibraryBaseDirectory's doc comment - same purpose for Music.</summary>
    private string GetMusicLibraryBaseDirectory(LibraryScope scope)
    {
        return scope == LibraryScope.SessionLocal
            ? _sessionService.MusicDirectory
            : ThemeSettingsService.MusicLibraryDirectory;
    }

    private void RemoveMap(MapItemViewModel map)
    {
        MapLibrary.Remove(map);
        if (SelectedMap == map) SelectedMap = null;

        try
        {
            var mapDirectory = Path.Combine(GetMapLibraryBaseDirectory(map.Scope), map.Id.ToString("N"));
            if (Directory.Exists(mapDirectory)) Directory.Delete(mapDirectory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not delete map directory for {MapName} ({MapId})", map.Name, map.Id);
        }

        SaveMapLibrarySettings();
    }

    /// <summary>
    ///     See MoveMediaLibraryItemToScope's doc comment - same move/flip-Scope/re-save
    ///     pattern, except a map moves its whole per-map directory (all floors' image+fog files)
    ///     in one step instead of a single file, since that Id-named folder is already the
    ///     natural on-disk unit here (see GetMapLibraryBaseDirectory).
    /// </summary>
    public void MoveMapLibraryItemToScope(MapItemViewModel map, LibraryScope targetScope)
    {
        MoveLibraryItemToScope(map.Scope, targetScope, "Map", map.Name, () =>
        {
            var targetBaseDirectory = GetMapLibraryBaseDirectory(targetScope);
            Directory.CreateDirectory(targetBaseDirectory);
            var sourceMapDirectory = Path.Combine(GetMapLibraryBaseDirectory(map.Scope), map.Id.ToString("N"));
            var targetMapDirectory = Path.Combine(targetBaseDirectory, map.Id.ToString("N"));
            if (Directory.Exists(sourceMapDirectory)) Directory.Move(sourceMapDirectory, targetMapDirectory);

            foreach (var floor in map.Floors)
                floor.UpdatePaths(
                    Path.Combine(targetMapDirectory, Path.GetFileName(floor.ImagePath)),
                    Path.Combine(targetMapDirectory, Path.GetFileName(floor.FogPath)));

            map.Scope = targetScope;
            SaveMapLibrarySettings();
        });
    }

    /// <summary>See MoveMediaLibraryItemToShared/ToSession's doc comment - same wrapping for Maps.</summary>
    [RelayCommand]
    private void MoveMapLibraryItemToShared(MapItemViewModel map)
    {
        MoveMapLibraryItemToScope(map, LibraryScope.Shared);
    }

    [RelayCommand]
    private void MoveMapLibraryItemToSession(MapItemViewModel map)
    {
        MoveMapLibraryItemToScope(map, LibraryScope.SessionLocal);
    }

    public async Task AddFloorToMapAsync(MapItemViewModel map, string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out _) || kind != MediaKind.Image)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMapFloorFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var mapDirectory = Path.Combine(GetMapLibraryBaseDirectory(map.Scope), map.Id.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            var floorId = Guid.NewGuid();
            var imagePath = Path.Combine(mapDirectory, $"{floorId:N}{Path.GetExtension(sourcePath)}");
            await using (var source = File.OpenRead(sourcePath))
            await using (var target = File.Create(imagePath))
            {
                await source.CopyToAsync(target);
            }

            var cellSizePx = map.DefaultCellSizePx;
            int gridWidth, gridHeight;
            await using (var dimensionStream = File.OpenRead(imagePath))
            {
                var bitmap = new Bitmap(dimensionStream);
                gridWidth = Math.Max(1, (bitmap.PixelSize.Width + cellSizePx - 1) / cellSizePx);
                gridHeight = Math.Max(1, (bitmap.PixelSize.Height + cellSizePx - 1) / cellSizePx);
            }

            var fogPath = Path.Combine(mapDirectory, $"{floorId:N}.fog");
            var startingFog = FogMask.CreateFullyHidden(gridWidth, gridHeight, cellSizePx);
            await File.WriteAllBytesAsync(fogPath, FogMaskSerializer.Serialize(startingFog));

            var floor = new MapFloorItemViewModel(
                floorId, Path.GetFileNameWithoutExtension(sourcePath), imagePath, fogPath,
                cellSizePx, gridWidth, gridHeight, LoadMapFloorThumbnail(imagePath),
                f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings());
            map.Floors.Add(floor);
            SaveMapLibrarySettings();
            Log.Information("Floor added to map {MapName}: {FloorName} ({GridWidth}x{GridHeight} cells)",
                map.Name, floor.Name, gridWidth, gridHeight);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedFloorToMap"),
                floor.Name, map.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Floor could not be added to map {MapName} ({SourcePath})", map.Name, sourcePath);
            MediaErrorMessage =
                string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeAddedToLibrary"),
                    ex.Message);
        }
    }

    private void RemoveFloorFromMap(MapItemViewModel map, MapFloorItemViewModel floor)
    {
        map.Floors.Remove(floor);

        try
        {
            if (File.Exists(floor.ImagePath)) File.Delete(floor.ImagePath);
            if (File.Exists(floor.FogPath)) File.Delete(floor.FogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not delete floor files for {FloorName} ({FloorId})", floor.Name, floor.Id);
        }

        SaveMapLibrarySettings();
    }

    private static Bitmap? LoadMapFloorThumbnail(string imagePath)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            return Bitmap.DecodeToWidth(stream, 96);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning(ex, "Map floor thumbnail could not be loaded ({ImagePath})", imagePath);
            return null;
        }
    }

    /// <summary>
    ///     Shared onChanged callback for MapItemViewModel (name rename, fog-style override
    ///     change) - persists the library and, if this map is currently open to players, also
    ///     re-applies/re-broadcasts its (possibly just-changed) effective fog style live.
    /// </summary>
    private void OnMapLibraryItemChanged(MapItemViewModel map)
    {
        SaveMapLibrarySettings();
        if (ReferenceEquals(map, OpenMap)) ApplyAndBroadcastEffectiveFogStyleForOpenMap();
    }

    private static MapLibraryEntryDto ToMapLibraryEntryDto(MapItemViewModel map)
    {
        return new MapLibraryEntryDto
        {
            Id = map.Id,
            Name = map.Name,
            FogColorHex = map.FogColorHex,
            FogOpacityPercent = map.FogOpacityPercent,
            FogBlurRadius = map.FogBlurRadius,
            FogBlurEnabled = map.FogBlurEnabled,
            DefaultCellSizePx = map.DefaultCellSizePx,
            FormatVersion = map.FormatVersion,
            TagIds = map.TagIds.ToList(),
            Tokens = map.Tokens.Select(ToMapTokenDto).ToList(),
            InitiativeEntries = map.InitiativeEntries.Select(ToInitiativeEntryDto).ToList(),
            InitiativeCurrentIndex = map.InitiativeCurrentIndex,
            InitiativeIsRunning = map.InitiativeIsRunning,
            InitiativeRound = map.InitiativeRound,
            Floors = map.Floors.Select((floor, index) => new MapFloorEntryDto
            {
                Id = floor.Id,
                Name = floor.Name,
                ImagePath = floor.ImagePath,
                FogPath = floor.FogPath,
                CellSizePx = floor.CellSizePx,
                GridWidth = floor.GridWidth,
                GridHeight = floor.GridHeight,
                Order = index,
                // Only Permanent lines are ever written to disk - SemiPermanent ones expire on
                // their own timer (see ExpireSemiPermanentLineAsync) and would be meaningless to
                // restore with no remaining time left after an app restart.
                Lines = floor.Lines.Where(l => l.Durability == Persistence.MapLineDurability.Permanent).ToList()
            }).ToList()
        };
    }

    private static MapTokenDto ToMapTokenDto(MapTokenViewModel token)
    {
        return new MapTokenDto
        {
            Id = token.Id,
            FloorId = token.FloorId,
            X = token.X,
            Y = token.Y,
            LinkKind = token.LinkKind,
            LinkedId = token.LinkedId,
            LinkedVariantId = token.LinkedVariantId,
            Name = token.Name,
            Description = token.Description,
            IconImageId = token.IconImage?.Id,
            IconGlyph = token.IconGlyph,
            RevealMode = token.RevealMode,
            PlayerVisibleName = token.PlayerVisibleName,
            PlayerVisiblePortrait = token.PlayerVisiblePortrait,
            PlayerVisibleDetail = token.PlayerVisibleDetail,
            FacingDegrees = token.FacingDegrees
        };
    }

    private static InitiativeEntryDto ToInitiativeEntryDto(InitiativeEntryViewModel entry)
    {
        return new InitiativeEntryDto
        {
            Id = entry.Id,
            LinkKind = entry.LinkKind,
            LinkedId = entry.LinkedId,
            LinkedVariantId = entry.LinkedVariantId,
            FreeformName = entry.FreeformName
        };
    }

    /// <summary>
    ///     Reconstructs a Map's Tokens from their saved DTOs - called after a map's Floors are
    ///     populated (so FloorId references already resolve to something meaningful) but this has
    ///     no actual dependency on Floors being present; separated out only because every map
    ///     construction call site (Shared settings load, session load, full-session/session-only
    ///     import) needs the exact same reconstruction.
    /// </summary>
    private void LoadTokensIntoMap(MapItemViewModel map, IEnumerable<MapTokenDto> tokenDtos)
    {
        foreach (var dto in tokenDtos)
        {
            var token = new MapTokenViewModel(dto.Id, dto.FloorId, dto.X, dto.Y, dto.LinkKind, dto.LinkedId,
                map.RemoveToken, _ => SaveMapLibrarySettings())
            {
                LinkedVariantId = dto.LinkedVariantId,
                Name = dto.Name,
                Description = dto.Description,
                IconImage = dto.IconImageId is { } imageId ? MediaLibrary.FirstOrDefault(m => m.Id == imageId) : null,
                IconGlyph = dto.IconGlyph,
                RevealMode = dto.RevealMode,
                PlayerVisibleName = dto.PlayerVisibleName,
                PlayerVisiblePortrait = dto.PlayerVisiblePortrait,
                PlayerVisibleDetail = dto.PlayerVisibleDetail,
                FacingDegrees = dto.FacingDegrees
            };
            map.Tokens.Add(token);
        }
    }

    /// <summary>
    ///     Reconstructs a Map's initiative order (#70) from its saved entries - same "every
    ///     construction call site needs the exact same reconstruction" reasoning as
    ///     LoadTokensIntoMap, called alongside it.
    /// </summary>
    private void LoadInitiativeIntoMap(MapItemViewModel map, MapLibraryEntryDto dto)
    {
        foreach (var entryDto in dto.InitiativeEntries)
        {
            var entry = new InitiativeEntryViewModel(entryDto.Id, entryDto.LinkKind, entryDto.LinkedId,
                entryDto.LinkedVariantId, entryDto.FreeformName, map.RemoveInitiativeEntry,
                _ => SaveMapLibrarySettings());
            map.InitiativeEntries.Add(entry);
        }

        map.InitiativeCurrentIndex = dto.InitiativeCurrentIndex;
        map.InitiativeIsRunning = dto.InitiativeIsRunning;
        map.InitiativeRound = dto.InitiativeRound;
    }

    /// <summary>
    ///     Resolves a token's display Name - read live from the linked Character/PointOfInterest
    ///     entry, never copied onto the token itself (see MapTokenViewModel's doc comment). Falls
    ///     back to the token's own Name field if the link no longer resolves (the linked entry was
    ///     deleted without the token itself being cleaned up yet) or for a freeform token.
    /// </summary>
    public string ResolveTokenName(MapTokenViewModel token)
    {
        return token.LinkKind switch
        {
            TokenLinkKind.Character => NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId)?.Name ?? token.Name,
            TokenLinkKind.PointOfInterest => PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)
                ?.Name ?? token.Name,
            _ => token.Name
        };
    }

    /// <summary>Health (Character) or Description (PointOfInterest/freeform) - see #31's "Detail" field.</summary>
    public string? ResolveTokenDetail(MapTokenViewModel token)
    {
        switch (token.LinkKind)
        {
            case TokenLinkKind.Character:
                var npc = NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId);
                if (npc is null) return null;
                var variant = npc.Variants.FirstOrDefault(v => v.Id == token.LinkedVariantId) ?? npc.DefaultVariant;
                return variant.Health;
            case TokenLinkKind.PointOfInterest:
                return PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)?.Description;
            default:
                return token.Description;
        }
    }

    /// <summary>
    ///     The linked entity's player-facing Markdown bio - a Character's PlayerInfo
    ///     (NpcVariantViewModel.PlayerInfo, via GetEffectivePlayerInfo's Default-variant fallback)
    ///     or a Point of Interest's own PlayerInfo field; null for freeform tokens, which have no
    ///     such field.
    /// </summary>
    public string? ResolvePlayerInfo(MapTokenViewModel token)
    {
        switch (token.LinkKind)
        {
            case TokenLinkKind.Character:
                var npc = NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId);
                if (npc is null) return null;
                var variant = npc.Variants.FirstOrDefault(v => v.Id == token.LinkedVariantId) ?? npc.DefaultVariant;
                return npc.GetEffectivePlayerInfo(variant);
            case TokenLinkKind.PointOfInterest:
                return PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)?.PlayerInfo;
            default:
                return null;
        }
    }

    public MediaLibraryItemViewModel? ResolveTokenPortrait(MapTokenViewModel token)
    {
        switch (token.LinkKind)
        {
            case TokenLinkKind.Character:
                var npc = NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId);
                if (npc is null) return null;
                var variant = npc.Variants.FirstOrDefault(v => v.Id == token.LinkedVariantId) ?? npc.DefaultVariant;
                return npc.GetEffectiveTokenImage(variant);
            case TokenLinkKind.PointOfInterest:
                return PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)?.IconImage;
            default:
                return token.IconImage;
        }
    }

    public string? ResolveTokenIconGlyph(MapTokenViewModel token)
    {
        switch (token.LinkKind)
        {
            case TokenLinkKind.Character:
                var npc = NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId);
                if (npc is null) return null;
                var variant = npc.Variants.FirstOrDefault(v => v.Id == token.LinkedVariantId) ?? npc.DefaultVariant;
                return npc.GetEffectiveTokenIcon(variant);
            case TokenLinkKind.PointOfInterest:
                return PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)?.IconGlyph;
            default:
                return token.IconGlyph;
        }
    }

    /// <summary>Last-resort fallback (no portrait, no icon) - up to the first two words of the resolved Name.</summary>
    public string ResolveTokenInitials(MapTokenViewModel token)
    {
        var name = ResolveTokenName(token);
        return string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Select(word => char.ToUpperInvariant(word[0])));
    }

    /// <summary>
    ///     Whether the grid cell a token currently occupies is revealed on the map's live fog -
    ///     the "cellRevealed" input to MapTokenViewModel.IsVisibleToPlayers. False (never visible)
    ///     if the token's FloorId no longer resolves to one of the map's floors, or its position
    ///     falls outside the grid.
    /// </summary>
    private bool IsTokenCellRevealed(MapItemViewModel map, MapTokenViewModel token)
    {
        var floor = map.Floors.FirstOrDefault(f => f.Id == token.FloorId);
        if (floor is null) return false;

        if (token.X < 0 || token.Y < 0) return false;

        var cellX = (int)(token.X / floor.CellSizePx);
        var cellY = (int)(token.Y / floor.CellSizePx);
        if (cellX < 0 || cellY < 0 || cellX >= floor.GridWidth || cellY >= floor.GridHeight) return false;

        return GetLiveFog(floor).IsRevealed(cellX, cellY);
    }

    /// <summary>
    ///     Builds the wire snapshot for one token - Name/Detail/IconGlyph are only populated when
    ///     that field's player-visible toggle is on, so the client never receives data it isn't
    ///     allowed to show (see MapTokenSnapshotDto's doc comment). Does not check
    ///     IsVisibleToPlayers itself - callers decide whether this token should be sent/kept at all
    ///     (see NotifyTokenChanged/BuildVisibleTokenSnapshots).
    /// </summary>
    private MapTokenSnapshotDto ToTokenSnapshotDto(MapItemViewModel map, MapTokenViewModel token)
    {
        return new MapTokenSnapshotDto
        {
            Id = token.Id,
            FloorId = token.FloorId,
            X = token.X,
            Y = token.Y,
            Name = token.PlayerVisibleName ? ResolveTokenName(token) : null,
            Detail = token.PlayerVisibleDetail ? ResolveTokenDetail(token) : null,
            IconGlyph = token.PlayerVisiblePortrait ? ResolveTokenIconGlyph(token) : null,
            PlayerInfo = token.PlayerVisiblePlayerInfo ? ResolvePlayerInfo(token) : null,
            IsCurrentTurn = token.Id == GetCurrentTurnTokenId(map),
            FacingDegrees = token.LinkKind == TokenLinkKind.Character ? token.FacingDegrees : null
        };
    }

    /// <summary>
    ///     Resolves the map token whose turn it currently is (#70) - null unless the initiative
    ///     tracker is running, currently pointing at a Character-linked entry, and that Character
    ///     has a token actually placed on this map. Recomputed on demand rather than cached, so
    ///     every snapshot build (a token edit, a fog reveal, a full map.show resync) always
    ///     reflects the tracker's current state without a separate field to keep in sync.
    /// </summary>
    private Guid? GetCurrentTurnTokenId(MapItemViewModel map)
    {
        if (!map.InitiativeIsRunning) return null;
        if (map.InitiativeCurrentIndex < 0 || map.InitiativeCurrentIndex >= map.InitiativeEntries.Count) return null;

        var entry = map.InitiativeEntries[map.InitiativeCurrentIndex];
        if (entry.LinkKind != TokenLinkKind.Character || entry.LinkedId is not { } linkedId) return null;

        return map.Tokens.FirstOrDefault(t => t.LinkKind == TokenLinkKind.Character && t.LinkedId == linkedId)?.Id;
    }

    /// <summary>Every token on this map currently visible to players - the full-resync payload for map.show.</summary>
    private List<MapTokenSnapshotDto> BuildVisibleTokenSnapshots(MapItemViewModel map)
    {
        return map.Tokens.Where(t => t.IsVisibleToPlayers(IsTokenCellRevealed(map, t)))
            .Select(t => ToTokenSnapshotDto(map, t))
            .ToList();
    }

    /// <summary>
    ///     Public wrapper around BuildVisibleTokenSnapshots for MapLiveWindow's own "Vorschau"
    ///     thumbnail (a separate MapDisplayViewModel instance from MapDisplay that isn't kept in
    ///     sync by RefreshTokenPlayerState, since that only targets whichever map is currently open
    ///     to players - the preview needs the same resolved/filtered snapshots for whatever map its
    ///     own window is editing, open-to-players or not).
    /// </summary>
    public IReadOnlyList<MapTokenSnapshotDto> GetVisibleTokenSnapshots(MapItemViewModel map)
    {
        return BuildVisibleTokenSnapshots(map);
    }

    /// <summary>How long a SemiPermanent line stays up before automatically erasing itself (#Tier1 grilled design).</summary>
    private static readonly TimeSpan SemiPermanentLineLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Every SemiPermanent/Permanent line on this map not withheld by HiddenUntilRevealed - the full-resync payload for map.show.</summary>
    private static List<MapLineSnapshotDto> BuildVisibleLineSnapshots(MapItemViewModel map)
    {
        return map.Floors
            .SelectMany(floor => floor.Lines.Where(l => !l.HiddenUntilRevealed), (floor, line) => ToLineSnapshotDto(floor, line))
            .ToList();
    }

    private static MapLineSnapshotDto ToLineSnapshotDto(MapFloorItemViewModel floor, Persistence.MapLineDto line)
    {
        return new MapLineSnapshotDto
        {
            Id = line.Id,
            FloorId = floor.Id,
            Points = line.Points.Select(p => new MapLinePoint { X = p.X, Y = p.Y }).ToList(),
            ColorHex = line.ColorHex,
            Durability = (RpgTimeTracker.Shared.Models.Rpc.MapLineDurability)(int)line.Durability,
            Thickness = line.Thickness,
            OwnerClientId = line.OwnerClientId
        };
    }

    /// <summary>
    ///     GM draws a SemiPermanent/Permanent line (see MapEditCanvasControl's Draw tool) - adds it
    ///     to the floor, persists (Permanent only - see ToMapLibraryEntryDto), and broadcasts it live
    ///     if this map is currently open to players and the line isn't withheld by
    ///     HiddenUntilRevealed. A SemiPermanent line also gets a one-shot timer that removes itself
    ///     once SemiPermanentLineLifetime elapses.
    /// </summary>
    public void AddMapLine(MapItemViewModel map, MapFloorItemViewModel floor, Persistence.MapLineDto line)
    {
        floor.Lines.Add(line);
        SaveMapLibrarySettings();

        if (ReferenceEquals(OpenMap, map))
        {
            var snapshot = ToLineSnapshotDto(floor, line);
            MapDisplay.UpsertLine(snapshot);
            if (!line.HiddenUntilRevealed) _ = _playerServer.PublishMapLineUpsertAsync(snapshot);
        }

        if (line.Durability == Persistence.MapLineDurability.SemiPermanent)
            _ = ExpireSemiPermanentLineAsync(map, floor, line.Id);
    }

    /// <summary>Public wrapper around ToLineSnapshotDto for MapLiveWindow's own "Vorschau" thumbnail, mirroring GetVisibleTokenSnapshots.</summary>
    public RpgTimeTracker.Shared.Models.Rpc.MapLineSnapshotDto BuildLineSnapshot(MapFloorItemViewModel floor, Persistence.MapLineDto line)
    {
        return ToLineSnapshotDto(floor, line);
    }

    private async Task ExpireSemiPermanentLineAsync(MapItemViewModel map, MapFloorItemViewModel floor, Guid lineId)
    {
        await Task.Delay(SemiPermanentLineLifetime);
        RemoveMapLine(map, floor, lineId);
    }

    /// <summary>
    ///     GM's Erase tool trimmed a line down to fewer points instead of removing it outright (see
    ///     MapEditCanvasControl.LinePointsErased) - the line is already sitting in floor.Lines
    ///     (mutated in place), so this just re-persists and re-sends it as a full upsert by Id,
    ///     the same wire shape a newly drawn line uses (see AddMapLine).
    /// </summary>
    public void UpdateMapLine(MapItemViewModel map, MapFloorItemViewModel floor, Persistence.MapLineDto line)
    {
        SaveMapLibrarySettings();

        if (!ReferenceEquals(OpenMap, map)) return;

        var snapshot = ToLineSnapshotDto(floor, line);
        MapDisplay.UpsertLine(snapshot);
        if (!line.HiddenUntilRevealed) _ = _playerServer.PublishMapLineUpsertAsync(snapshot);
    }

    /// <summary>GM's Erase tool removed one specific line (own or, for the GM, anyone's).</summary>
    public void RemoveMapLine(MapItemViewModel map, MapFloorItemViewModel floor, Guid lineId)
    {
        var removed = floor.Lines.FirstOrDefault(l => l.Id == lineId);
        if (removed is null) return;

        floor.Lines.Remove(removed);
        SaveMapLibrarySettings();

        if (ReferenceEquals(OpenMap, map))
        {
            MapDisplay.RemoveLine(lineId);
            _ = _playerServer.PublishMapLineRemoveAsync(floor.Id, lineId);
        }
    }

    /// <summary>GM's "Erase all" - clears every line on this one floor at once.</summary>
    public void ClearMapLines(MapItemViewModel map, MapFloorItemViewModel floor)
    {
        if (floor.Lines.Count == 0) return;

        floor.Lines.Clear();
        SaveMapLibrarySettings();

        if (ReferenceEquals(OpenMap, map))
        {
            MapDisplay.ClearLinesForFloor(floor.Id);
            _ = _playerServer.PublishMapLineClearAllAsync(floor.Id);
        }
    }

    /// <summary>
    ///     Call after any edit to a token's data (link, name/description, icon, Reveal mode, a
    ///     player-visibility toggle, or moving it to a different floor) - saves, and if this token's
    ///     map is the one currently open to players, re-sends its full resolved snapshot (or removes
    ///     it, if it's no longer visible) to both the network and the Host's own local preview.
    /// </summary>
    public void NotifyTokenChanged(MapItemViewModel map, MapTokenViewModel token)
    {
        OnMapLibraryItemChanged(map);

        RefreshTokenPlayerState(map, token);
    }

    private void RefreshTokenPlayerState(MapItemViewModel map, MapTokenViewModel token)
    {
        if (!IsMapOpenToPlayers || !ReferenceEquals(OpenMap, map)) return;

        if (token.IsVisibleToPlayers(IsTokenCellRevealed(map, token)))
        {
            var snapshot = ToTokenSnapshotDto(map, token);
            _ = _playerServer.PublishMapTokenUpsertAsync(snapshot);
            MapDisplay.UpsertToken(snapshot);
        }
        else
        {
            _ = _playerServer.PublishMapTokenRemoveAsync(token.Id);
            MapDisplay.RemoveToken(token.Id);
        }
    }

    /// <summary>
    ///     Call after dragging a token to a new position (same or different floor) - same
    ///     resolve-and-resend behavior as NotifyTokenChanged (see RpcMethods.MapTokenUpsert's doc
    ///     comment for why a position change is just another full upsert, not a lighter "move").
    /// </summary>
    public void NotifyTokenMoved(MapItemViewModel map, MapTokenViewModel token)
    {
        NotifyTokenChanged(map, token);
    }

    /// <summary>Call after removing a token (map.RemoveToken already took it out of Tokens) - saves and broadcasts.</summary>
    public void NotifyTokenRemoved(MapItemViewModel map, Guid tokenId)
    {
        OnMapLibraryItemChanged(map);
        if (!IsMapOpenToPlayers || !ReferenceEquals(OpenMap, map)) return;

        _ = _playerServer.PublishMapTokenRemoveAsync(tokenId);
        MapDisplay.RemoveToken(tokenId);
    }

    /// <summary>
    ///     Called after a fog paint stroke (see MapLiveWindow.OnCellsPainted) - a
    ///     HiddenUntilRevealed token whose cell was just painted may have just become visible (or
    ///     hidden again), so its network/preview state needs re-evaluating even though nothing
    ///     about the token itself changed. Cheap: only tokens on the painted floor are checked, and
    ///     only those actually sitting in one of the just-painted cells are re-sent.
    /// </summary>
    public void NotifyTokensAffectedByFogChange(MapItemViewModel map, MapFloorItemViewModel floor,
        IReadOnlyList<FogCellDto> cells)
    {
        if (!ReferenceEquals(OpenMap, map)) return;

        var paintedCells = cells.Select(c => (c.X, c.Y)).ToHashSet();
        foreach (var token in map.Tokens)
        {
            if (token.FloorId != floor.Id || token.RevealMode != TokenRevealMode.HiddenUntilRevealed) continue;

            var cellX = (int)(token.X / floor.CellSizePx);
            var cellY = (int)(token.Y / floor.CellSizePx);
            if (paintedCells.Contains((cellX, cellY))) RefreshTokenPlayerState(map, token);
        }
    }

    // ==================== Initiative tracker (#70) ====================

    /// <summary>
    ///     Call after any edit to an initiative entry (add, remove, reorder) - the tracker itself
    ///     has no network presence (only its effect, IsCurrentTurn, does - see
    ///     GetCurrentTurnTokenId), so this is just a save, matching InitiativeEntryViewModel's own
    ///     onChanged callback shape.
    /// </summary>
    public void OnInitiativeEntryChanged(MapItemViewModel map)
    {
        OnMapLibraryItemChanged(map);
    }

    /// <summary>
    ///     Whether the clock was running before Freeze mode paused it for the currently-running
    ///     tracker, so StopInitiative only resumes it if it was actually running before - only
    ///     meaningful while some map's InitiativeIsRunning is true and InitiativeClockMode ==
    ///     Freeze (at most one map can have a running tracker matter here in practice, since only
    ///     one map is ever "open"/actively played at a time).
    /// </summary>
    private bool _clockWasRunningBeforeInitiative;

    /// <summary>
    ///     Fired whenever the current-turn token changes (including to/from null) - MapLiveWindow
    ///     subscribes to this (filtered to the map it's editing) to switch floor and pan the view
    ///     to the new current-turn token, matching how EditCanvas.TokenSelected is already used for
    ///     a similar "the ViewModel changed something the window needs to react to" purpose.
    /// </summary>
    public event Action<MapItemViewModel, MapTokenViewModel?>? InitiativeTurnChanged;

    /// <summary>
    ///     A player pinged the map (floorId, x, y) - MapLiveWindow subscribes to show it on its
    ///     mirrored preview, matching InitiativeTurnChanged's "broadcast, subscriber filters to
    ///     its own map" pattern. Only one map can be open to players at a time, so there's no map
    ///     identity to filter by beyond that.
    /// </summary>
    public event Action<Guid, double, double>? PlayerMapPingReceived;

    /// <summary>
    ///     Same shape as PlayerMapPingReceived, for a player's freehand annotation stroke instead
    ///     of a single point - carries the originating player's ClientId too (see
    ///     PainterTagHelper) so subscribers can render it with a consistent per-painter color/tag.
    /// </summary>
    public event Action<Guid, IReadOnlyList<AnnotationPoint>, string>? PlayerMapAnnotationReceived;

    /// <summary>
    ///     Starts tracking initiative for this map from the top of the order - resets
    ///     InitiativeCurrentIndex/Round rather than resuming wherever it was left, since a
    ///     previous stop implies the prior combat ended.
    /// </summary>
    public void StartInitiative(MapItemViewModel map)
    {
        if (map.InitiativeEntries.Count == 0) return;

        map.InitiativeIsRunning = true;
        map.InitiativeCurrentIndex = 0;
        map.InitiativeRound = 1;

        if (InitiativeClockMode == InitiativeClockMode.Freeze)
        {
            _clockWasRunningBeforeInitiative = _clock.IsRunning;
            _clock.Pause();
        }

        ApplyCurrentTurn(map, previousTokenId: null);
    }

    public void StopInitiative(MapItemViewModel map)
    {
        var previousTokenId = GetCurrentTurnTokenId(map);
        map.InitiativeIsRunning = false;

        if (InitiativeClockMode == InitiativeClockMode.Freeze && _clockWasRunningBeforeInitiative) _clock.Start();

        ApplyCurrentTurn(map, previousTokenId);
    }

    /// <summary>
    ///     Advances to the next non-skipped entry (see IsInitiativeEntrySkipped), wrapping back to
    ///     0 and incrementing Round when it passes the end - bounded to at most one full lap so an
    ///     all-skipped order can't infinite-loop (the pointer just settles on whichever skipped
    ///     entry it reached).
    /// </summary>
    public void AdvanceInitiative(MapItemViewModel map)
    {
        if (!map.InitiativeIsRunning || map.InitiativeEntries.Count == 0) return;

        var previousTokenId = GetCurrentTurnTokenId(map);
        var wrapped = false;
        var attempts = 0;
        do
        {
            map.InitiativeCurrentIndex++;
            if (map.InitiativeCurrentIndex >= map.InitiativeEntries.Count)
            {
                map.InitiativeCurrentIndex = 0;
                wrapped = true;
            }

            attempts++;
        } while (attempts <= map.InitiativeEntries.Count &&
                 IsInitiativeEntrySkipped(map.InitiativeEntries[map.InitiativeCurrentIndex]));

        if (wrapped)
        {
            map.InitiativeRound++;
            if (InitiativeClockMode == InitiativeClockMode.AdvancePerRound)
                _clock.Jump(TimeSpan.FromSeconds(InitiativeAdvanceSecondsPerRound));
        }

        ApplyCurrentTurn(map, previousTokenId);
    }

    /// <summary>
    ///     Whether an entry's linked Character's active Status (see StatusDefinitionCatalog)
    ///     skips its initiative turn - always false for a freeform entry (nothing to look up).
    /// </summary>
    private bool IsInitiativeEntrySkipped(InitiativeEntryViewModel entry)
    {
        if (entry.LinkKind != TokenLinkKind.Character || entry.LinkedId is not { } linkedId) return false;

        var npc = NpcLibrary.FirstOrDefault(n => n.Id == linkedId);
        if (npc is null) return false;

        var variant = npc.Variants.FirstOrDefault(v => v.Id == entry.LinkedVariantId) ?? npc.DefaultVariant;
        return StatusDefinitionCatalog.Resolve(GetActiveThemeDefinition(), variant.StatusId)?.SkipsInitiativeTurn ??
               false;
    }

    /// <summary>
    ///     Pushes the (possibly unchanged) current-turn token state to the network/local preview
    ///     and fires InitiativeTurnChanged for the live map window's pan-to-token - called after
    ///     Start/Stop/Advance, all of which may have changed which token (if any) is "current".
    /// </summary>
    private void ApplyCurrentTurn(MapItemViewModel map, Guid? previousTokenId)
    {
        var newTokenId = GetCurrentTurnTokenId(map);

        if (previousTokenId is { } prevId && prevId != newTokenId)
        {
            var previousToken = map.Tokens.FirstOrDefault(t => t.Id == prevId);
            if (previousToken is not null) RefreshTokenPlayerState(map, previousToken);
        }

        MapTokenViewModel? newToken = null;
        if (newTokenId is { } newId)
        {
            newToken = map.Tokens.FirstOrDefault(t => t.Id == newId);
            if (newToken is not null && newId != previousTokenId) RefreshTokenPlayerState(map, newToken);
        }

        InitiativeTurnChanged?.Invoke(map, newToken);
    }

    /// <summary>
    ///     Who references a Media Library item (a freeform token's IconImage), a Character, or a
    ///     Point of Interest via a map token - registered into _usageRegistry so deleting any of
    ///     those goes through the same 3-way confirm-delete flow as any other in-use item. The
    ///     same id could only ever match one of IconImage/LinkedId at a time in practice (they're
    ///     different Guid spaces), so checking both per token is safe.
    /// </summary>
    private IEnumerable<string> FindMapTokenUsagesById(Guid id)
    {
        foreach (var map in MapLibrary)
        foreach (var token in map.Tokens)
            if (token.IconImage?.Id == id || token.LinkedId == id)
                yield return string.Format(LocalizationService.Get("MainWindowViewModel.Labels.MapTokenUsage"),
                    map.Name);
    }

    /// <summary>
    ///     A freeform token's IconImage reference is cleared like any other icon reference (see
    ///     ClearNpcReferencesById). A linked token whose Character/PointOfInterest was deleted has
    ///     nothing left to display (unlike a freeform token, it carries no Name/Description of its
    ///     own - see MapTokenViewModel's doc comment), so the token itself is removed instead of
    ///     left dangling with a link to nothing.
    /// </summary>
    private void ClearMapTokenReferencesById(Guid id)
    {
        foreach (var map in MapLibrary)
        {
            foreach (var token in map.Tokens.Where(t => t.IconImage?.Id == id).ToList()) token.IconImage = null;
            foreach (var token in map.Tokens.Where(t => t.LinkedId == id).ToList()) map.RemoveToken(token);
        }
    }

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    /// <summary>
    ///     Shared by every library's Save*LibrarySettings method: split items by Scope, write
    ///     the Shared-scoped ones into the always-present Shared settings file, and (only if a
    ///     session is open) the SessionLocal-scoped ones into that session's library file - only
    ///     the DTO shape and which list on each settings object to assign differ per library.
    /// </summary>
    private void SaveLibrarySettings<TItem, TDto>(
        IEnumerable<TItem> items,
        Func<TItem, LibraryScope> getScope,
        Func<TItem, TDto> toDto,
        Action<ThemeSettingsService.ThemeSettingsDto, List<TDto>> setShared,
        Action<SessionLibraryDto, List<TDto>> setSessionLocal)
    {
        var itemList = items as IList<TItem> ?? items.ToList();

        var settings = ThemeSettingsService.LoadSettings();
        setShared(settings, itemList.Where(i => getScope(i) == LibraryScope.Shared).Select(toDto).ToList());
        ThemeSettingsService.SaveSettings(settings);

        if (_sessionService.IsSessionOpen)
        {
            var sessionLibrary = _sessionService.LoadLibrary();
            setSessionLocal(sessionLibrary,
                itemList.Where(i => getScope(i) == LibraryScope.SessionLocal).Select(toDto).ToList());
            _sessionService.SaveLibrary(sessionLibrary);
        }
    }

    private void SaveMapLibrarySettings()
    {
        SaveLibrarySettings(MapLibrary, map => map.Scope, ToMapLibraryEntryDto,
            (settings, list) => settings.MapLibrary = list,
            (sessionLibrary, list) => sessionLibrary.MapLibrary = list);
    }

    public void NotifyMapPrepareWindowOpened(Guid mapId)
    {
        _mapsBeingPrepared.Add(mapId);
        OnPropertyChanged(nameof(IsSelectedMapBeingPrepared));
        OnPropertyChanged(nameof(CanShowSelectedMap));
    }

    public void NotifyMapPrepareWindowClosed(Guid mapId)
    {
        _mapsBeingPrepared.Remove(mapId);
        OnPropertyChanged(nameof(IsSelectedMapBeingPrepared));
        OnPropertyChanged(nameof(CanShowSelectedMap));
    }

    partial void OnOpenMapChanged(MapItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsSelectedMapOpenToPlayers));
    }

    partial void OnIsMapOpenToPlayersChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSelectedMapOpenToPlayers));
    }

    partial void OnSelectedMapChanged(MapItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsSelectedMapOpenToPlayers));
        OnPropertyChanged(nameof(IsSelectedMapBeingPrepared));
        OnPropertyChanged(nameof(CanShowSelectedMap));

        if (_selectedMapForFloorsWatch is not null)
            _selectedMapForFloorsWatch.PropertyChanged -= OnSelectedMapPropertyChangedForCanShow;
        _selectedMapForFloorsWatch = value;
        if (_selectedMapForFloorsWatch is not null)
            _selectedMapForFloorsWatch.PropertyChanged += OnSelectedMapPropertyChangedForCanShow;
    }

    private void OnSelectedMapPropertyChangedForCanShow(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapItemViewModel.HasNoFloors)) OnPropertyChanged(nameof(CanShowSelectedMap));
    }

    /// <summary>
    ///     Resolves a map's effective fog render style: its own per-map override where set,
    ///     falling back to the global Settings-tab values otherwise (see
    ///     MapLibraryEntryDto's fog-style fields). Public so MapPrepareWindow/
    ///     MapLiveWindow can render their own local preview with the same effective style used for
    ///     the real broadcast.
    /// </summary>
    public (string ColorHex, int OpacityPercent, double BlurRadius, bool BlurEnabled) GetEffectiveFogStyle(
        MapItemViewModel map)
    {
        return (map.FogColorHex ?? FogColorHex,
            map.FogOpacityPercent ?? FogOpacityPercent,
            map.FogBlurRadius ?? FogBlurRadius,
            map.FogBlurEnabled ?? FogBlurEnabled);
    }

    /// <summary>
    ///     Re-applies/re-broadcasts the currently open map's effective fog style - called
    ///     both when the global fallback changes (ApplyAndBroadcastFogStyle) and when a map's own
    ///     override changes (MapItemViewModel.OnFog*Changed via OnMapLibraryItemChanged).
    /// </summary>
    private void ApplyAndBroadcastEffectiveFogStyleForOpenMap()
    {
        if (OpenMap is not { } map) return;

        var style = GetEffectiveFogStyle(map);
        MapDisplay.ApplyRenderStyle(FogOverlayRenderer.BuildHiddenColor(style.ColorHex, style.OpacityPercent),
            style.BlurRadius, style.BlurEnabled);
        _ = _playerServer.PublishMapRenderStyleAsync(style.ColorHex, style.OpacityPercent, style.BlurRadius,
            style.BlurEnabled);
    }

    private void ApplyAndBroadcastFogStyle()
    {
        ApplyAndBroadcastEffectiveFogStyleForOpenMap();

        var settings = ThemeSettingsService.LoadSettings();
        settings.FogColorHex = FogColorHex;
        settings.FogOpacityPercent = FogOpacityPercent;
        settings.FogBlurRadius = FogBlurRadius;
        settings.FogBlurEnabled = FogBlurEnabled;
        ThemeSettingsService.SaveSettings(settings);
    }

    partial void OnFogColorHexChanged(string value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogOpacityPercentChanged(int value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogBlurRadiusChanged(double value)
    {
        ApplyAndBroadcastFogStyle();
    }

    partial void OnFogBlurEnabledChanged(bool value)
    {
        ApplyAndBroadcastFogStyle();
    }

    /// <summary>
    ///     The live (current) fog for a floor - loaded from its starting template on first
    ///     access, then mutated in place as the GM paints.
    /// </summary>
    public FogMask GetLiveFog(MapFloorItemViewModel floor)
    {
        if (_liveFog.TryGetValue(floor.Id, out var fog)) return fog;

        fog = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(File.ReadAllBytes(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);
        _liveFog[floor.Id] = fog;
        return fog;
    }

    /// <summary>
    ///     The in-memory prepare-mode fog for a floor, edited by MapPrepareWindow - loaded
    ///     from the same on-disk starting template as GetLiveFog, but cached and saved
    ///     independently (see _prepareFog).
    /// </summary>
    public FogMask GetPrepareFog(MapFloorItemViewModel floor)
    {
        if (_prepareFog.TryGetValue(floor.Id, out var fog)) return fog;

        fog = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(File.ReadAllBytes(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);
        _prepareFog[floor.Id] = fog;
        return fog;
    }

    /// <summary>
    ///     Persists the current in-memory prepare-mode fog back to the floor's starting
    ///     template file - called debounced from MapPrepareWindow as the GM paints, so a crash or
    ///     forced close doesn't silently lose prep work.
    /// </summary>
    public async Task SavePrepareFogAsync(MapFloorItemViewModel floor)
    {
        await File.WriteAllBytesAsync(floor.FogPath, FogMaskSerializer.Serialize(GetPrepareFog(floor)));
    }

    /// <summary>
    ///     Changes a floor's CellSizePx (GM-editable from MapPrepareWindow), rescaling every copy
    ///     of its fog mask (the on-disk starting template, the in-memory _prepareFog cache, and the
    ///     in-memory _liveFog cache) to the new grid dimensions so existing revealed/hidden cells
    ///     stay aligned - see FogMaskRescaler. Recomputes GridWidth/GridHeight from the floor's
    ///     actual image dimensions the same way AddFloorToMapAsync does. If this floor's map is
    ///     currently open to players, re-broadcasts the full (now differently-sized) live mask
    ///     rather than an incremental cell update, since every coordinate has shifted.
    /// </summary>
    public async Task RescaleFloorCellSizeAsync(MapItemViewModel map, MapFloorItemViewModel floor, int newCellSizePx)
    {
        if (newCellSizePx == floor.CellSizePx) return;

        int newGridWidth, newGridHeight;
        await using (var dimensionStream = File.OpenRead(floor.ImagePath))
        {
            var bitmap = new Bitmap(dimensionStream);
            newGridWidth = Math.Max(1, (bitmap.PixelSize.Width + newCellSizePx - 1) / newCellSizePx);
            newGridHeight = Math.Max(1, (bitmap.PixelSize.Height + newCellSizePx - 1) / newCellSizePx);
        }

        var onDisk = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(await File.ReadAllBytesAsync(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);
        var rescaledOnDisk = FogMaskRescaler.Rescale(onDisk, newCellSizePx, newGridWidth, newGridHeight);
        await File.WriteAllBytesAsync(floor.FogPath, FogMaskSerializer.Serialize(rescaledOnDisk));

        if (_prepareFog.TryGetValue(floor.Id, out var prepareFog))
            _prepareFog[floor.Id] = FogMaskRescaler.Rescale(prepareFog, newCellSizePx, newGridWidth, newGridHeight);

        var liveWasOpen = ReferenceEquals(OpenMap, map);
        if (_liveFog.TryGetValue(floor.Id, out var liveFog))
            _liveFog[floor.Id] = FogMaskRescaler.Rescale(liveFog, newCellSizePx, newGridWidth, newGridHeight);

        floor.CellSizePx = newCellSizePx;
        floor.GridWidth = newGridWidth;
        floor.GridHeight = newGridHeight;
        SaveMapLibrarySettings();

        if (liveWasOpen)
            // Coordinates shifted for every cell - a full resync, not an incremental
            // BroadcastFogCellsAsync, is the only way to keep clients correct.
            await OpenMapToPlayersAsync(map);
    }

    [RelayCommand]
    private async Task OpenMapToPlayersAsync(MapItemViewModel map)
    {
        // A map currently being prepared (MapPrepareWindow open) must never also be streamed -
        // editing (including a CellSizePx rescale) and an active broadcast of the same mask must
        // never coincide. The Maps tab's "Show" button is already disabled for this case; this is
        // defense-in-depth against the command being invoked another way.
        if (_mapsBeingPrepared.Contains(map.Id)) return;

        var floors = new List<TcpPlayerServerService.OpenMapFloor>();
        var displayFloors = new List<MapDisplayFloor>();
        foreach (var floor in map.Floors)
        {
            if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

            var startingFog = FogMaskSerializer.Deserialize(await File.ReadAllBytesAsync(floor.FogPath));
            var liveFog = GetLiveFog(floor);
            MediaTypeHelper.TryGetKind(floor.ImagePath, out _, out var mimeType);
            floors.Add(new TcpPlayerServerService.OpenMapFloor
            {
                FloorId = floor.Id,
                FloorName = floor.Name,
                ImageFileName = Path.GetFileName(floor.ImagePath),
                ImageMimeType = mimeType,
                ImageBytes = await File.ReadAllBytesAsync(floor.ImagePath),
                CellSizePx = floor.CellSizePx,
                GridWidth = floor.GridWidth,
                GridHeight = floor.GridHeight,
                StartingFog = startingFog,
                CurrentFog = liveFog
            });

            using var imageStream = File.OpenRead(floor.ImagePath);
            displayFloors.Add(new MapDisplayFloor
            {
                FloorId = floor.Id,
                Name = floor.Name,
                Image = new Bitmap(imageStream),
                CurrentFog = liveFog
            });
        }

        OpenMap = map;
        EditingFloor ??= map.Floors.FirstOrDefault();
        IsMapOpenToPlayers = true;
        var tokens = BuildVisibleTokenSnapshots(map);
        var lines = BuildVisibleLineSnapshots(map);
        await _playerServer.PublishMapShowAsync(map.Id, map.Name, floors, tokens, lines);
        MapDisplay.ShowMap(map.Name, displayFloors);
        foreach (var token in tokens) MapDisplay.UpsertToken(token);
        ApplyAndBroadcastEffectiveFogStyleForOpenMap();
        OnPropertyChanged(nameof(ShouldShowMapLocally));
        Log.Information("Map opened to players: {MapName} ({FloorCount} floors)", map.Name, floors.Count);
    }

    [RelayCommand]
    private async Task CloseMapToPlayersAsync()
    {
        IsMapOpenToPlayers = false;
        await _playerServer.PublishMapHideAsync();
        MapDisplay.HideMap();
        OnPropertyChanged(nameof(ShouldShowMapLocally));
        Log.Information("Map closed to players");
    }

    /// <summary>
    ///     Broadcasts a debounced batch of already-applied reveal/hide cells (the editor paints
    ///     the live FogMask returned by GetLiveFog directly, for immediate visual feedback - this
    ///     just tells connected players). Harmless to call for a floor that isn't the currently
    ///     open map: the server-side cache update and the broadcast are both no-ops for clients
    ///     that don't have that floor loaded.
    /// </summary>
    public Task BroadcastFogCellsAsync(Guid floorId, List<FogCellDto> cells)
    {
        return cells.Count == 0 ? Task.CompletedTask : _playerServer.PublishMapFogUpdateAsync(floorId, cells);
    }

    /// <summary>
    ///     GM double-clicked the map (see MapEditCanvasControl.PingRequested) - broadcasts to
    ///     every connected player and shows it on the Host's own local preview too, same "no
    ///     network round-trip needed for our own state" pattern as RefreshTokenPlayerState.
    /// </summary>
    public Task BroadcastMapPingAsync(Guid floorId, double x, double y)
    {
        MapDisplay.NotifyPingReceived(floorId, x, y);
        return _playerServer.PublishMapPingAsync(floorId, x, y);
    }

    /// <summary>
    ///     GM's Draw tool finished a Temporary-tier stroke (MapEditCanvasControl.TemporaryLineDrawn) -
    ///     unlike SemiPermanent/Permanent (AddMapLine), a Temporary line is never added to
    ///     floor.Lines/persisted at all; it's purely a live fade-and-forget stroke, reusing the exact
    ///     same ephemeral pipeline a player's own Shift+drag stroke uses. colorHex (the GM's picked
    ///     Draw-tool color) travels inline in a PainterTagHelper sentinel ClientId rather than a
    ///     dedicated wire field, so every recipient can tell this apart from a real player's stroke
    ///     and render it in that color with no player-tag label (see MapDisplayView.OnAnnotationReceived).
    /// </summary>
    public Task BroadcastGmAnnotationAsync(Guid floorId, IReadOnlyList<AnnotationPoint> points, string colorHex)
    {
        var sentinel = PainterTagHelper.GmSentinelFor(colorHex);
        MapDisplay.NotifyAnnotationReceived(floorId, points, sentinel);
        return _playerServer.PublishGmAnnotationAsync(floorId, points, sentinel);
    }

    /// <summary>
    ///     "Reset to Prepared" (MapLiveWindow): pushes floor.FogPath - which is exactly what
    ///     MapPrepareWindow's edits save to (see SavePrepareFogAsync) - into the live fog. Three
    ///     fog states exist per floor: the on-disk file (the prepare/starting template), _liveFog
    ///     (the broadcast/player-visible mask), and _prepareFog (MapPrepareWindow's in-memory
    ///     cache, lazily backed by the same file). _liveFog and _prepareFog seed independently from
    ///     disk and do not auto-sync with each other - Live only ever changes via a direct edit in
    ///     MapLiveWindow or this explicit reset.
    /// </summary>
    public async Task ResetFloorFogToStartingAsync(MapFloorItemViewModel floor)
    {
        var startingFog = File.Exists(floor.FogPath)
            ? FogMaskSerializer.Deserialize(await File.ReadAllBytesAsync(floor.FogPath))
            : FogMask.CreateFullyHidden(floor.GridWidth, floor.GridHeight, floor.CellSizePx);

        // Mutated in place (not replacing the dictionary entry) so the exact same FogMask
        // instance already shared with MapDisplay/MapEditorWindow stays valid - see GetLiveFog.
        var live = GetLiveFog(floor);
        live.RevealedBits = startingFog.RevealedBits;

        await _playerServer.PublishMapFogResetAsync(floor.Id);
        MapDisplay.NotifyFogChanged(floor.Id);

        if (OpenMap is { } map && map.Floors.Contains(floor))
            foreach (var token in map.Tokens.Where(t =>
                         t.FloorId == floor.Id && t.RevealMode == TokenRevealMode.HiddenUntilRevealed))
                RefreshTokenPlayerState(map, token);
    }

    /// <summary>
    ///     Exports a single map (its floors' images + starting fog) as a self-contained
    ///     <c>.rtt-map</c> zip - one map per file, unlike the whole-folder Media/Sound library
    ///     export/import (a map is a single unit a GM shares/backs up, not a batch of many).
    /// </summary>
    public byte[] ExportMapToZipBytes(MapItemViewModel map)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var manifest = new MapManifestDto { Name = map.Name, FormatVersion = map.FormatVersion };
            var order = 0;
            foreach (var floor in map.Floors)
            {
                if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

                var imageFileName = $"{Guid.NewGuid():N}{Path.GetExtension(floor.ImagePath)}";
                var fogFileName = $"{Guid.NewGuid():N}.fog";
                WriteZipFileEntry(zip, imageFileName, floor.ImagePath);
                WriteZipFileEntry(zip, fogFileName, floor.FogPath);
                manifest.Floors.Add(new MapFloorManifestEntryDto
                {
                    Name = floor.Name,
                    ImageFileName = imageFileName,
                    FogFileName = fogFileName,
                    CellSizePx = floor.CellSizePx,
                    GridWidth = floor.GridWidth,
                    GridHeight = floor.GridHeight,
                    Order = order++
                });
            }

            var manifestEntry = zip.CreateEntry(MapManifestEntryName);
            using var manifestStream = manifestEntry.Open();
            using var writer = new StreamWriter(manifestStream);
            writer.Write(JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
        }

        Log.Information("Map exported: {MapName} ({FloorCount} floors)", map.Name, map.Floors.Count);
        return stream.ToArray();
    }

    public void ImportMapFromZipBytes(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var manifestEntry = zip.GetEntry(MapManifestEntryName);
            if (manifestEntry is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidMapExportFound");
                return;
            }

            MapManifestDto? manifest;
            using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream))
            {
                manifest = JsonSerializer.Deserialize<MapManifestDto>(reader.ReadToEnd());
            }

            if (manifest is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.ManifestCouldNotBeRead");
                return;
            }

            var mapId = Guid.NewGuid();
            var map = new MapItemViewModel(mapId, manifest.Name, DefaultMapCellSizePx, RemoveMap,
                OnMapLibraryItemChanged, formatVersion: manifest.FormatVersion);
            var mapDirectory = Path.Combine(ThemeSettingsService.MapLibraryDirectory, mapId.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            foreach (var floorEntry in manifest.Floors.OrderBy(f => f.Order))
            {
                var imageEntry = zip.GetEntry(floorEntry.ImageFileName);
                var fogEntry = zip.GetEntry(floorEntry.FogFileName);
                if (imageEntry is null || fogEntry is null) continue;

                var floorId = Guid.NewGuid();
                var imagePath = Path.Combine(mapDirectory, $"{floorId:N}{Path.GetExtension(floorEntry.ImageFileName)}");
                var fogPath = Path.Combine(mapDirectory, $"{floorId:N}.fog");
                using (var entryStream = imageEntry.Open())
                using (var target = File.Create(imagePath))
                {
                    entryStream.CopyTo(target);
                }

                using (var entryStream = fogEntry.Open())
                using (var target = File.Create(fogPath))
                {
                    entryStream.CopyTo(target);
                }

                map.Floors.Add(new MapFloorItemViewModel(
                    floorId, floorEntry.Name, imagePath, fogPath,
                    floorEntry.CellSizePx, floorEntry.GridWidth, floorEntry.GridHeight,
                    LoadMapFloorThumbnail(imagePath),
                    f => RemoveFloorFromMap(map, f), _ => SaveMapLibrarySettings()));
            }

            MapLibrary.Add(map);
            SaveMapLibrarySettings();
            Log.Information("Map imported: {MapName} ({FloorCount} floors)", map.Name, map.Floors.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MapImported"),
                map.Name));
        }
        catch (InvalidDataException)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidMapExportFound");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Map import failed");
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"),
                ex.Message);
        }
    }
}