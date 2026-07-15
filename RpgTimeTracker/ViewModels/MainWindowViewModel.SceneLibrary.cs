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
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel
{
    // ==================== Scenes library (Phase 2 of the Scenes/Tags/Calendars project) ====================
    // Own top-level library, not nested under anything - see SceneLibraryItemViewModel's doc
    // comment for why it doesn't derive from LibraryItemViewModelBase<TSelf> like Media/Sound.

    public ObservableCollection<SceneLibraryItemViewModel> SceneLibrary { get; } = [];

    public bool HasNoScenes => SceneLibrary.Count == 0;

    [ObservableProperty] private SceneLibraryItemViewModel? _selectedScene;

    /// <summary>Phase 3 of the plan: the Scene whose own Timer/Alarm/IntervalEvent timeline is
    ///     currently ticking. OnClockTick (MainWindowViewModel.Clock.cs) only advances
    ///     ActiveScene's items, never any other Scene's - that omission IS the "paused while
    ///     inactive" mechanism, there's no separate pause flag to maintain. Set by
    ///     ActivateSceneAsync; cleared if the active Scene itself gets deleted.</summary>
    [ObservableProperty] private SceneLibraryItemViewModel? _activeScene;

    [RelayCommand]
    private async Task AddSceneAsync()
    {
        var scope = await ResolveScopeForNewItemAsync();
        // No start date by default - not every Scene is timebound (see
        // SceneLibraryItemViewModel's doc comment); the GM adds one via CalendarDateInput only
        // if this Scene actually needs to be tied to a calendar date.
        var scene = new SceneLibraryItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewSceneName"), null,
            RemoveScene, OnSceneLibraryItemChanged, scope);
        SceneLibrary.Add(scene);
        SelectedScene = scene;
        OnPropertyChanged(nameof(HasNoScenes));
        SaveSceneLibrarySettings();
    }

    private void RemoveScene(SceneLibraryItemViewModel scene)
    {
        SceneLibrary.Remove(scene);
        if (SelectedScene == scene) SelectedScene = null;
        if (ActiveScene == scene) ActiveScene = null;
        OnPropertyChanged(nameof(HasNoScenes));
        SaveSceneLibrarySettings();
    }

    private void OnSceneLibraryItemChanged(SceneLibraryItemViewModel scene) => SaveSceneLibrarySettings();

    /// <summary>See MoveNpcLibraryItemToScope's doc comment - Scenes own no files either, so
    ///     moving scope is just a Scope flip + re-save, no file to relocate.</summary>
    public void MoveSceneLibraryItemToScope(SceneLibraryItemViewModel scene, LibraryScope targetScope) =>
        MoveLibraryItemToScope(scene.Scope, targetScope, "Scene", scene.Name, () =>
        {
            scene.Scope = targetScope;
            SaveSceneLibrarySettings();
        });

    [RelayCommand]
    private void MoveSceneLibraryItemToShared(SceneLibraryItemViewModel scene) =>
        MoveSceneLibraryItemToScope(scene, LibraryScope.Shared);

    [RelayCommand]
    private void MoveSceneLibraryItemToSession(SceneLibraryItemViewModel scene) =>
        MoveSceneLibraryItemToScope(scene, LibraryScope.SessionLocal);

    /// <summary>Who references a Media/Sound/Map Library item by Id from any Scene's bundle -
    ///     registered into _usageRegistry so deleting that item goes through the same 3-way
    ///     confirm-delete flow as any other in-use item. Playlists aren't Library items in this
    ///     registry's sense (see RemovePlaylist's own direct cleanup instead).</summary>
    private IEnumerable<string> FindSceneUsagesById(Guid id)
    {
        foreach (var scene in SceneLibrary)
            if (scene.Images.Any(i => i.Id == id) || scene.Maps.Any(m => m.Id == id) ||
                scene.Sounds.Any(s => s.Id == id))
                yield return scene.Name;
    }

    private void ClearSceneReferencesById(Guid id)
    {
        foreach (var scene in SceneLibrary)
        {
            foreach (var image in scene.Images.Where(i => i.Id == id).ToList()) scene.Images.Remove(image);
            foreach (var map in scene.Maps.Where(m => m.Id == id).ToList()) scene.Maps.Remove(map);
            foreach (var sound in scene.Sounds.Where(s => s.Id == id).ToList()) scene.Sounds.Remove(sound);
        }
    }

    private static SceneLibraryEntryDto ToSceneLibraryEntryDto(SceneLibraryItemViewModel scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        DescriptionMarkdown = scene.DescriptionMarkdown,
        StartDateSeconds = scene.StartDate?.TotalSeconds,
        ImageIds = scene.Images.Select(i => i.Id).ToList(),
        MapIds = scene.Maps.Select(m => m.Id).ToList(),
        SoundIds = scene.Sounds.Select(s => s.Id).ToList(),
        PlaylistId = scene.Playlist?.Id,
        TagIds = scene.TagIds.ToList(),
        Timers = scene.Timers.Select(t => t.ToDto()).ToList(),
        Alarms = scene.Alarms.Select(a => a.ToDto()).ToList(),
        IntervalEvents = scene.IntervalEvents.Select(i => i.ToDto()).ToList()
    };

    /// <summary>Wrapped in BeginBulkLoad/EndBulkLoad - see NpcLibraryItemViewModel's identical
    ///     concern: this Scene isn't in the SceneLibrary collection yet while this method builds
    ///     it, so every property-set below would otherwise fire a save that serializes SceneLibrary
    ///     as it stood before this (and any later) entry was added.</summary>
    private SceneLibraryItemViewModel FromSceneLibraryEntryDto(SceneLibraryEntryDto entry, LibraryScope scope)
    {
        var startDate = entry.StartDateSeconds is { } seconds ? new Shared.Models.GameInstant(seconds) : (Shared.Models.GameInstant?)null;
        var scene = new SceneLibraryItemViewModel(entry.Id, entry.Name,
            startDate, RemoveScene, OnSceneLibraryItemChanged, scope,
            entry.TagIds);
        scene.BeginBulkLoad();
        try
        {
            scene.DescriptionMarkdown = entry.DescriptionMarkdown;
            scene.IsDescriptionPreviewMode = !string.IsNullOrWhiteSpace(entry.DescriptionMarkdown);
            foreach (var imageId in entry.ImageIds)
            {
                var image = MediaLibrary.FirstOrDefault(m => m.Id == imageId);
                if (image is not null) scene.Images.Add(image);
            }

            foreach (var mapId in entry.MapIds)
            {
                var map = MapLibrary.FirstOrDefault(m => m.Id == mapId);
                if (map is not null) scene.Maps.Add(map);
            }

            foreach (var soundId in entry.SoundIds)
            {
                var sound = SoundLibrary.FirstOrDefault(s => s.Id == soundId);
                if (sound is not null) scene.Sounds.Add(sound);
            }

            scene.Playlist = entry.PlaylistId is { } playlistId
                ? Playlists.FirstOrDefault(p => p.Id == playlistId)
                : null;

            foreach (var timerDto in entry.Timers) scene.Timers.Add(TimerItemViewModel.FromDto(timerDto, scene.RemoveTimer));
            foreach (var alarmDto in entry.Alarms)
                scene.Alarms.Add(AlarmItemViewModel.FromDto(alarmDto, scene.StartDate ?? default, scene.RemoveAlarm));
            foreach (var intervalDto in entry.IntervalEvents)
                scene.IntervalEvents.Add(IntervalEventItemViewModel.FromDto(intervalDto, scene.RemoveIntervalEvent));

            return scene;
        }
        finally
        {
            scene.EndBulkLoad();
        }
    }

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    private void SaveSceneLibrarySettings() =>
        SaveLibrarySettings(SceneLibrary, s => s.Scope, ToSceneLibraryEntryDto,
            (settings, list) => settings.SceneLibrary = list,
            (sessionLibrary, list) => sessionLibrary.SceneLibrary = list);

    /// <summary>
    ///     "Activate Scene" (Phase 2 of the plan, reworked): deliberately does NOT auto-push the
    ///     bundle to players - a Scene can hold several Images/Maps at once, and not every Scene
    ///     is even timebound, so firing everything the moment a Scene becomes "active" would be
    ///     surprising rather than helpful (a GM might activate a Scene purely to un-pause its own
    ///     timeline, with no intention of showing a specific map yet). Activating only sets
    ///     ActiveScene; the GM sends each Image/Map/Sound/Playlist individually from the Scene
    ///     editor's own per-item buttons (MediaLibraryItemViewModel.ShowCommand,
    ///     OpenMapToPlayersCommand, SoundLibraryItemViewModel.PlayCommand, PlayPlaylistCommand) -
    ///     the same commands the Media/Sound/Music tabs' own tiles already use.
    /// </summary>
    [RelayCommand]
    private void ActivateScene(SceneLibraryItemViewModel scene)
    {
        // Phase 3: becoming the active Scene is what "un-pauses" its own Timer/Alarm/
        // IntervalEvent timeline - see ActiveScene's doc comment and OnClockTick.
        ActiveScene = scene;

        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.SceneActivated"), scene.Name));
        Log.Information("Scene activated: {SceneName}", scene.Name);
    }

    /// <summary>Phase 4 hook: resolves a Timer/Alarm/IntervalEvent/CalendarEntry's optional
    ///     TargetSceneId and activates that Scene - a no-op if the field is unset or no longer
    ///     resolves to an existing Scene (e.g. it was deleted after being targeted).</summary>
    private void ActivateSceneById(Guid? sceneId)
    {
        if (sceneId is not { } id) return;

        var scene = SceneLibrary.FirstOrDefault(s => s.Id == id);
        if (scene is null) return;

        ActivateScene(scene);
    }
}
