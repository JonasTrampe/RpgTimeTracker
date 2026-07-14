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
    // ==================== Send media (image/video) ====================

    /// <summary>
    ///     Reads the selected file, shows it locally in the player window, and distributes it
    ///     over the network to all connected player clients.
    /// </summary>
    public async Task SendMediaFromPathAsync(string sourcePath)
    {
        Log.Information("Sending ad-hoc medium: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            Log.Warning("Ad-hoc medium rejected: unsupported format ({SourcePath})", sourcePath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMediaFormat");
            return;
        }

        var prepared = await ReadAndCacheAdHocFileAsync(sourcePath, ThemeSettingsService.MediaCacheDirectory);
        if (prepared is null) return;
        var (bytes, cachedPath) = prepared.Value;

        var fileName = Path.GetFileName(sourcePath);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = kind.ToString(),
            FileName = fileName,
            MimeType = mimeType,
            Loop = SendMediaLoop,
            AddToGallery = true
        };

        // isOwnedCache: false - the cache copy is now kept for the gallery (browsable),
        // instead of being automatically deleted on the next ad-hoc send. Cleanup is handled by
        // RetractSentMediaItem when this gallery entry is explicitly removed.
        SetCurrentMediaStatus(kind, cachedPath, fileName, false);
        AddSentMediaItem(header, cachedPath, true);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.MediaSentAdHoc"), fileName));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaSent"), fileName));

        await _playerServer.PublishMediaAsync(header, bytes);
        BeginVideoTracking(header, cachedPath, false);
    }

    /// <summary>
    ///     Reads+validates an ad-hoc chosen file and creates a GUID-named working copy
    ///     in the given cache directory (shared logic for image/video and sound ad-hoc sending).
    ///     Sets MediaErrorMessage on errors and returns null.
    /// </summary>
    private async Task<(byte[] Bytes, string CachedPath)?> ReadAndCacheAdHocFileAsync(string sourcePath,
        string cacheDirectory)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                Log.Warning("Ad-hoc file rejected: not found ({SourcePath})", sourcePath);
                MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc file could not be read ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return null;
        }

        if (info.Length > TcpPlayerServerService.MaxMediaBytes)
        {
            Log.Warning("Ad-hoc file rejected: {SizeMb} MB exceeds limit ({SourcePath})",
                info.Length / (1024 * 1024), sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileTooLarge"),
                info.Length / (1024 * 1024), TcpPlayerServerService.MaxMediaBytes / (1024 * 1024));
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ad-hoc file could not be read ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return null;
        }

        string cachedPath;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            cachedPath = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
            await File.WriteAllBytesAsync(cachedPath, bytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ad-hoc file could not be cached ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeCached"), ex.Message);
            return null;
        }

        return (bytes, cachedPath);
    }

    /// <summary>
    ///     Ad-hoc sound sending (sounds tab, without adding it to the library) - runs
    ///     entirely through SendSoundAsync, see its comment.
    /// </summary>
    public async Task SendSoundFromPathAsync(string sourcePath)
    {
        Log.Information("Sending ad-hoc sound: {SourcePath}", sourcePath);

        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            Log.Warning("Ad-hoc sound rejected: unsupported format ({SourcePath})", sourcePath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedSoundFormat");
            return;
        }

        var prepared = await ReadAndCacheAdHocFileAsync(sourcePath, ThemeSettingsService.MediaCacheDirectory);
        if (prepared is null) return;
        var (bytes, cachedPath) = prepared.Value;

        var fileName = Path.GetFileName(sourcePath);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = kind.ToString(),
            Layer = MediaHeaderDto.LayerSound,
            FileName = fileName,
            MimeType = mimeType,
            Loop = SendSoundLoop,
            Volume = 100
        };

        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.SoundSentAdHoc"), fileName));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundSent"), fileName));
        await SendSoundAsync(header, bytes, cachedPath, true);
    }

    [RelayCommand]
    private void ClearMedia()
    {
        CancelPendingVideoTracking();
        _activeTriggerMediaSource = null;

        var previousPath = CurrentMediaLocalPath;
        var previousWasOwned = _currentMediaIsOwnedCache;
        CurrentMediaKind = MediaKind.None;
        CurrentMediaLocalPath = null;
        CurrentMediaFileName = null;
        MediaErrorMessage = null;
        _currentMediaIsOwnedCache = false;

        if (previousWasOwned) DeleteFileQuietly(previousPath);
        _ = _playerServer.PublishMediaClearAsync();

        // Fullscreen is deliberately NOT touched when a medium is closed/reset - this
        // previously unintentionally ended a fullscreen turned on via trigger or manually (e.g.
        // "show list permanently fullscreen to the player") just because some image/video was
        // closed. Fullscreen now only changes via the explicit fullscreen action.
    }

    /// <summary>Adds a file to the library WITHOUT displaying/sending it immediately.</summary>
    public async Task AddMediaToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind == MediaKind.Audio)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMediaFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var scope = await ResolveScopeForNewItemAsync();
            var targetDirectory = GetMediaLibraryBaseDirectory(scope);
            var cachedPath = await ContentAddressedStorage.StoreFileAsync(sourcePath, targetDirectory);

            var item = new MediaLibraryItemViewModel(
                Guid.NewGuid(), Path.GetFileNameWithoutExtension(sourcePath), cachedPath, kind, mimeType, false,
                RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged)
            {
                Scope = scope
            };
            MediaLibrary.Add(item);
            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
            MediaErrorMessage = null;
            Log.Information("Medium added to library: {Name} ({Kind}, {Scope})", item.Name, item.Kind, scope);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedToMediaLibrary"), item.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Medium could not be added to library ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MediaCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    /// <summary>Shared by every library's Move*ItemToScope method: the no-op guards (already in
    ///     targetScope; targetScope is SessionLocal but no session is open) and the try/catch/log
    ///     wrapper are identical for Media/Sound/Music/Map/Npc - only the actual file operation and
    ///     which Save*LibrarySettings to call differ, so those are the caller's moveAndSave action.</summary>
    private void MoveLibraryItemToScope(LibraryScope currentScope, LibraryScope targetScope,
        string itemLabel, string itemName, Action moveAndSave)
    {
        if (currentScope == targetScope) return;
        if (targetScope == LibraryScope.SessionLocal && !_sessionService.IsSessionOpen) return;

        try
        {
            moveAndSave();
            Log.Information("{Label} moved to {Scope}: {Name}", itemLabel, targetScope, itemName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "{Label} could not be moved to {Scope} ({Name})", itemLabel, targetScope, itemName);
        }
    }

    /// <summary>Moves a Media Library item's file between the Shared directory and the open
    ///     session's media/ folder, then flips its Scope and re-saves both stores. A no-op if
    ///     already in targetScope, or if targetScope is SessionLocal but no session is open.</summary>
    public void MoveMediaLibraryItemToScope(MediaLibraryItemViewModel item, LibraryScope targetScope) =>
        MoveLibraryItemToScope(item.Scope, targetScope, "Medium", item.Name, () =>
        {
            var targetDirectory = GetMediaLibraryBaseDirectory(targetScope);
            Directory.CreateDirectory(targetDirectory);
            var newPath = Path.Combine(targetDirectory, Path.GetFileName(item.LocalPath));
            File.Move(item.LocalPath, newPath);
            item.UpdateLocalPath(newPath);
            item.Scope = targetScope;
            SaveMediaLibrarySettings();
        });

    /// <summary>Thin single-argument wrappers around MoveMediaLibraryItemToScope so the tile's
    ///     "move" buttons can bind a plain ICommand with CommandParameter="{Binding}" (the item),
    ///     rather than needing a two-argument command.</summary>
    [RelayCommand]
    private void MoveMediaLibraryItemToShared(MediaLibraryItemViewModel item) =>
        MoveMediaLibraryItemToScope(item, LibraryScope.Shared);

    [RelayCommand]
    private void MoveMediaLibraryItemToSession(MediaLibraryItemViewModel item) =>
        MoveMediaLibraryItemToScope(item, LibraryScope.SessionLocal);

    /// <summary>
    ///     Set by the view (see MainWindow.axaml.cs), since a warning/confirmation needs a window,
    ///     which the view model itself doesn't know about - analogous to the LibraryPanelView funcs
    ///     above. Takes just the item's display name (not a concrete MediaLibraryItemViewModel) so
    ///     Sound/Music deletion can reuse the exact same dialog. Without a handler (e.g. during
    ///     testing) an in-use item is NEVER silently deleted.
    /// </summary>
    public Func<string, IReadOnlyList<string>, Task<TriggerMediaDeleteChoice>>?
        ConfirmTriggerMediaDeleteAsync { get; set; }

    private void RemoveMediaLibraryItem(MediaLibraryItemViewModel item)
    {
        _ = RemoveMediaLibraryItemAsync(item);
    }

    private async Task RemoveMediaLibraryItemAsync(MediaLibraryItemViewModel item)
    {
        var usedBy = _usageRegistry.FindUsagesOf(item.Id);
        if (usedBy.Count > 0)
        {
            var choice = ConfirmTriggerMediaDeleteAsync is null
                ? TriggerMediaDeleteChoice.Cancel
                : await ConfirmTriggerMediaDeleteAsync(item.Name, usedBy).ConfigureAwait(true);

            switch (choice)
            {
                case TriggerMediaDeleteChoice.Cancel:
                    return;
                case TriggerMediaDeleteChoice.RemoveFromItemsAndDelete:
                    _usageRegistry.ClearReferencesTo(item.Id);
                    break;
                case TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly:
                    // File deliberately stays on disk - the affected items still reference
                    // it directly (see the TriggerMediaConfig comment: no own copy
                    // mechanism), only the library entry disappears.
                    MediaLibrary.Remove(item);
                    OnPropertyChanged(nameof(HasNoMediaLibraryItems));
                    SaveMediaLibrarySettings();
                    RefreshCalendarViews();
                    Log.Information(
                        "Medium removed from library only, file kept (still used by {Count} items): {Name}",
                        usedBy.Count, item.Name);
                    return;
            }
        }

        MediaLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoMediaLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        SaveMediaLibrarySettings();
        RefreshCalendarViews();
        Log.Information("Medium removed from library: {Name}", item.Name);
    }

    /// <summary>
    ///     Checks whether localPath is currently assigned as an event medium - across all timers/alarms/
    ///     intervals as well as calendar entries, exclusively via TriggerMedia.Path (NOT via
    ///     the item's name, which is freely changeable and has nothing to do with the file's identity).
    ///     Returns a display label per location found (for the warning), no object references.
    /// </summary>
    private List<string> FindTriggerMediaUsageLabels(string localPath)
    {
        var labels = new List<string>();

        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        labels.AddRange(Timers.Where(t => Matches(t.TriggerMedia))
            .Select(t => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.TimerUsage"), t.Name)));
        labels.AddRange(Alarms.Where(a => Matches(a.TriggerMedia))
            .Select(a => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.AlarmUsage"), a.Name)));
        labels.AddRange(IntervalEvents.Where(i => Matches(i.TriggerMedia))
            .Select(i => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.IntervalUsage"), i.Name)));
        labels.AddRange(CalendarEntries.Where(c => Matches(c.TriggerMedia))
            .Select(c => string.Format(LocalizationService.Get("MainWindowViewModel.Labels.CalendarEntryUsage"), c.Title)));

        return labels;
    }

    /// <summary>Resets TriggerMedia on all items that currently have localPath assigned (see FindTriggerMediaUsageLabels).</summary>
    private void ClearTriggerMediaReferences(string localPath)
    {
        bool Matches(TriggerMediaConfig config) =>
            !string.IsNullOrEmpty(config.Path) && string.Equals(config.Path, localPath, StringComparison.OrdinalIgnoreCase);

        foreach (var t in Timers.Where(t => Matches(t.TriggerMedia))) t.TriggerMedia.ClearCommand.Execute(null);
        foreach (var a in Alarms.Where(a => Matches(a.TriggerMedia))) a.TriggerMedia.ClearCommand.Execute(null);
        foreach (var i in IntervalEvents.Where(i => Matches(i.TriggerMedia))) i.TriggerMedia.ClearCommand.Execute(null);
        foreach (var c in CalendarEntries.Where(c => Matches(c.TriggerMedia))) c.TriggerMedia.ClearCommand.Execute(null);
    }

    /// <summary>Adds an audio file to the sound library WITHOUT sending it immediately.</summary>
    public async Task AddSoundToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedSoundFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var scope = await ResolveScopeForNewItemAsync();
            var targetDirectory = GetSoundLibraryBaseDirectory(scope);
            var cachedPath = await ContentAddressedStorage.StoreFileAsync(sourcePath, targetDirectory);
            var item = CreateSoundLibraryItem(Guid.NewGuid(), Path.GetFileNameWithoutExtension(sourcePath), VisualItemHelper.IconTimer,
                cachedPath, mimeType, false, 100);
            item.Scope = scope;
            SoundLibrary.Add(item);
            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
            MediaErrorMessage = null;
            Log.Information("Sound added to library: {Name} ({Scope})", item.Name, scope);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedToSoundLibrary"), item.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound could not be added to library ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.SoundCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    /// <summary>See MoveMediaLibraryItemToScope's doc comment - same move-file/flip-Scope/re-save
    ///     pattern for the Sound Library.</summary>
    public void MoveSoundLibraryItemToScope(SoundLibraryItemViewModel item, LibraryScope targetScope) =>
        MoveLibraryItemToScope(item.Scope, targetScope, "Sound", item.Name, () =>
        {
            var targetDirectory = GetSoundLibraryBaseDirectory(targetScope);
            Directory.CreateDirectory(targetDirectory);
            var newPath = Path.Combine(targetDirectory, Path.GetFileName(item.LocalPath));
            File.Move(item.LocalPath, newPath);
            item.UpdateLocalPath(newPath);
            item.Scope = targetScope;
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
        });

    /// <summary>See MoveMediaLibraryItemToShared/ToSession's doc comment - same wrapping for Sound.</summary>
    [RelayCommand]
    private void MoveSoundLibraryItemToShared(SoundLibraryItemViewModel item) =>
        MoveSoundLibraryItemToScope(item, LibraryScope.Shared);

    [RelayCommand]
    private void MoveSoundLibraryItemToSession(SoundLibraryItemViewModel item) =>
        MoveSoundLibraryItemToScope(item, LibraryScope.SessionLocal);

    private SoundLibraryItemViewModel CreateSoundLibraryItem(
        Guid id, string name, string icon, string localPath, string mimeType, bool loop, int volume,
        int repeatCount = 1, double trimStartSeconds = 0, double trimEndSeconds = 0)
    {
        return new SoundLibraryItemViewModel(id, name, icon, localPath, mimeType, loop, volume, repeatCount,
            trimStartSeconds, trimEndSeconds,
            RemoveSoundLibraryItem,
            PlaySoundLibraryItem,
            TestSoundLibraryItem,
            OnSoundLibraryItemChanged);
    }

    private void RemoveSoundLibraryItem(SoundLibraryItemViewModel item)
    {
        _ = RemoveSoundLibraryItemAsync(item);
    }

    private async Task RemoveSoundLibraryItemAsync(SoundLibraryItemViewModel item)
    {
        var usedBy = _usageRegistry.FindUsagesOf(item.Id);
        if (usedBy.Count > 0)
        {
            var choice = ConfirmTriggerMediaDeleteAsync is null
                ? TriggerMediaDeleteChoice.Cancel
                : await ConfirmTriggerMediaDeleteAsync(item.Name, usedBy).ConfigureAwait(true);

            switch (choice)
            {
                case TriggerMediaDeleteChoice.Cancel:
                    return;
                case TriggerMediaDeleteChoice.RemoveFromItemsAndDelete:
                    _usageRegistry.ClearReferencesTo(item.Id);
                    break;
                case TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly:
                    // File deliberately stays on disk - see RemoveMediaLibraryItemAsync's identical comment.
                    SoundLibrary.Remove(item);
                    OnPropertyChanged(nameof(HasNoSoundLibraryItems));
                    SyncSoundServiceLibrary();
                    SaveSoundLibrarySettings();
                    RefreshCalendarViews();
                    Log.Information(
                        "Sound removed from library only, file kept (still used by {Count} items): {Name}",
                        usedBy.Count, item.Name);
                    return;
            }
        }

        SoundLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoSoundLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        SyncSoundServiceLibrary();
        SaveSoundLibrarySettings();
        RefreshCalendarViews();
        Log.Information("Sound removed from library: {Name}", item.Name);
    }

    /// <summary>Single-click handler: immediately sends this library sound to all players (see SendSoundAsync).</summary>
    private async void PlaySoundLibraryItem(SoundLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Library sound not found: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.LibrarySoundNotFound");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Library sound could not be read: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return;
        }

        Log.Information("Sending library sound: {Name}", item.Name);

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = nameof(MediaKind.Audio),
            Layer = MediaHeaderDto.LayerSound,
            FileName = item.Name,
            MimeType = item.MimeType,
            Loop = item.Loop,
            Volume = item.Volume,
            RepeatCount = item.RepeatCount,
            TrimStartMs = (long)(item.TrimStartSeconds * 1000),
            TrimEndMs = (long)(item.TrimEndSeconds * 1000)
        };

        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.SoundSentLibrary"), item.Name));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundSent"), item.Name));
        await SendSoundAsync(header, bytes, item.LocalPath, false);
    }

    /// <summary>
    ///     "Test" button: plays the sound ONLY locally at the GM (never sent, no
    ///     ActivePlayingSounds entry) - to check the configured volume beforehand.
    /// </summary>
    private void TestSoundLibraryItem(SoundLibraryItemViewModel item)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;
        if (!File.Exists(item.LocalPath)) return;

        try
        {
            _testSoundPlayer?.Stop();
            _testSoundPlayer?.Dispose();

            var player = new MediaPlayer(libVlc);
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_testSoundPlayer, player)) return;
                player.Stop();
                player.Dispose();
                _testSoundPlayer = null;
            });
            _testSoundPlayer = player;

            using var media = new Media(libVlc, item.LocalPath);
            VlcMediaService.ApplySoundTrim(media, (long)(item.TrimStartSeconds * 1000),
                (long)(item.TrimEndSeconds * 1000));
            player.Play(media);
            player.Volume = Math.Clamp(item.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound test failed: {Name}", item.Name);
        }
    }

    private void OnSoundLibraryItemChanged(SoundLibraryItemViewModel item)
    {
        SyncSoundServiceLibrary();
        SaveSoundLibrarySettings();
        RefreshCalendarViews();
    }

    private void SyncSoundServiceLibrary()
    {
        SoundService.SyncLibrarySounds(SoundLibrary.Select(s => (s.Name, s.LocalPath)));
    }

    private static SoundLibraryEntryDto ToSoundLibraryEntryDto(SoundLibraryItemViewModel s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Icon = s.Icon,
        Path = s.LocalPath,
        MimeType = s.MimeType,
        Loop = s.Loop,
        Volume = s.Volume,
        RepeatCount = s.RepeatCount,
        TrimStartMs = (long)(s.TrimStartSeconds * 1000),
        TrimEndMs = (long)(s.TrimEndSeconds * 1000)
    };

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    private void SaveSoundLibrarySettings() =>
        SaveLibrarySettings(SoundLibrary, s => s.Scope, ToSoundLibraryEntryDto,
            (settings, list) => settings.SoundLibrary = list,
            (sessionLibrary, list) => sessionLibrary.SoundLibrary = list);

    public async Task ExportMediaLibraryAsync(string targetFolder)
    {
        try
        {
            var manifest = new LibraryManifestDto { LibraryType = "Media" };
            foreach (var item in MediaLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                File.Copy(item.LocalPath, Path.Combine(targetFolder, fileName), true);
                manifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name,
                    FileName = fileName,
                    MimeType = item.MimeType,
                    Kind = item.Kind.ToString(),
                    Loop = item.Loop
                });
            }

            await File.WriteAllTextAsync(Path.Combine(targetFolder, LibraryManifestFileName),
                JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
            Log.Information("Media library exported to {Folder} ({Count} files)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaLibraryExported"), manifest.Items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Media library export failed ({Folder})", targetFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    public async Task ImportMediaLibraryAsync(string sourceFolder)
    {
        try
        {
            var manifest = await ReadLibraryManifestAsync(sourceFolder);
            if (manifest is null) return;

            Directory.CreateDirectory(ThemeSettingsService.MediaLibraryDirectory);
            var imported = 0;
            foreach (var entry in manifest.Items)
            {
                var sourceFile = Path.Combine(sourceFolder, entry.FileName);
                if (!File.Exists(sourceFile)) continue;
                if (!Enum.TryParse<MediaKind>(entry.Kind, out var kind) || kind == MediaKind.Audio) continue;

                var cachedPath = Path.Combine(ThemeSettingsService.MediaLibraryDirectory,
                    $"{Guid.NewGuid():N}{Path.GetExtension(entry.FileName)}");
                File.Copy(sourceFile, cachedPath, true);
                MediaLibrary.Add(new MediaLibraryItemViewModel(
                    Guid.NewGuid(), entry.Name, cachedPath, kind, entry.MimeType, entry.Loop,
                    RemoveMediaLibraryItem, ShowMediaLibraryItem, OnMediaLibraryItemChanged));
                imported++;
            }

            OnPropertyChanged(nameof(HasNoMediaLibraryItems));
            SaveMediaLibrarySettings();
            Log.Information("Media library imported from {Folder} ({Count} files)", sourceFolder, imported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaImported"), imported));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Media library import failed ({Folder})", sourceFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    public async Task ExportSoundLibraryAsync(string targetFolder)
    {
        try
        {
            var manifest = new LibraryManifestDto { LibraryType = "Sound" };
            foreach (var item in SoundLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                File.Copy(item.LocalPath, Path.Combine(targetFolder, fileName), true);
                manifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name,
                    Icon = item.Icon,
                    FileName = fileName,
                    MimeType = item.MimeType,
                    Loop = item.Loop,
                    Volume = item.Volume,
                    RepeatCount = item.RepeatCount,
                    TrimStartMs = (long)(item.TrimStartSeconds * 1000),
                    TrimEndMs = (long)(item.TrimEndSeconds * 1000)
                });
            }

            await File.WriteAllTextAsync(Path.Combine(targetFolder, LibraryManifestFileName),
                JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
            Log.Information("Sound library exported to {Folder} ({Count} files)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundLibraryExported"), manifest.Items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound library export failed ({Folder})", targetFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    public async Task ImportSoundLibraryAsync(string sourceFolder)
    {
        try
        {
            var manifest = await ReadLibraryManifestAsync(sourceFolder);
            if (manifest is null) return;

            Directory.CreateDirectory(ThemeSettingsService.SoundLibraryDirectory);
            var imported = 0;
            foreach (var entry in manifest.Items)
            {
                var sourceFile = Path.Combine(sourceFolder, entry.FileName);
                if (!File.Exists(sourceFile)) continue;

                var cachedPath = Path.Combine(ThemeSettingsService.SoundLibraryDirectory,
                    $"{Guid.NewGuid():N}{Path.GetExtension(entry.FileName)}");
                File.Copy(sourceFile, cachedPath, true);
                SoundLibrary.Add(CreateSoundLibraryItem(Guid.NewGuid(), entry.Name, entry.Icon ?? VisualItemHelper.IconTimer,
                    cachedPath,
                    entry.MimeType, entry.Loop, entry.Volume ?? 100,
                    entry.RepeatCount ?? 1, entry.TrimStartMs / 1000.0, entry.TrimEndMs / 1000.0));
                imported++;
            }

            OnPropertyChanged(nameof(HasNoSoundLibraryItems));
            SyncSoundServiceLibrary();
            SaveSoundLibrarySettings();
            Log.Information("Sound library imported from {Folder} ({Count} files)", sourceFolder, imported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.SoundsImported"), imported));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound library import failed ({Folder})", sourceFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    public async Task AddMusicToLibraryFromPathAsync(string sourcePath)
    {
        if (!MediaTypeHelper.TryGetKind(sourcePath, out var kind, out var mimeType) || kind != MediaKind.Audio)
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.UnsupportedMusicFormat");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.FileNotFound");
            return;
        }

        try
        {
            var scope = await ResolveScopeForNewItemAsync();
            var targetDirectory = GetMusicLibraryBaseDirectory(scope);
            var cachedPath = await ContentAddressedStorage.StoreFileAsync(sourcePath, targetDirectory);
            var item = CreateMusicLibraryItem(Guid.NewGuid(), Path.GetFileNameWithoutExtension(sourcePath),
                VisualItemHelper.IconTimer, cachedPath, mimeType, 100);
            item.Scope = scope;
            MusicLibrary.Add(item);
            OnPropertyChanged(nameof(HasNoMusicLibraryItems));
            SaveMusicLibrarySettings();
            MediaErrorMessage = null;
            Log.Information("Music track added to library: {Name} ({Scope})", item.Name, scope);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.AddedToMusicLibrary"), item.Name));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music track could not be added to library ({SourcePath})", sourcePath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.MusicCouldNotBeAddedToLibrary"), ex.Message);
        }
    }

    /// <summary>See MoveMediaLibraryItemToScope's doc comment - same move-file/flip-Scope/re-save
    ///     pattern for the Music Library.</summary>
    public void MoveMusicLibraryItemToScope(MusicLibraryItemViewModel item, LibraryScope targetScope) =>
        MoveLibraryItemToScope(item.Scope, targetScope, "Music track", item.Name, () =>
        {
            var targetDirectory = GetMusicLibraryBaseDirectory(targetScope);
            Directory.CreateDirectory(targetDirectory);
            var newPath = Path.Combine(targetDirectory, Path.GetFileName(item.LocalPath));
            File.Move(item.LocalPath, newPath);
            item.UpdateLocalPath(newPath);
            item.Scope = targetScope;
            SaveMusicLibrarySettings();
        });

    /// <summary>See MoveMediaLibraryItemToShared/ToSession's doc comment - same wrapping for Music.</summary>
    [RelayCommand]
    private void MoveMusicLibraryItemToShared(MusicLibraryItemViewModel item) =>
        MoveMusicLibraryItemToScope(item, LibraryScope.Shared);

    [RelayCommand]
    private void MoveMusicLibraryItemToSession(MusicLibraryItemViewModel item) =>
        MoveMusicLibraryItemToScope(item, LibraryScope.SessionLocal);

    private MusicLibraryItemViewModel CreateMusicLibraryItem(
        Guid id, string name, string icon, string localPath, string mimeType, int volume)
    {
        return new MusicLibraryItemViewModel(id, name, icon, localPath, mimeType, volume,
            RemoveMusicLibraryItem,
            PlayMusicLibraryItem,
            TestMusicLibraryItem,
            OnMusicLibraryItemChanged);
    }

    private void RemoveMusicLibraryItem(MusicLibraryItemViewModel item)
    {
        _ = RemoveMusicLibraryItemAsync(item);
    }

    private async Task RemoveMusicLibraryItemAsync(MusicLibraryItemViewModel item)
    {
        var usedBy = _usageRegistry.FindUsagesOf(item.Id);
        if (usedBy.Count > 0)
        {
            var choice = ConfirmTriggerMediaDeleteAsync is null
                ? TriggerMediaDeleteChoice.Cancel
                : await ConfirmTriggerMediaDeleteAsync(item.Name, usedBy).ConfigureAwait(true);

            switch (choice)
            {
                case TriggerMediaDeleteChoice.Cancel:
                    return;
                case TriggerMediaDeleteChoice.RemoveFromItemsAndDelete:
                    _usageRegistry.ClearReferencesTo(item.Id);
                    break;
                case TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly:
                    // File deliberately stays on disk - see RemoveMediaLibraryItemAsync's identical comment.
                    if (CurrentPlaylistTrack == item) StopPlaylistPlayback();
                    MusicLibrary.Remove(item);
                    OnPropertyChanged(nameof(HasNoMusicLibraryItems));
                    SaveMusicLibrarySettings();
                    Log.Information(
                        "Music track removed from library only, file kept (still used by {Count} items): {Name}",
                        usedBy.Count, item.Name);
                    return;
            }
        }

        // Stop rather than try to skip past it mid-deletion - simpler and safer than juggling
        // playback indices while the very file/track object the sequencer is playing disappears.
        if (CurrentPlaylistTrack == item) StopPlaylistPlayback();

        MusicLibrary.Remove(item);
        OnPropertyChanged(nameof(HasNoMusicLibraryItems));
        DeleteFileQuietly(item.LocalPath);
        foreach (var playlist in Playlists) playlist.RemoveTracksReferencing(item);
        SaveMusicLibrarySettings();
        Log.Information("Music track removed from library: {Name}", item.Name);
    }

    /// <summary>
    ///     "Play" button on a single Music Library tile: still a local-only preview, same as
    ///     Test - real network distribution only happens through a Playlist (see PlayPlaylist),
    ///     since sending an ad-hoc single track outside any playlist has no sequencing/routing
    ///     context to advance from. Kept as a separate command from Test for UI clarity.
    /// </summary>
    private void PlayMusicLibraryItem(MusicLibraryItemViewModel item)
    {
        PlayMusicLocalPreview(item);
    }

    /// <summary>"Test" button: plays the track ONLY locally at the GM, to check the configured volume.</summary>
    private void TestMusicLibraryItem(MusicLibraryItemViewModel item)
    {
        PlayMusicLocalPreview(item);
    }

    private void PlayMusicLocalPreview(MusicLibraryItemViewModel item)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;
        if (!File.Exists(item.LocalPath)) return;

        try
        {
            _testMusicPlayer?.Stop();
            _testMusicPlayer?.Dispose();

            var player = new MediaPlayer(libVlc);
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_testMusicPlayer, player)) return;
                player.Stop();
                player.Dispose();
                _testMusicPlayer = null;
            });
            _testMusicPlayer = player;

            using var media = new Media(libVlc, item.LocalPath);
            player.Play(media);
            player.Volume = Math.Clamp(item.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music track preview failed: {Name}", item.Name);
        }
    }

    /// <summary>Starts a playlist playing (to all connected players and the Host's own local
    ///     preview, if the player window is open) - the actual "start" action the GM asked for.</summary>
    [RelayCommand]
    private async Task PlayPlaylistAsync(PlaylistViewModel playlist)
    {
        StopPlaylistPlayback();

        if (playlist.Tracks.Count == 0) return;

        CurrentPlaylist = playlist;
        BuildPlaybackOrder(playlist);
        _playbackPosition = 0;
        IsPlaylistPlaying = true;
        await PlayCurrentPlaylistTrackAsync();
    }

    [RelayCommand]
    private void StopPlaylist()
    {
        StopPlaylistPlayback();
    }

    [RelayCommand]
    private async Task NextPlaylistTrackAsync()
    {
        if (!IsPlaylistPlaying) return;

        await AdvancePlaylistAsync();
    }

    [RelayCommand]
    private async Task PreviousPlaylistTrackAsync()
    {
        if (!IsPlaylistPlaying || _playbackOrder.Count == 0) return;

        _playbackPosition = (_playbackPosition - 1 + _playbackOrder.Count) % _playbackOrder.Count;
        await PlayCurrentPlaylistTrackAsync();
    }

    /// <summary>Builds the order tracks play in - the list order itself, or a one-time shuffle of
    ///     it if CurrentPlaylist.Shuffle is set (re-shuffled every time the playlist (re)starts,
    ///     not live while playing, so the current+next track stay predictable mid-playback).</summary>
    private void BuildPlaybackOrder(PlaylistViewModel playlist)
    {
        var indices = Enumerable.Range(0, playlist.Tracks.Count).ToList();
        if (playlist.Shuffle)
            for (var i = indices.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

        _playbackOrder = indices;
    }

    private async Task PlayCurrentPlaylistTrackAsync()
    {
        if (CurrentPlaylist is null || _playbackPosition < 0 || _playbackPosition >= _playbackOrder.Count)
        {
            StopPlaylistPlayback();
            return;
        }

        var trackIndex = _playbackOrder[_playbackPosition];
        if (trackIndex >= CurrentPlaylist.Tracks.Count)
        {
            // The library shrank (a track was deleted) since BuildPlaybackOrder ran - rebuild
            // against the current track count rather than risk an out-of-range index.
            BuildPlaybackOrder(CurrentPlaylist);
            if (_playbackOrder.Count == 0)
            {
                StopPlaylistPlayback();
                return;
            }

            _playbackPosition %= _playbackOrder.Count;
            trackIndex = _playbackOrder[_playbackPosition];
        }

        var track = CurrentPlaylist.Tracks[trackIndex].Track;
        CurrentPlaylistTrack = track;
        CurrentPlaylistVolume = track.Volume;

        if (!File.Exists(track.LocalPath))
        {
            Log.Warning("Playlist track file missing, skipping: {Name} ({Path})", track.Name, track.LocalPath);
            await AdvancePlaylistAsync();
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(track.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Playlist track could not be read, skipping: {Name} ({Path})", track.Name, track.LocalPath);
            await AdvancePlaylistAsync();
            return;
        }

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = MediaHeaderDto.MediaKindAudio,
            Layer = MediaHeaderDto.LayerMusic,
            FileName = track.Name,
            MimeType = track.MimeType,
            Volume = track.Volume
        };

        Log.Information("Playlist {PlaylistName} playing track {TrackName} ({Position}/{Count})",
            CurrentPlaylist.Name, track.Name, _playbackPosition + 1, _playbackOrder.Count);
        _currentMusicPlayback = (header, track.LocalPath, DateTime.UtcNow);
        await _playerServer.PublishMusicTrackAsync(header, bytes);
        PlayLocalMusicIfNeeded(header, track.LocalPath);
        BeginMusicTracking(header, track.LocalPath);
    }

    /// <summary>Advances to the next track in _playbackOrder, wrapping around (and reshuffling,
    ///     if Shuffle is on) when CurrentPlaylist.LoopPlaylist is set, otherwise stopping.</summary>
    private async Task AdvancePlaylistAsync()
    {
        if (CurrentPlaylist is null) return;

        _playbackPosition++;
        if (_playbackPosition >= _playbackOrder.Count)
        {
            if (!CurrentPlaylist.LoopPlaylist)
            {
                StopPlaylistPlayback();
                return;
            }

            BuildPlaybackOrder(CurrentPlaylist);
            _playbackPosition = 0;
        }

        await PlayCurrentPlaylistTrackAsync();
    }

    /// <summary>Stops playback entirely: cancels pending-track tracking, stops the Host's own
    ///     local preview player, tells all clients to stop, and clears the Now Playing state.</summary>
    private void StopPlaylistPlayback()
    {
        CancelPendingMusicTracking();

        _localMusicPlayer?.Stop();
        _localMusicPlayer?.Dispose();
        _localMusicPlayer = null;
        _currentMusicPlayback = null;

        if (IsPlaylistPlaying) _ = _playerServer.PublishMusicStopAsync();

        CurrentPlaylist = null;
        CurrentPlaylistTrack = null;
        IsPlaylistPlaying = false;
        _playbackOrder = [];
        _playbackPosition = 0;
    }

    /// <summary>
    ///     Plays the current playlist track locally too, whenever the player window is open -
    ///     same rule as PlayLocalSoundIfNeeded/ShouldShowMediaLocally, so the GM hears exactly
    ///     what players hear regardless of connected client count. seekToMs is only non-zero when
    ///     resuming after PlayMusicLocally was re-enabled mid-track (see OnPlayMusicLocallyChanged) -
    ///     a fresh track start never needs one.
    /// </summary>
    private void PlayLocalMusicIfNeeded(MediaHeaderDto header, string localPath, long seekToMs = 0)
    {
        if (!IsPlayerWindowOpen || !PlayMusicLocally) return;
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return;

        try
        {
            var player = new MediaPlayer(libVlc);
            var mediaId = header.MediaId;
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => ResolvePendingMusicTrack(mediaId));

            _localMusicPlayer = player;

            using var media = new Media(libVlc, localPath);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);

            // Estimated resume position (see PlayMusicLocally re-enable) - VLC needs the media to
            // actually start playing before a seek sticks, hence the one-shot handler instead of
            // setting player.Time immediately after Play() (same approach as the client's
            // ClientMainWindowViewModel.PlayMusicTrack/PlaySound for the analogous network case).
            if (seekToMs > 0)
            {
                EventHandler<MediaPlayerTimeChangedEventArgs>? onFirstTimeChanged = null;
                onFirstTimeChanged = (_, _) =>
                {
                    player.TimeChanged -= onFirstTimeChanged;
                    Dispatcher.UIThread.Post(() => player.Time = seekToMs);
                };
                player.TimeChanged += onFirstTimeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music track could not be played locally: {Path}", localPath);
        }
    }

    /// <summary>
    ///     Starts tracking when the currently playing track (mediaId) has ended, so the sequencer
    ///     can advance - mirrors BeginVideoTracking exactly: whichever of {a client's
    ///     music.trackEnded report, the Host's own local-preview EndReached, a duration-estimate
    ///     fallback timeout} fires first wins, via the same idempotent ResolvePendingMusicTrack.
    /// </summary>
    private void BeginMusicTracking(MediaHeaderDto header, string localPath)
    {
        CancelPendingMusicTracking();
        _pendingMusicMediaId = header.MediaId;

        var cts = new CancellationTokenSource();
        _pendingMusicFallbackCts = cts;
        _ = RunMusicFallbackTimeoutAsync(header.MediaId, localPath, cts.Token);
    }

    private void CancelPendingMusicTracking()
    {
        _pendingMusicFallbackCts?.Cancel();
        _pendingMusicFallbackCts?.Dispose();
        _pendingMusicFallbackCts = null;
        _pendingMusicMediaId = null;
    }

    /// <summary>Safety net: if no client (and no local preview) reports the track ending, don't
    ///     stay stuck on it forever - e.g. no client connected and the player window closed.</summary>
    private async Task RunMusicFallbackTimeoutAsync(string mediaId, string localPath, CancellationToken token)
    {
        var duration = await TryGetMediaDurationAsync(localPath) ?? TimeSpan.FromMinutes(10);
        var fallback = duration + TimeSpan.FromSeconds(15);

        try
        {
            await Task.Delay(fallback, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ResolvePendingMusicTrack(mediaId);
    }

    private void ResolvePendingMusicTrack(string mediaId)
    {
        if (_pendingMusicMediaId != mediaId) return;

        CancelPendingMusicTracking();
        _ = AdvancePlaylistAsync();
    }

    private void OnMusicLibraryItemChanged(MusicLibraryItemViewModel item)
    {
        SaveMusicLibrarySettings();
    }

    private static MusicLibraryEntryDto ToMusicLibraryEntryDto(MusicLibraryItemViewModel m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Icon = m.Icon,
        Path = m.LocalPath,
        MimeType = m.MimeType,
        Volume = m.Volume
    };

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    private void SaveMusicLibrarySettings() =>
        SaveLibrarySettings(MusicLibrary, m => m.Scope, ToMusicLibraryEntryDto,
            (settings, list) => settings.MusicLibrary = list,
            (sessionLibrary, list) => sessionLibrary.MusicLibrary = list);

    public async Task ExportMusicLibraryAsync(string targetFolder)
    {
        try
        {
            var manifest = new LibraryManifestDto { LibraryType = "Music" };
            foreach (var item in MusicLibrary)
            {
                if (!File.Exists(item.LocalPath)) continue;

                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(item.LocalPath)}";
                File.Copy(item.LocalPath, Path.Combine(targetFolder, fileName), true);
                manifest.Items.Add(new LibraryManifestEntryDto
                {
                    Name = item.Name,
                    Icon = item.Icon,
                    FileName = fileName,
                    MimeType = item.MimeType,
                    Volume = item.Volume
                });
            }

            await File.WriteAllTextAsync(Path.Combine(targetFolder, LibraryManifestFileName),
                JsonSerializer.Serialize(manifest, LibraryManifestJsonOptions));
            Log.Information("Music library exported to {Folder} ({Count} files)", targetFolder,
                manifest.Items.Count);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MusicLibraryExported"), manifest.Items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music library export failed ({Folder})", targetFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    public async Task ImportMusicLibraryAsync(string sourceFolder)
    {
        try
        {
            var manifest = await ReadLibraryManifestAsync(sourceFolder);
            if (manifest is null) return;

            Directory.CreateDirectory(ThemeSettingsService.MusicLibraryDirectory);
            var imported = 0;
            foreach (var entry in manifest.Items)
            {
                var sourceFile = Path.Combine(sourceFolder, entry.FileName);
                if (!File.Exists(sourceFile)) continue;

                var cachedPath = Path.Combine(ThemeSettingsService.MusicLibraryDirectory,
                    $"{Guid.NewGuid():N}{Path.GetExtension(entry.FileName)}");
                File.Copy(sourceFile, cachedPath, true);
                MusicLibrary.Add(CreateMusicLibraryItem(Guid.NewGuid(), entry.Name,
                    entry.Icon ?? VisualItemHelper.IconTimer, cachedPath, entry.MimeType, entry.Volume ?? 100));
                imported++;
            }

            OnPropertyChanged(nameof(HasNoMusicLibraryItems));
            SaveMusicLibrarySettings();
            Log.Information("Music library imported from {Folder} ({Count} files)", sourceFolder, imported);
            ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MusicImported"), imported));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Music library import failed ({Folder})", sourceFolder);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    private async Task<LibraryManifestDto?> ReadLibraryManifestAsync(string sourceFolder)
    {
        var manifestPath = Path.Combine(sourceFolder, LibraryManifestFileName);
        if (!File.Exists(manifestPath))
        {
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoValidLibraryExportFound");
            return null;
        }

        var manifest = JsonSerializer.Deserialize<LibraryManifestDto>(await File.ReadAllTextAsync(manifestPath));
        if (manifest is null) MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.ManifestCouldNotBeRead");
        return manifest;
    }

    /// <summary>Double-click handler: immediately shows and sends a medium already stored in the library.</summary>
    private async void ShowMediaLibraryItem(MediaLibraryItemViewModel item)
    {
        if (!File.Exists(item.LocalPath))
        {
            Log.Warning("Library medium not found: {Name} ({LocalPath})", item.Name, item.LocalPath);
            MediaErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.LibraryMediumNotFound");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(item.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Library medium could not be read: {Name} ({LocalPath})", item.Name,
                item.LocalPath);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.FileCouldNotBeRead"), ex.Message);
            return;
        }

        Log.Information("Sending library medium: {Name} ({Kind})", item.Name, item.Kind);

        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = item.Kind.ToString(),
            FileName = item.Name,
            MimeType = item.MimeType,
            Loop = item.Loop,
            AddToGallery = true
        };

        // isOwnedCache: false - this is the persistent library file, which must not be
        // deleted like a one-off cache on the next media change.
        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        AddSentMediaItem(header, item.LocalPath, false);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.MediaSentLibrary"), item.Name));
        ShowActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MediaSent"), item.Name));

        await _playerServer.PublishMediaAsync(header, bytes);
        BeginVideoTracking(header, item.LocalPath, false);
    }

    private void OnMediaLibraryItemChanged(MediaLibraryItemViewModel item)
    {
        if (string.Equals(CurrentMediaLocalPath, item.LocalPath, StringComparison.OrdinalIgnoreCase))
            CurrentMediaFileName = item.Name;
        SaveMediaLibrarySettings();
        RefreshCalendarViews();
    }

    private static MediaLibraryEntryDto ToMediaLibraryEntryDto(MediaLibraryItemViewModel i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Path = i.LocalPath,
        Kind = i.Kind.ToString(),
        MimeType = i.MimeType,
        Loop = i.Loop
    };

    /// <summary>Splits MediaLibrary by Scope: Shared items round-trip through the always-present
    ///     ThemeSettingsService store, exactly as before Sessions existed; SessionLocal items
    ///     (only possible while a session is open) round-trip through that session's own
    ///     session-library.json instead, leaving the other session-local library sections in that
    ///     file untouched.</summary>
    private void SaveMediaLibrarySettings() =>
        SaveLibrarySettings(MediaLibrary, i => i.Scope, ToMediaLibraryEntryDto,
            (settings, list) => settings.MediaLibrary = list,
            (sessionLibrary, list) => sessionLibrary.MediaLibrary = list);

    private void SetCurrentMediaStatus(MediaKind kind, string localPath, string fileName, bool isOwnedCache)
    {
        var previousPath = CurrentMediaLocalPath;
        var previousWasOwned = _currentMediaIsOwnedCache;

        CurrentMediaFileName = fileName;
        CurrentMediaLocalPath = localPath;
        MediaErrorMessage = null;
        CurrentMediaKind = kind;
        _currentMediaIsOwnedCache = isOwnedCache;

        // Every newly shown medium replaces any previous event medium - the trigger
        // (if SendTriggerMediaAsync sets _activeTriggerMediaSource again right after) would otherwise
        // keep applying to a completely different medium, e.g. one shown via ad-hoc sending.
        _activeTriggerMediaSource = null;

        if (previousWasOwned) DeleteFileQuietly(previousPath);

        // Explicit instead of only via OnCurrentMediaKindChanged: for two media of the same kind
        // in a row (e.g. image -> image), CurrentMediaKind doesn't "change", but the path
        // does - without this explicit call, the local preview would remain stuck.
        RefreshLocalMediaPreview();
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            Log.Error("Failed to delete file: \"{Path}\" with error: {Error}", path, e.Message);
        }
    }

    partial void OnSlideshowSecondsPerImageChanged(double value)
    {
        _ = _playerServer.PublishSlideshowIntervalAsync(value);
        RestartSlideshowTimer();
    }

    /// <summary>
    ///     The GM's own forward/back mechanism is always the global "highlight" broadcast
    ///     (see HighlightSentMediaItem) - unlike players, the GM has no separate purely
    ///     local navigation; arrow keys in the host player window use the same commands.
    /// </summary>
    private void RestartSlideshowTimer()
    {
        _slideshowTimer?.Stop();
        _slideshowTimer = null;

        if (SlideshowSecondsPerImage <= 0) return;

        _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SlideshowSecondsPerImage) };
        _slideshowTimer.Tick += (_, _) => AdvanceSlideshowIfDue();
        _slideshowTimer.Start();
    }

    /// <summary>
    ///     Videos are never automatically advanced away - they have their own natural
    ///     duration; the timer simply skips this tick and checks again next time.
    /// </summary>
    private void AdvanceSlideshowIfDue()
    {
        if (SentMediaItems.Count == 0) return;

        var current = _currentGalleryMediaId is null
            ? null
            : SentMediaItems.FirstOrDefault(i => i.MediaId == _currentGalleryMediaId);
        if (current is not null && current.Kind == MediaKind.Video) return;

        NextGalleryItemCommand.Execute(null);
    }

    [RelayCommand]
    private void NextGalleryItem()
    {
        NavigateGallery(1);
    }

    [RelayCommand]
    private void PreviousGalleryItem()
    {
        NavigateGallery(-1);
    }

    private void NavigateGallery(int direction)
    {
        if (SentMediaItems.Count == 0) return;

        var items = SentMediaItems;
        var currentIndex = _currentGalleryMediaId is null
            ? -1
            : items.ToList().FindIndex(i => i.MediaId == _currentGalleryMediaId);
        var nextIndex = ((currentIndex < 0 ? 0 : currentIndex) + direction + items.Count) % items.Count;
        HighlightSentMediaItem(items[nextIndex]);
    }

    private void AddSentMediaItem(MediaHeaderDto header, string localPath, bool isOwnedCache)
    {
        if (!Enum.TryParse<MediaKind>(header.Kind, out var kind)) return;

        var sourceItem = FindMediaLibraryItemByPath(localPath);
        var item = new SentMediaItemViewModel(header.MediaId, header.FileName, kind, localPath, header.MimeType,
            isOwnedCache,
            HighlightSentMediaItem,
            RetractSentMediaItem,
            sourceItem);
        SentMediaItems.Add(item);
        OnPropertyChanged(nameof(HasNoSentMediaItems));
        _currentGalleryMediaId = header.MediaId;
    }

    /// <summary>
    ///     GM click on a gallery item: jumps at the GM and all players (locally, non-
    ///     blocking) to this image/video, without transmitting it over the network again.
    /// </summary>
    private void HighlightSentMediaItem(SentMediaItemViewModel item)
    {
        _currentGalleryMediaId = item.MediaId;
        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        _ = _playerServer.PublishHighlightAsync(item.MediaId);
    }

    private void RetractSentMediaItem(SentMediaItemViewModel item)
    {
        item.Dispose();
        SentMediaItems.Remove(item);
        OnPropertyChanged(nameof(HasNoSentMediaItems));

        if (_currentGalleryMediaId == item.MediaId)
        {
            _currentGalleryMediaId = null;
            if (CurrentMediaLocalPath == item.LocalPath) ClearMediaCommand.Execute(null);
        }

        if (item.IsOwnedCache) DeleteFileQuietly(item.LocalPath);
        _ = _playerServer.PublishRetractAsync(item.MediaId);
    }

    partial void OnIsClockRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleClockLabel));
        OnPropertyChanged(nameof(NextEventJumpLabel));
        OnPropertyChanged(nameof(ClockStatusText));
    }

    [RelayCommand]
    private void ToggleNetworkServer()
    {
        if (IsNetworkServerRunning)
        {
            _playerServer.Stop();
            IsNetworkServerRunning = false;
            NetworkServerStatus = LocalizationService.Get("MainWindowViewModel.Network.ServerOff");
            NetworkServerAddress = null;
            RemoteFullscreen = false;
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
            OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
            ClearRemoteOnlyActiveSounds();
            return;
        }

        try
        {
            var port = NetworkServerPort is > 0 and <= 65535
                ? NetworkServerPort
                : TcpPlayerServerService.DefaultPort;

            NetworkServerPort = port;
            var announcedName = string.IsNullOrWhiteSpace(ServerName) ? "RpgTimeTracker" : ServerName.Trim();
            if (announcedName.Length > 60) announcedName = announcedName[..60];
            _playerServer.Start(port, announcedName, enableDiscovery: !Program.DisableNetworkDiscovery);
            IsNetworkServerRunning = true;
            OnPropertyChanged(nameof(CanToggleDisplayFullscreen));
            NetworkServerAddress = PlayerMdnsAnnouncer.GetBestLocalIPv4()?.ToString();
            NetworkServerStatus = string.Format(LocalizationService.Get("MainWindowViewModel.Network.ServerActiveOnPort"), port);
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Network server could not be started on port {Port}", NetworkServerPort);
            IsNetworkServerRunning = false;
            NetworkServerStatus = string.Format(LocalizationService.Get("MainWindowViewModel.Network.ServerStartFailed"), ex.Message);
            OnPropertyChanged(nameof(ToggleNetworkServerLabel));
        }
    }

    [RelayCommand]
    private void ToggleClock()
    {
        if (_clock.IsRunning)
        {
            _clock.Pause();
            IsClockRunning = _clock.IsRunning;
            Log.Information("Game time paused at {GameTime}", _clock.CurrentTime);
            _ = _playerServer.PublishClockStoppedAsync();
        }
        else
        {
            _clock.Start();
            IsClockRunning = _clock.IsRunning;
            Log.Information("Game time started at {GameTime} (speed {Speed}x)", _clock.CurrentTime,
                SpeedMultiplier);
            _ = _playerServer.PublishClockStartedAsync();
        }
    }

    [RelayCommand]
    private void ApplyManualDateTime()
    {
        if (!DateTime.TryParse(ManualDateTimeText, out var parsed))
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.InvalidDateTime");
            return;
        }

        ClockErrorMessage = null;
        var delta = parsed - _clock.CurrentTime;
        if (delta == TimeSpan.Zero) return;

        Log.Information("Game time manually set: {OldTime} -> {NewTime}", _clock.CurrentTime, parsed);
        _clock.SetTime(parsed);
        PushJump(delta);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.GameTimeManuallySet"), FormatGameTime(parsed)));
    }


    [RelayCommand]
    private void JumpToNextEvent()
    {
        var next = GetNextEventDelta();
        if (next is null || next.Value <= TimeSpan.Zero)
        {
            ClockErrorMessage = LocalizationService.Get("MainWindowViewModel.Errors.NoNextEventFound");
            return;
        }

        ClockErrorMessage = null;
        JumpBy(next.Value);
    }

    private TimeSpan? GetNextEventDelta()
    {
        var candidates = Timers
            .Select(t => t.TimeUntilNextEvent)
            .Concat(Alarms.Select(a => a.TimeUntilNextEvent))
            .Concat(IntervalEvents.Select(i => i.TimeUntilNextEvent))
            .Where(delta => delta.HasValue && delta.Value > TimeSpan.Zero)
            .Select(delta => delta!.Value)
            .ToList();

        return candidates.Count == 0 ? null : candidates.Min();
    }

}
