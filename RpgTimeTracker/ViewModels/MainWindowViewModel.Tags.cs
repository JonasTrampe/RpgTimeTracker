using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Freeform Tags, attachable to any library item's TagIds (Media/Sound/Music/Map/NPC) - a
///     single flat, campaign-wide list persisted in ThemeSettingsService (see Tag's doc comment
///     for why this isn't Shared-vs-SessionLocal like the libraries it tags). Deleting a tag goes
///     through _usageRegistry (see FindTagUsagesById/ClearTagReferencesById) exactly like deleting
///     an in-use Media/Sound/Map/NPC item, but without a confirmation prompt - a tag carries no
///     content of its own, so silently untagging every item that had it is low-stakes compared to
///     losing a file reference.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    [ObservableProperty] private string _newTagName = string.Empty;

    public bool HasNoTags => Tags.Count == 0;

    private void LoadTags(List<Tag> tags)
    {
        foreach (var tag in tags) Tags.Add(TagViewModel.FromModel(tag, RemoveTag, _ => SaveTags()));
        OnPropertyChanged(nameof(HasNoTags));
    }

    [RelayCommand]
    private void AddTag()
    {
        CreateTag(NewTagName);
        NewTagName = string.Empty;
    }

    /// <summary>Shared by the Settings-tab "Add" button (AddTag, above) and the "+" quick-add row
    ///     in each item's TagSelectorFlyout (see CreateTagCommand) - so a GM tagging a Media/Sound/
    ///     Music/Map/NPC/Scene item doesn't have to leave that flyout and go to Settings just to
    ///     define a new tag first.</summary>
    [RelayCommand]
    private void CreateTag(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name)
            ? LocalizationService.Get("MainWindowViewModel.Defaults.NewTagName")
            : name.Trim();
        var tag = new TagViewModel(Guid.NewGuid(), trimmed, string.Empty, RemoveTag, _ => SaveTags());
        Tags.Add(tag);
        OnPropertyChanged(nameof(HasNoTags));
        SaveTags();
    }

    private void RemoveTag(TagViewModel tag)
    {
        Tags.Remove(tag);
        _usageRegistry.ClearReferencesTo(tag.Id);
        OnPropertyChanged(nameof(HasNoTags));
        SaveTags();
    }

    private void SaveTags()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.Tags = Tags.Select(t => t.ToModel()).ToList();
        ThemeSettingsService.SaveSettings(settings);
    }

    /// <summary>Every place a Tag Id can be attached - registered into _usageRegistry so deleting a
    ///     tag (see RemoveTag) clears it from every item that had it, the same mechanism already
    ///     used for trigger-media/playlist/NPC references.</summary>
    private IEnumerable<string> FindTagUsagesById(Guid tagId)
    {
        foreach (var item in MediaLibrary) if (item.TagIds.Contains(tagId)) yield return item.Name;
        foreach (var item in SoundLibrary) if (item.TagIds.Contains(tagId)) yield return item.Name;
        foreach (var item in MusicLibrary) if (item.TagIds.Contains(tagId)) yield return item.Name;
        foreach (var item in MapLibrary) if (item.TagIds.Contains(tagId)) yield return item.Name;
        foreach (var item in NpcLibrary) if (item.TagIds.Contains(tagId)) yield return item.Name;
    }

    private void ClearTagReferencesById(Guid tagId)
    {
        foreach (var item in MediaLibrary) item.TagIds.Remove(tagId);
        foreach (var item in SoundLibrary) item.TagIds.Remove(tagId);
        foreach (var item in MusicLibrary) item.TagIds.Remove(tagId);
        foreach (var item in MapLibrary) item.TagIds.Remove(tagId);
        foreach (var item in NpcLibrary) item.TagIds.Remove(tagId);
    }
}
