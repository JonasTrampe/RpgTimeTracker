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
    // ==================== Scenes library (Phase 2 of the Scenes/Tags/Calendars project) ====================
    // Own top-level library, not nested under anything - see SceneLibraryItemViewModel's doc
    // comment for why it doesn't derive from LibraryItemViewModelBase<TSelf> like Media/Sound.
    // Activation orchestration and a Scene-scoped timeline (Phases 3-4 of the plan) aren't wired
    // yet - this is the data/persistence layer only, with no tab UI to edit it from either.

    public ObservableCollection<SceneLibraryItemViewModel> SceneLibrary { get; } = [];

    public bool HasNoScenes => SceneLibrary.Count == 0;

    [ObservableProperty] private SceneLibraryItemViewModel? _selectedScene;

    [RelayCommand]
    private async Task AddSceneAsync()
    {
        var scope = await ResolveScopeForNewItemAsync();
        var scene = new SceneLibraryItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewSceneName"), _clock.CurrentTime,
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

    /// <summary>Who references a Media/Sound/Music/Map Library item by Id from any Scene's bundle -
    ///     registered into _usageRegistry so deleting that item goes through the same 3-way
    ///     confirm-delete flow as any other in-use item.</summary>
    private IEnumerable<string> FindSceneUsagesById(Guid id)
    {
        foreach (var scene in SceneLibrary)
            if (scene.Image?.Id == id || scene.Map?.Id == id || scene.Music?.Id == id ||
                scene.Sounds.Any(s => s.Id == id))
                yield return scene.Name;
    }

    private void ClearSceneReferencesById(Guid id)
    {
        foreach (var scene in SceneLibrary)
        {
            if (scene.Image?.Id == id) scene.Image = null;
            if (scene.Map?.Id == id) scene.Map = null;
            if (scene.Music?.Id == id) scene.Music = null;

            var staleSounds = scene.Sounds.Where(s => s.Id == id).ToList();
            foreach (var sound in staleSounds) scene.Sounds.Remove(sound);
        }
    }

    private static SceneLibraryEntryDto ToSceneLibraryEntryDto(SceneLibraryItemViewModel scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        DescriptionMarkdown = scene.DescriptionMarkdown,
        StartDateSeconds = scene.StartDate.TotalSeconds,
        ImageId = scene.Image?.Id,
        MapId = scene.Map?.Id,
        SoundIds = scene.Sounds.Select(s => s.Id).ToList(),
        MusicId = scene.Music?.Id,
        TagIds = scene.TagIds.ToList()
    };

    /// <summary>Wrapped in BeginBulkLoad/EndBulkLoad - see NpcLibraryItemViewModel's identical
    ///     concern: this Scene isn't in the SceneLibrary collection yet while this method builds
    ///     it, so every property-set below would otherwise fire a save that serializes SceneLibrary
    ///     as it stood before this (and any later) entry was added.</summary>
    private SceneLibraryItemViewModel FromSceneLibraryEntryDto(SceneLibraryEntryDto entry, LibraryScope scope)
    {
        var scene = new SceneLibraryItemViewModel(entry.Id, entry.Name,
            new Shared.Models.GameInstant(entry.StartDateSeconds), RemoveScene, OnSceneLibraryItemChanged, scope,
            entry.TagIds);
        scene.BeginBulkLoad();
        try
        {
            scene.DescriptionMarkdown = entry.DescriptionMarkdown;
            scene.IsDescriptionPreviewMode = !string.IsNullOrWhiteSpace(entry.DescriptionMarkdown);
            scene.Image = entry.ImageId is { } imageId ? MediaLibrary.FirstOrDefault(m => m.Id == imageId) : null;
            scene.Map = entry.MapId is { } mapId ? MapLibrary.FirstOrDefault(m => m.Id == mapId) : null;
            scene.Music = entry.MusicId is { } musicId ? MusicLibrary.FirstOrDefault(m => m.Id == musicId) : null;
            foreach (var soundId in entry.SoundIds)
            {
                var sound = SoundLibrary.FirstOrDefault(s => s.Id == soundId);
                if (sound is not null) scene.Sounds.Add(sound);
            }

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
}
