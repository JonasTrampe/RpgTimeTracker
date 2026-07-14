using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    // ==================== Full session export/import (state + all three libraries in one file) ====================

    /// <summary>
    ///     Bundles everything needed to fully restore or move a campaign: the game state
    ///     (same shape as ExportStateToJson/the .rtt-save format) plus the Media, Sound, and
    ///     Map libraries - into a single .rtt-session zip. Unlike the individual per-library
    ///     export (folder-based, for backing up/sharing just that one library) or .rtt-save
    ///     (state only, since libraries are meant to be durable/shared across campaigns), this
    ///     is the "give me one file with everything" option, e.g. for moving to a new machine.
    /// </summary>
    public byte[] ExportFullSessionToZipBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipTextEntry(zip, "state.json", ExportStateToJson());
            WriteMediaManifestSection(zip, MediaLibrary);
            WriteSoundManifestSection(zip, SoundLibrary);
            WriteMapManifestSection(zip, MapLibrary);
            WriteNpcManifestSection(zip, NpcLibrary);
        }

        Log.Information("Full session exported: {MediaCount} media, {SoundCount} sounds, {MapCount} maps, {NpcCount} characters",
            MediaLibrary.Count, SoundLibrary.Count, MapLibrary.Count, NpcLibrary.Count);
        return stream.ToArray();
    }

    /// <summary>
    ///     Exports only the currently open session: its own state + its own SessionLocal-scoped
    ///     Media/Sound/Map/NPC entries. An NPC referencing a Shared-scoped Media/Sound item is
    ///     still exported (NPCs themselves are always included, regardless of scope elsewhere -
    ///     only the referenced asset's bundling is scope-gated); if that Shared asset isn't
    ///     bundled (because it's Shared, not session-local), the reference is silently dropped on
    ///     import rather than left dangling - see ImportNpcEntry. Caller must check IsSessionOpen
    ///     first.
    /// </summary>
    public byte[] ExportSessionToZipBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipTextEntry(zip, "state.json", ExportStateToJson());
            var sessionMedia = MediaLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList();
            var sessionSound = SoundLibrary.Where(i => i.Scope == LibraryScope.SessionLocal).ToList();
            var sessionMaps = MapLibrary.Where(m => m.Scope == LibraryScope.SessionLocal).ToList();
            var sessionNpcs = NpcLibrary.Where(n => n.Scope == LibraryScope.SessionLocal).ToList();
            WriteMediaManifestSection(zip, sessionMedia);
            WriteSoundManifestSection(zip, sessionSound);
            WriteMapManifestSection(zip, sessionMaps);
            WriteNpcManifestSection(zip, sessionNpcs);

            Log.Information("Session exported: {MediaCount} media, {SoundCount} sounds, {MapCount} maps, {NpcCount} characters",
                sessionMedia.Count, sessionSound.Count, sessionMaps.Count, sessionNpcs.Count);
        }

        return stream.ToArray();
    }

    private static void WriteMediaManifestSection(ZipArchive zip, IEnumerable<MediaLibraryItemViewModel> items)
    {
        var manifest = new LibraryManifestDto { LibraryType = "Media" };
        foreach (var item in items)
        {
            if (!File.Exists(item.LocalPath)) continue;

            var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
            WriteZipFileEntry(zip, $"media/{fileName}", item.LocalPath);
            manifest.Items.Add(new LibraryManifestEntryDto
            {
                Id = item.Id, Name = item.Name, FileName = fileName, MimeType = item.MimeType,
                Kind = item.Kind.ToString(), Loop = item.Loop
            });
        }

        WriteZipTextEntry(zip, "media/manifest.json", JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
    }

    private static void WriteSoundManifestSection(ZipArchive zip, IEnumerable<SoundLibraryItemViewModel> items)
    {
        var manifest = new LibraryManifestDto { LibraryType = "Sound" };
        foreach (var item in items)
        {
            if (!File.Exists(item.LocalPath)) continue;

            var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
            WriteZipFileEntry(zip, $"sound/{fileName}", item.LocalPath);
            manifest.Items.Add(new LibraryManifestEntryDto
            {
                Id = item.Id, Name = item.Name, Icon = item.Icon, FileName = fileName, MimeType = item.MimeType, Loop = item.Loop,
                Volume = item.Volume, RepeatCount = item.RepeatCount,
                TrimStartMs = (long)(item.TrimStartSeconds * 1000), TrimEndMs = (long)(item.TrimEndSeconds * 1000)
            });
        }

        WriteZipTextEntry(zip, "sound/manifest.json", JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
    }

    private static void WriteMapManifestSection(ZipArchive zip, IEnumerable<MapItemViewModel> maps)
    {
        var manifest = new MapLibraryManifestDto();
        foreach (var map in maps)
        {
            var mapEntry = new MapLibraryManifestEntryDto { Name = map.Name, FormatVersion = map.FormatVersion };
            var order = 0;
            foreach (var floor in map.Floors)
            {
                if (!File.Exists(floor.ImagePath) || !File.Exists(floor.FogPath)) continue;

                var imageFileName = $"{Guid.NewGuid():N}{Path.GetExtension(floor.ImagePath)}";
                var fogFileName = $"{Guid.NewGuid():N}.fog";
                WriteZipFileEntry(zip, $"maps/{imageFileName}", floor.ImagePath);
                WriteZipFileEntry(zip, $"maps/{fogFileName}", floor.FogPath);
                mapEntry.Floors.Add(new MapFloorManifestEntryDto
                {
                    Name = floor.Name, ImageFileName = imageFileName, FogFileName = fogFileName,
                    CellSizePx = floor.CellSizePx, GridWidth = floor.GridWidth, GridHeight = floor.GridHeight,
                    Order = order++
                });
            }

            manifest.Maps.Add(mapEntry);
        }

        WriteZipTextEntry(zip, "maps/manifest.json", JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
    }

    private static void WriteNpcManifestSection(ZipArchive zip, IEnumerable<NpcLibraryItemViewModel> npcs)
    {
        var manifest = new NpcLibraryManifestDto { Npcs = npcs.Select(ToNpcLibraryEntryDto).ToList() };
        WriteZipTextEntry(zip, "npcs/manifest.json", JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
    }

    /// <summary>
    ///     Restores game state (replacing it, like a normal load) and adds every library item
    ///     found in the bundle (appending, like a normal library import - existing library
    ///     items are left alone, so importing the same session twice will duplicate entries;
    ///     this matches the existing single-library import behavior rather than introducing a
    ///     new merge policy just for this feature).
    /// </summary>
    public void ImportFullSessionFromZipBytes(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var stateEntry = zip.GetEntry("state.json");
            if (stateEntry is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
                return;
            }

            using (var entryStream = stateEntry.Open())
            using (var reader = new StreamReader(entryStream, Encoding.UTF8))
            {
                ImportStateFromJson(reader.ReadToEnd());
            }

            var (mediaImported, mediaIdMap) = ImportMediaSectionFromZip(zip);
            var (soundImported, soundIdMap) = ImportSoundSectionFromZip(zip);
            var mapsImported = ImportMapsSectionFromZip(zip);
            var npcsImported = ImportNpcSectionFromZip(zip, LibraryScope.Shared, mediaIdMap, soundIdMap);

            Log.Information(
                "Full session imported: {MediaCount} media, {SoundCount} sounds, {MapCount} maps, {NpcCount} NPCs",
                mediaImported, soundImported, mapsImported, npcsImported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.FullSessionImported"),
                mediaImported, soundImported, mapsImported));
        }
        catch (InvalidDataException)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Full session import failed");
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    /// <summary>
    ///     Imports a .rtt-session bundle into a brand-new Session folder rather than merging into
    ///     the Shared Library: creates the session (see SessionService.CreateSession), restores
    ///     state, and lands every Media/Sound/Map entry as a fresh, SessionLocal-scoped copy with
    ///     a brand-new Id/file (matching the existing "imports always mint fresh Guids" precedent
    ///     from every other import path in this class) - never silently merged into the target
    ///     machine's Shared Library.
    /// </summary>
    public void ImportSessionIntoNewFolder(byte[] data, string targetFolderPath)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var stateEntry = zip.GetEntry("state.json");
            if (stateEntry is null)
            {
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
                return;
            }

            _sessionService.CreateSession(targetFolderPath);

            using (var entryStream = stateEntry.Open())
            using (var reader = new StreamReader(entryStream, Encoding.UTF8))
            {
                ImportStateFromJson(reader.ReadToEnd());
            }

            var (mediaImported, mediaIdMap) = ImportMediaSectionFromZip(zip, LibraryScope.SessionLocal);
            var (soundImported, soundIdMap) = ImportSoundSectionFromZip(zip, LibraryScope.SessionLocal);
            var mapsImported = ImportMapsSectionFromZip(zip, LibraryScope.SessionLocal);
            var npcsImported = ImportNpcSectionFromZip(zip, LibraryScope.SessionLocal, mediaIdMap, soundIdMap);
            NotifySessionChanged();

            Log.Information(
                "Session imported into {FolderPath}: {MediaCount} media, {SoundCount} sounds, {MapCount} maps, {NpcCount} NPCs",
                targetFolderPath, mediaImported, soundImported, mapsImported, npcsImported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.FullSessionImported"),
                mediaImported, soundImported, mapsImported));
        }
        catch (InvalidDataException)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidSessionExportFound");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Session import failed");
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    /// <summary>Imports the media/ section, returning both the count and an old-Id -&gt; new-Id
    ///     map (the manifest's LibraryManifestEntryDto.Id, from the exporting machine, mapped to
    ///     the freshly minted Id the imported item actually gets) - used by
    ///     ImportNpcSectionFromZip to relink a character variant's portrait/token reference to the
    ///     newly-imported copy instead of a stale foreign Id.</summary>
    private (int Imported, Dictionary<Guid, Guid> IdMap) ImportMediaSectionFromZip(ZipArchive zip,
        LibraryScope scope = LibraryScope.Shared)
    {
        var idMap = new Dictionary<Guid, Guid>();
        var manifest = ReadZipJsonEntry<LibraryManifestDto>(zip, "media/manifest.json");
        if (manifest is null) return (0, idMap);

        var targetDirectory = GetMediaLibraryBaseDirectory(scope);
        Directory.CreateDirectory(targetDirectory);
        var imported = 0;
        foreach (var item in manifest.Items)
        {
            var fileEntry = zip.GetEntry($"media/{item.FileName}");
            if (fileEntry is null) continue;
            if (!Enum.TryParse<MediaKind>(item.Kind, out var kind) || kind == MediaKind.Audio) continue;

            var cachedPath = Path.Combine(targetDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(item.FileName)}");
            using (var entryStream = fileEntry.Open())
            using (var target = File.Create(cachedPath))
            {
                entryStream.CopyTo(target);
            }

            var newId = Guid.NewGuid();
            MediaLibrary.Add(new MediaLibraryItemViewModel(
                newId, item.Name, cachedPath, kind, item.MimeType, item.Loop,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged)
            {
                Scope = scope
            });
            if (item.Id != Guid.Empty) idMap[item.Id] = newId;
            imported++;
        }

        if (imported > 0)
        {
            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
        }

        return (imported, idMap);
    }

    /// <summary>See ImportMediaSectionFromZip's doc comment - same reasoning for Sound.</summary>
    private (int Imported, Dictionary<Guid, Guid> IdMap) ImportSoundSectionFromZip(ZipArchive zip,
        LibraryScope scope = LibraryScope.Shared)
    {
        var idMap = new Dictionary<Guid, Guid>();
        var manifest = ReadZipJsonEntry<LibraryManifestDto>(zip, "sound/manifest.json");
        if (manifest is null) return (0, idMap);

        var targetDirectory = GetSoundLibraryBaseDirectory(scope);
        Directory.CreateDirectory(targetDirectory);
        var imported = 0;
        foreach (var item in manifest.Items)
        {
            var fileEntry = zip.GetEntry($"sound/{item.FileName}");
            if (fileEntry is null) continue;

            var cachedPath = Path.Combine(targetDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(item.FileName)}");
            using (var entryStream = fileEntry.Open())
            using (var target = File.Create(cachedPath))
            {
                entryStream.CopyTo(target);
            }

            var newId = Guid.NewGuid();
            var soundItem = CreateSoundLibraryItem(newId, item.Name, item.Icon ?? VisualItemHelper.IconTimer, cachedPath,
                item.MimeType, item.Loop, item.Volume ?? 100, item.RepeatCount ?? 1,
                item.TrimStartMs / 1000.0, item.TrimEndMs / 1000.0);
            soundItem.Scope = scope;
            SoundLibrary.Add(soundItem);
            if (item.Id != Guid.Empty) idMap[item.Id] = newId;
            imported++;
        }

        if (imported > 0)
        {
            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
        }

        return (imported, idMap);
    }

    private int ImportMapsSectionFromZip(ZipArchive zip, LibraryScope scope = LibraryScope.Shared)
    {
        var manifest = ReadZipJsonEntry<MapLibraryManifestDto>(zip, "maps/manifest.json");
        if (manifest is null) return 0;

        var imported = 0;
        foreach (var mapEntry in manifest.Maps)
        {
            var mapId = Guid.NewGuid();
            var map = new MapItemViewModel(mapId, mapEntry.Name, DefaultMapCellSizePx, RemoveMap,
                OnMapLibraryItemChanged, formatVersion: mapEntry.FormatVersion, scope: scope);
            var mapDirectory = Path.Combine(GetMapLibraryBaseDirectory(scope), mapId.ToString("N"));
            Directory.CreateDirectory(mapDirectory);

            foreach (var floorEntry in mapEntry.Floors.OrderBy(f => f.Order))
            {
                var imageEntry = zip.GetEntry($"maps/{floorEntry.ImageFileName}");
                var fogEntry = zip.GetEntry($"maps/{floorEntry.FogFileName}");
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
            imported++;
        }

        if (imported > 0) SaveMapLibrarySettings();
        return imported;
    }

    /// <summary>Imports the npcs/ section. mediaIdMap/soundIdMap (from ImportMediaSectionFromZip/
    ///     ImportSoundSectionFromZip, called first for the same zip) relink each variant's
    ///     Image/TokenImage/Sound references from the exporting machine's Id to the freshly-imported
    ///     copy's Id; a reference to an asset that wasn't bundled (e.g. a Shared asset outside a
    ///     session-scoped export) has no entry in the map and is silently dropped rather than left
    ///     dangling, matching ClearNpcReferencesById's "unset, don't crash" behavior for deletions.</summary>
    private int ImportNpcSectionFromZip(ZipArchive zip, LibraryScope scope,
        IReadOnlyDictionary<Guid, Guid> mediaIdMap, IReadOnlyDictionary<Guid, Guid> soundIdMap)
    {
        var manifest = ReadZipJsonEntry<NpcLibraryManifestDto>(zip, "npcs/manifest.json");
        if (manifest is null) return 0;

        var imported = 0;
        foreach (var npcEntry in manifest.Npcs)
        {
            var remapped = new NpcLibraryEntryDto
            {
                Id = Guid.NewGuid(),
                Name = npcEntry.Name,
                GmInfoBlocks = npcEntry.GmInfoBlocks,
                Variants = npcEntry.Variants.Select(v => new NpcVariantEntryDto
                {
                    Id = Guid.NewGuid(),
                    Name = v.Name,
                    IsDefault = v.IsDefault,
                    ImageId = v.ImageId is { } imageId && mediaIdMap.TryGetValue(imageId, out var newImageId)
                        ? newImageId
                        : null,
                    TokenImageId = v.TokenImageId is { } tokenImageId &&
                                   mediaIdMap.TryGetValue(tokenImageId, out var newTokenImageId)
                        ? newTokenImageId
                        : null,
                    TokenIcon = v.TokenIcon,
                    PlayerInfo = v.PlayerInfo,
                    SoundIds = v.SoundIds?.Select(id => soundIdMap.TryGetValue(id, out var newSoundId) ? newSoundId : (Guid?)null)
                        .Where(id => id is not null).Select(id => id!.Value).ToList()
                }).ToList()
            };
            // ActiveVariantId must reference one of the freshly-minted variant Ids above, positionally
            // matching the original entry (variants are processed in the same order).
            var activeIndex = npcEntry.Variants.FindIndex(v => v.Id == npcEntry.ActiveVariantId);
            remapped.ActiveVariantId = activeIndex >= 0 ? remapped.Variants[activeIndex].Id : Guid.Empty;

            var npc = FromNpcLibraryEntryDto(remapped, scope);
            if (npc is null) continue;

            NpcLibrary.Add(npc);
            imported++;
        }

        if (imported > 0)
        {
            OnPropertyChanged(nameof(HasNoNpcLibraryItems));
            SaveNpcLibrarySettings();
        }

        return imported;
    }

    private static void WriteZipTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteZipFileEntry(ZipArchive zip, string entryName, string sourceFilePath)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var sourceStream = File.OpenRead(sourceFilePath);
        sourceStream.CopyTo(entryStream);
    }

    private static T? ReadZipJsonEntry<T>(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return default;

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd());
    }

    // ==================== Gallery: session's own list of sent images/videos ====================
    // Unlike the single-slot model above (CurrentMediaKind), NOTHING is discarded here on the next
    // send - every image/video sent via ad-hoc/library stays in SentMediaItems
    // until it is explicitly removed. The GM and each player navigate independently and locally
    // through this list (see ClientMainWindowViewModel); "highlight" is only a jump suggestion,
    // not a requirement. Event-trigger media are NEVER part of it - they only temporarily
    // override the display (taking precedence), see StopMediaIfTriggeredBy for the resumption afterward.

    public ObservableCollection<SentMediaItemViewModel> SentMediaItems { get; } = [];
    public bool HasNoSentMediaItems => SentMediaItems.Count == 0;

    public ObservableCollection<string> SessionLogEntries { get; } = [];

    public bool HasSessionLogEntries => SessionLogEntries.Count > 0;

    /// <summary>
    ///     History of the most recent action notices (newest first) - for the notifications flyout in
    ///     the status bar. ActionStatusMessage remains the "just now" indicator (clears itself after
    ///     4s, see ClearActionStatusAfterDelayAsync), this history persists until it
    ///     exceeds the cap.
    /// </summary>
    public ObservableCollection<string> ActionStatusHistory { get; } = [];

    public bool HasNoActionStatusHistory => ActionStatusHistory.Count == 0;

    public ObservableCollection<ActiveSoundViewModel> ActivePlayingSounds { get; } = [];
    public bool HasNoActivePlayingSounds => ActivePlayingSounds.Count == 0;

    public MediaLibraryItemViewModel? SelectedCalendarTriggerMediaItem
    {
        get => SelectedCalendarEntry?.TriggerMedia.Kind is MediaKind.Image or MediaKind.Video
            ? MediaLibrary.FirstOrDefault(item =>
                string.Equals(item.LocalPath, SelectedCalendarEntry.TriggerMedia.Path,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        set
        {
            if (SelectedCalendarEntry is null)
                return;

            if (value is null)
            {
                if (SelectedCalendarEntry.TriggerMedia.Kind is MediaKind.Image or MediaKind.Video)
                    SelectedCalendarEntry.TriggerMedia.ClearCommand.Execute(null);
            }
            else
            {
                SelectedCalendarEntry.TriggerMedia.Path = value.LocalPath;
                SelectedCalendarEntry.TriggerMedia.FileName = value.Name;
                SelectedCalendarEntry.TriggerMedia.Kind = value.Kind;
                SelectedCalendarEntry.TriggerMedia.Loop = value.Loop;
            }

            RefreshCalendarViews();
            QueueCalendarFullResync();
        }
    }

    public SoundLibraryItemViewModel? SelectedCalendarTriggerSoundItem
    {
        get => SelectedCalendarEntry?.TriggerMedia.Kind == MediaKind.Audio
            ? SoundLibrary.FirstOrDefault(item =>
                string.Equals(item.LocalPath, SelectedCalendarEntry.TriggerMedia.Path,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        set
        {
            if (SelectedCalendarEntry is null)
                return;

            if (value is null)
            {
                if (SelectedCalendarEntry.TriggerMedia.Kind == MediaKind.Audio)
                    SelectedCalendarEntry.TriggerMedia.ClearCommand.Execute(null);
            }
            else
            {
                SelectedCalendarEntry.TriggerMedia.Path = value.LocalPath;
                SelectedCalendarEntry.TriggerMedia.FileName = value.Name;
                SelectedCalendarEntry.TriggerMedia.Kind = MediaKind.Audio;
                SelectedCalendarEntry.TriggerMedia.Loop = value.Loop;
                SelectedCalendarEntry.TriggerMedia.Fullscreen = false;
                SelectedCalendarEntry.TriggerMedia.PauseClockDuringVideo = false;
            }

            RefreshCalendarViews();
            QueueCalendarFullResync();
        }
    }

    public bool HasPlayerVisibleCalendarEntries => CalendarEntries.Any(entry =>
        entry.TryBuildDefinition(out var definition) && definition.IsPlayerVisible);

    public bool HasPlayerCalendarEntriesForSelectedDate => PlayerCalendarEntries.Count > 0;
    ICommand IPlayerDisplayContext.ShowPlayerTimelineCommand => ShowPlayerTimelineCommand;

    ICommand IPlayerDisplayContext.ShowPlayerCalendarCommand => ShowPlayerCalendarCommand;

    ICommand IPlayerDisplayContext.PreviousPlayerCalendarMonthCommand => PreviousPlayerCalendarMonthCommand;

    ICommand IPlayerDisplayContext.NextPlayerCalendarMonthCommand => NextPlayerCalendarMonthCommand;

    public bool ShowPlayerTimelineView => !ShowPlayerCalendarView;

    IEnumerable IPlayerDisplayContext.TimelineEntries => PlayerTimelineItems;
    IEnumerable IPlayerDisplayContext.CalendarMonthDays => PlayerCalendarDays;
    IEnumerable IPlayerDisplayContext.CalendarEntries => PlayerCalendarEntries;

    public string SpeedMultiplierDisplay => SpeedMultiplier.ToString("0.0", LocalizationService.Culture) + "×";

    public string SpeedLabel => string.Format(LocalizationService.Get("PlayerHeaderView.SpeedLabel"), SpeedMultiplierDisplay);

    partial void OnNetworkServerAddressChanged(string? value)
    {
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnIsPlayerWindowOpenChanged(bool value)
    {
        if (!value) IsLocalPlayerFullscreen = false;
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        RefreshLocalMediaPreview();
        OnPropertyChanged(nameof(ShouldShowMapLocally));
    }

    /// <summary>
    ///     GM requests that the local player window (if open) be switched to fullscreen - e.g. when an event medium
    ///     with "fullscreen" triggers.
    /// </summary>
    public event Action<bool>? LocalFullscreenRequested;

    partial void OnCurrentMediaKindChanged(MediaKind value)
    {
        OnPropertyChanged(nameof(HasMedia));
        RefreshLocalMediaPreview();
    }

    partial void OnRemoteFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDisplayFullscreenActive));
        OnPropertyChanged(nameof(DisplayFullscreenLabel));
    }

    partial void OnIsLocalPlayerFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDisplayFullscreenActive));
        OnPropertyChanged(nameof(DisplayFullscreenLabel));
    }

    partial void OnShowPlayerCalendarViewChanged(bool value)
    {
        if (value && !HasPlayerVisibleCalendarEntries)
        {
            ShowPlayerCalendarView = false;
            return;
        }

        OnPropertyChanged(nameof(ShowPlayerTimelineView));
    }

    partial void OnSelectedCalendarEntryChanged(CalendarEntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedCalendarEntry));
        OnPropertyChanged(nameof(SelectedCalendarTriggerMediaItem));
        OnPropertyChanged(nameof(SelectedCalendarTriggerSoundItem));
    }

    partial void OnConnectedClientCountChanged(int value)
    {
        if (value == 0) ClearRemoteOnlyActiveSounds();
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnNetworkServerPortChanged(int value)
    {
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    [RelayCommand]
    private void ToggleDisplayFullscreen()
    {
        SetDisplayFullscreen(!IsDisplayFullscreenActive);
    }

    [RelayCommand]
    private void ShowPlayerTimeline()
    {
        ShowPlayerCalendarView = false;
    }

    [RelayCommand]
    private void ShowPlayerCalendar()
    {
        if (!HasPlayerVisibleCalendarEntries)
            return;

        ShowPlayerCalendarView = true;
    }

    [RelayCommand]
    private void PreviousPlayerCalendarMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(-1);
        RefreshCalendarViews();
    }

    [RelayCommand]
    private void NextPlayerCalendarMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(1);
        RefreshCalendarViews();
    }

    /// <summary>
    ///     Unifies the fullscreen request for everything currently present: local
    ///     player window, remote clients, or both.
    /// </summary>
    private void SetDisplayFullscreen(bool value)
    {
        SetLocalPlayerFullscreen(value);
        SetRemotePlayerFullscreen(value);
    }

    private void SetLocalPlayerFullscreen(bool value)
    {
        if (!IsPlayerWindowOpen)
        {
            IsLocalPlayerFullscreen = false;
            return;
        }

        IsLocalPlayerFullscreen = value;
        LocalFullscreenRequested?.Invoke(value);
    }

    private void SetRemotePlayerFullscreen(bool value)
    {
        if (!IsNetworkServerRunning)
        {
            RemoteFullscreen = false;
            return;
        }

        RemoteFullscreen = value;
        _ = _playerServer.PublishDisplayFullscreenAsync(value);
    }

    public void ReportLocalPlayerFullscreen(bool fullscreen)
    {
        IsLocalPlayerFullscreen = IsPlayerWindowOpen && fullscreen;
    }

    /// <summary>
    ///     Decides based on ShouldShowMediaLocally whether the current medium is decoded/
    ///     played locally. Called explicitly instead of via a plain property diff, because
    ///     e.g. CurrentMediaKind doesn't "change" for two media of the same kind in a row
    ///     (image -> image), and the WPF/Avalonia default notification would then not fire.
    /// </summary>
    private void RefreshLocalMediaPreview()
    {
        OnPropertyChanged(nameof(ShouldShowMediaLocally));
        OnPropertyChanged(nameof(ShowLocalImage));
        OnPropertyChanged(nameof(ShowLocalVideo));

        if (!ShouldShowMediaLocally)
        {
            StopLocalVideo();
            CurrentMediaBitmap = null;
            return;
        }

        if (CurrentMediaKind == MediaKind.Image)
        {
            StopLocalVideo();
            try
            {
                using var stream = File.OpenRead(CurrentMediaLocalPath!);
                CurrentMediaBitmap = new Bitmap(stream);
            }
            catch
            {
                CurrentMediaBitmap = null;
            }

            return;
        }

        if (CurrentMediaKind == MediaKind.Video)
        {
            CurrentMediaBitmap = null;
            if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;

            try
            {
                MediaPlayer ??= CreateLocalMediaPlayer(libVlc);
                using var media = new Media(libVlc, CurrentMediaLocalPath!);
                MediaPlayer.Play(media);
            }
            catch
            {
                // Error display remains reserved for the media tab status (MediaErrorMessage).
            }
        }
    }

    private void StopLocalVideo()
    {
        MediaPlayer?.Stop();
    }

    /// <summary>
    ///     Resolves BeginVideoTracking/ResolvePendingVideo exactly at the actual end even
    ///     when the video is (also) running locally in the host player window, instead of relying
    ///     solely on the client report or the duration estimate as a fallback (see
    ///     the BeginVideoTracking comment). Both sources target the same mediaId value -
    ///     ResolvePendingVideo is already idempotent thanks to the _pendingVideoMediaId check,
    ///     regardless of which source fires first.
    /// </summary>
    private MediaPlayer CreateLocalMediaPlayer(LibVLC libVlc)
    {
        var player = new MediaPlayer(libVlc);
        player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_pendingVideoMediaId is { } mediaId) ResolvePendingVideo(mediaId);
        });
        return player;
    }

    private void AddTimelineItem(TimerItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(AlarmItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(IntervalEventItemViewModel vm)
    {
        AddTimelineItem(new TimelineDisplayItemViewModel(vm));
    }

    private void AddTimelineItem(TimelineDisplayItemViewModel item)
    {
        TimelineItems.Add(item);
        if (item.IsPlayerVisible) PlayerTimelineItems.Add(item);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimelineDisplayItemViewModel.IsPlayerVisible) ||
                e.PropertyName == nameof(TimelineDisplayItemViewModel.IsSlOnly))
                SyncPlayerTimelineItems();

            OnPropertyChanged(nameof(NextEventJumpLabel));
        };
    }

    /// <summary>
    ///     Sends the current state of an item as timelineItem.upserted (or as
    ///     timelineItem.removed, if it is currently not player-visible). Triggered by the
    ///     StateChanged events of the timer/alarm/interval view models - NOT on every
    ///     clock tick, but only on real discrete state changes.
    /// </summary>
    private void PublishItemState(TimerItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(AlarmItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(IntervalEventItemViewModel vm)
    {
        PublishItemState(TimelineItems.FirstOrDefault(item => item.Wraps(vm)));
    }

    private void PublishItemState(TimelineDisplayItemViewModel? item)
    {
        if (item is null) return;

        if (!item.IsPlayerVisible)
        {
            _ = _playerServer.PublishTimelineItemRemovedAsync(item.Id);
            return;
        }

        _ = _playerServer.PublishTimelineItemUpsertedAsync(item.ToRpcSnapshot());
    }

    private void SyncPlayerTimelineItems()
    {
        PlayerTimelineItems.Clear();
        foreach (var item in TimelineItems.Where(item => item.IsPlayerVisible)) PlayerTimelineItems.Add(item);
    }

    [RelayCommand]
    private void AddCalendarEntry()
    {
        var entry = new CalendarEntryViewModel(OnCalendarEntryChanged, RemoveCalendarEntry)
        {
            StartDateTimeText = CalendarSelectedDate.Date.AddHours(12)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        CalendarEntries.Add(entry);
        SelectedCalendarEntry = entry;
        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void RemoveCalendarEntry(CalendarEntryViewModel entry)
    {
        CalendarEntries.Remove(entry);
        if (ReferenceEquals(SelectedCalendarEntry, entry))
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();

        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void OnCalendarEntryChanged(CalendarEntryViewModel entry)
    {
        if (entry.TryBuildDefinition(out var definition))
        {
            SelectedCalendarEntry ??= entry;
            if (ReferenceEquals(SelectedCalendarEntry, entry) || CalendarEntries.Count == 1)
            {
                CalendarSelectedDate = definition.Start.Date;
                CalendarMonth = new DateTime(definition.Start.Year, definition.Start.Month, 1);
            }
        }

        RefreshCalendarViews();
        QueueCalendarFullResync();
    }

    private void RefreshCalendarViews()
    {
        CalendarMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        PlayerCalendarMonthLabel = CalendarMonth.ToString("MMMM yyyy", LocalizationService.Culture);
        PlayerCalendarSelectedDateLabel =
            CalendarSelectedDate.ToString("dddd, dd.MM.yyyy", LocalizationService.Culture);

        var validDefinitions = CalendarEntries
            .Select(item =>
                item.TryBuildDefinition(out var definition)
                    ? (Item: item, Definition: definition)
                    : ((CalendarEntryViewModel Item, CalendarEntryDefinition Definition)?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToList();

        CalendarEntriesForSelectedDate.Clear();
        foreach (var entry in validDefinitions
                     .Where(item => item.Definition.TryGetOccurrenceOn(CalendarSelectedDate, out _))
                     .OrderBy(item =>
                     {
                         item.Definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
                         return occurrence;
                     }))
            CalendarEntriesForSelectedDate.Add(entry.Item);

        if (SelectedCalendarEntry is not null && !CalendarEntriesForSelectedDate.Contains(SelectedCalendarEntry))
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();
        else if (SelectedCalendarEntry is null && CalendarEntriesForSelectedDate.Count > 0)
            SelectedCalendarEntry = CalendarEntriesForSelectedDate.FirstOrDefault();

        PlayerCalendarDays.Clear();
        var firstOfMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        var offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var gridStart = firstOfMonth.AddDays(-offset);
        for (var index = 0; index < 42; index++)
        {
            var day = gridStart.AddDays(index);
            var dayVm = new PlayerCalendarDayViewModel(day, SelectCalendarDate)
            {
                IsCurrentMonth = day.Month == CalendarMonth.Month,
                IsSelected = day.Date == CalendarSelectedDate.Date,
                IsToday = day.Date == _clock.CurrentTime.Date,
                HasEntries = validDefinitions.Any(item => item.Definition.TryGetOccurrenceOn(day, out _))
            };
            PlayerCalendarDays.Add(dayVm);
        }

        PlayerCalendarEntries.Clear();
        foreach (var definition in validDefinitions
                     .Select(item => item.Definition)
                     .Where(definition => definition.IsPlayerVisible)
                     .Where(definition => definition.TryGetOccurrenceOn(CalendarSelectedDate, out _))
                     .OrderBy(definition =>
                     {
                         definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
                         return occurrence;
                     }))
        {
            definition.TryGetOccurrenceOn(CalendarSelectedDate, out var occurrence);
            PlayerCalendarEntries.Add(new PlayerCalendarEntryViewModel
            {
                Title = definition.Title,
                Description = definition.Description,
                Icon = definition.Icon,
                ColorHex = definition.ColorHex,
                TimeText = occurrence.ToString("HH:mm")
            });
        }

        if (!HasPlayerVisibleCalendarEntries)
            ShowPlayerCalendarView = false;

        OnPropertyChanged(nameof(HasCalendarEntries));
        OnPropertyChanged(nameof(HasNoCalendarEntries));
        OnPropertyChanged(nameof(HasCalendarEntriesForSelectedDate));
        OnPropertyChanged(nameof(HasPlayerVisibleCalendarEntries));
        OnPropertyChanged(nameof(HasPlayerCalendarEntriesForSelectedDate));
        OnPropertyChanged(nameof(ShowPlayerTimelineView));
        OnPropertyChanged(nameof(SelectedCalendarTriggerMediaItem));
        OnPropertyChanged(nameof(SelectedCalendarTriggerSoundItem));
    }

    private void SelectCalendarDate(DateTime date)
    {
        CalendarSelectedDate = date.Date;
        if (CalendarMonth.Month != date.Month || CalendarMonth.Year != date.Year)
            CalendarMonth = new DateTime(date.Year, date.Month, 1);

        RefreshCalendarViews();
    }

    private void QueueCalendarFullResync()
    {
        _calendarSyncCts?.Cancel();
        _calendarSyncCts = new CancellationTokenSource();
        var token = _calendarSyncCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                {
                    var snapshot = await Dispatcher.UIThread.InvokeAsync(BuildSessionSnapshot);
                    await _playerServer.PublishFullResyncAsync(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void RemoveTimelineItem(Func<TimelineDisplayItemViewModel, bool> predicate)
    {
        var item = TimelineItems.FirstOrDefault(predicate);
        if (item is not null)
        {
            TimelineItems.Remove(item);
            PlayerTimelineItems.Remove(item);
        }
    }

    partial void OnSelectedThemeOptionChanged(string value)
    {
        _ambienceCurrentFileName = null;

        if (!_themesByDisplayName.TryGetValue(value, out var loaded)) return;

        ThemeService.Apply(loaded.Definition, loaded.FolderPath);
        ThemeSettingsService.SaveLastThemeId(loaded.Definition.Id);
        CurrentThemeId = loaded.Definition.Id;
        OnPropertyChanged(nameof(HasAmbienceBackgrounds));

        // Themes are pushed to clients via RPC as a plain theme id (see design-decisions.md) -
        // a client must have the same theme.json locally (bundled SampleThemes or the same
        // %AppData%/RpgTimeTracker/Themes file) to render it; without a matching local file
        // the client stays on its last active theme (see ClientMainWindowViewModel.ApplyTheme).
        _ = _playerServer.PublishThemeChangedAsync(GetCurrentThemeWireValue());
        UpdateAmbience();
    }

    /// <summary>The theme value sent over the network/embedded in session.snapshot - the theme id.</summary>
    private string GetCurrentThemeWireValue()
    {
        return _themesByDisplayName.TryGetValue(SelectedThemeOption, out var loaded) ? loaded.Definition.Id : "shadowrun";
    }

    /// <summary>
    ///     Switches WindowBackgroundImageBrush between the first and last named background
    ///     of the currently active theme, depending on whether it is currently day (06-18) or
    ///     night. A no-op as long as the active theme has only one background (this currently
    ///     applies to all bundled samples except "Medieval").
    /// </summary>
    private void UpdateAmbience()
    {
        if (!AmbienceAutomationEnabled) return;
        if (CurrentThemeId is null) return;
        if (!_themesById.TryGetValue(CurrentThemeId, out var loaded)) return;
        if (loaded.Definition.Backgrounds.Count < 2) return;

        var isDaytime = _clock.CurrentTime.Hour is >= 6 and < 18;
        var background = isDaytime ? loaded.Definition.Backgrounds[0] : loaded.Definition.Backgrounds[^1];
        if (background.FileName == _ambienceCurrentFileName) return;

        if (Application.Current is null) return;

        if (ThemeDefinitionLoader.TrySetImageBrush(Application.Current.Resources, "WindowBackgroundImageBrush",
                loaded.FolderPath, background.FileName, loaded.Definition.Id))
            _ambienceCurrentFileName = background.FileName;
    }

    partial void OnSpeedMultiplierChanged(double value)
    {
        var rounded = Math.Round(value <= 0 ? 0.1 : value, 1);
        if (Math.Abs(rounded - value) > 0.0001)
        {
            SpeedMultiplier = rounded;
            return;
        }

        _clock.SpeedMultiplier = rounded;
        var asDecimal = (decimal)rounded;
        if (SpeedMultiplierInput != asDecimal) SpeedMultiplierInput = asDecimal;
        OnPropertyChanged(nameof(SpeedMultiplierDisplay));
        OnPropertyChanged(nameof(SpeedLabel));
        _ = _playerServer.PublishClockSpeedChangedAsync(rounded);
    }

    partial void OnIsNetworkServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
        OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        OnPropertyChanged(nameof(ServerStatusSummary));
    }

    partial void OnSpeedMultiplierInputChanged(decimal value)
    {
        var rounded = Math.Round(value <= 0 ? 0.1m : value, 1);
        if (rounded != value)
        {
            SpeedMultiplierInput = rounded;
            return;
        }

        var asDouble = (double)rounded;
        if (Math.Abs(SpeedMultiplier - asDouble) > 0.0001) SpeedMultiplier = asDouble;
    }

    partial void OnPlayerHeaderTitleChanged(string value)
    {
        SaveUiSettings();
        _ = _playerServer.PublishHeaderChangedAsync(PlayerHeaderTitle, PlayerHeaderSubtitle);
    }

    partial void OnPlayerHeaderSubtitleChanged(string value)
    {
        SaveUiSettings();
        _ = _playerServer.PublishHeaderChangedAsync(PlayerHeaderTitle, PlayerHeaderSubtitle);
    }

    partial void OnHeadsUpWarningEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnHeadsUpLeadMinutesChanged(decimal value)
    {
        SaveUiSettings();
    }

    partial void OnAmbienceAutomationEnabledChanged(bool value)
    {
        SaveUiSettings();
        _ambienceCurrentFileName = null;
        UpdateAmbience();
    }

    partial void OnServerNameChanged(string value)
    {
        SaveUiSettings();
    }

    partial void OnConnectionPinChanged(string value)
    {
        SaveUiSettings();
    }

    partial void OnAutoSaveOnCloseEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnAutoLoadOnStartupEnabledChanged(bool value)
    {
        SaveUiSettings();
    }

    partial void OnSelectedLanguageOptionChanged(string value)
    {
        LocalizationService.Apply(LanguageCode(value));
        SaveUiSettings();
    }

    private static string LanguageDisplayName(string code) => code == "de" ? "Deutsch" : "English";
    private static string LanguageCode(string display) => display == "Deutsch" ? "de" : "en";

    private void SaveUiSettings()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.PlayerHeaderTitle =
            string.IsNullOrWhiteSpace(PlayerHeaderTitle)
                ? LocalizationService.Get("MainWindowViewModel.Defaults.PlayerHeaderTitle")
                : PlayerHeaderTitle;
        settings.PlayerHeaderSubtitle = PlayerHeaderSubtitle;
        settings.HeadsUpWarningEnabled = HeadsUpWarningEnabled;
        settings.HeadsUpLeadMinutes = (double)HeadsUpLeadMinutes;
        settings.SoundSeekThresholdMs = SoundSeekThresholdMs;
        settings.AmbienceAutomationEnabled = AmbienceAutomationEnabled;
        settings.ServerName = string.IsNullOrWhiteSpace(ServerName) ? "RpgTimeTracker" : ServerName;
        settings.ConnectionPin = ConnectionPin;
        settings.Language = LanguageCode(SelectedLanguageOption);
        settings.AutoSaveOnCloseEnabled = AutoSaveOnCloseEnabled;
        settings.AutoLoadOnStartupEnabled = AutoLoadOnStartupEnabled;
        ThemeSettingsService.SaveSettings(settings);
    }

}
