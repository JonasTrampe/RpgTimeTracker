using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly HashSet<Guid> _selectedTimerTagFilterIds = [];

    private readonly Dictionary<TimelineDisplayItemViewModel, NotifyCollectionChangedEventHandler>
        _timelineTagSubscriptions = new();

    [ObservableProperty] private string _newTagName = string.Empty;

    [ObservableProperty] private string _newTimerTagName = string.Empty;
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    public bool HasNoTags => Tags.Count == 0;

    // ==================== Timer-specific Tags (separate flat list from the library-wide Tags
    // above) - attachable to any Timer/Alarm/IntervalEvent's TagIds, global or Scene-owned (both
    // show up wrapped in a TimelineDisplayItemViewModel in TimelineItems), so the Elementliste's
    // tag-filter bar isn't cluttered with Media/Sound/Map/NPC/Scene tags that make no sense there. ====================

    public ObservableCollection<TagViewModel> TimerTags { get; } = [];

    public bool HasNoTimerTags => TimerTags.Count == 0;

    /// <summary>One chip per TimerTag in the Elementliste's filter bar - see ApplyTimerTagFilter.</summary>
    public ObservableCollection<TagFilterOptionViewModel> TimerTagFilterOptions { get; } = [];

    public bool HasNoTimerTagFilterOptions => TimerTagFilterOptions.Count == 0;

    /// <summary>
    ///     Elementliste's actual ItemsSource - TimelineItems filtered by whichever
    ///     TimerTagFilterOptions are currently toggled on (OR semantics: showing any item
    ///     carrying at least one selected tag), or every item if none are selected. Kept in sync
    ///     by ApplyTimerTagFilter whenever TimelineItems or any item's TagIds changes.
    /// </summary>
    public ObservableCollection<TimelineDisplayItemViewModel> FilteredTimelineItems { get; } = [];

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

    /// <summary>
    ///     Shared by the Settings-tab "Add" button (AddTag, above) and the "+" quick-add row
    ///     in each item's TagSelectorFlyout (see CreateTagCommand) - so a GM tagging a Media/Sound/
    ///     Music/Map/NPC/Scene item doesn't have to leave that flyout and go to Settings just to
    ///     define a new tag first.
    /// </summary>
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

    /// <summary>
    ///     Every place a Tag Id can be attached - registered into _usageRegistry so deleting a
    ///     tag (see RemoveTag) clears it from every item that had it, the same mechanism already
    ///     used for trigger-media/playlist/NPC references.
    /// </summary>
    private IEnumerable<string> FindTagUsagesById(Guid tagId)
    {
        foreach (var item in MediaLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
        foreach (var item in SoundLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
        foreach (var item in MusicLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
        foreach (var item in MapLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
        foreach (var item in NpcLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
        foreach (var item in SceneLibrary)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
    }

    private void ClearTagReferencesById(Guid tagId)
    {
        foreach (var item in MediaLibrary) item.TagIds.Remove(tagId);
        foreach (var item in SoundLibrary) item.TagIds.Remove(tagId);
        foreach (var item in MusicLibrary) item.TagIds.Remove(tagId);
        foreach (var item in MapLibrary) item.TagIds.Remove(tagId);
        foreach (var item in NpcLibrary) item.TagIds.Remove(tagId);
        foreach (var item in SceneLibrary) item.TagIds.Remove(tagId);
    }

    /// <summary>
    ///     Called once from the constructor - keeps FilteredTimelineItems/subscriptions in
    ///     sync as items are added/removed from TimelineItems (both global and Scene-owned ones,
    ///     see MainWindowViewModel.SceneLibrary.cs's AddSceneTimelineItem).
    /// </summary>
    private void InitializeTimelineTagFiltering()
    {
        foreach (var item in TimelineItems) SubscribeTimelineItemTags(item);

        TimelineItems.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (TimelineDisplayItemViewModel item in e.NewItems)
                    SubscribeTimelineItemTags(item);
            if (e.OldItems is not null)
                foreach (TimelineDisplayItemViewModel item in e.OldItems)
                    UnsubscribeTimelineItemTags(item);
            ApplyTimerTagFilter();
        };
    }

    private void SubscribeTimelineItemTags(TimelineDisplayItemViewModel item)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => ApplyTimerTagFilter();
        item.TagIds.CollectionChanged += handler;
        _timelineTagSubscriptions[item] = handler;
    }

    private void UnsubscribeTimelineItemTags(TimelineDisplayItemViewModel item)
    {
        if (_timelineTagSubscriptions.Remove(item, out var handler)) item.TagIds.CollectionChanged -= handler;
    }

    private void RebuildTimerTagFilterOptions()
    {
        TimerTagFilterOptions.Clear();
        _selectedTimerTagFilterIds.Clear();
        foreach (var tag in TimerTags)
            TimerTagFilterOptions.Add(new TagFilterOptionViewModel(tag, OnTimerTagFilterToggled));
        OnPropertyChanged(nameof(HasNoTimerTagFilterOptions));
        ApplyTimerTagFilter();
    }

    private void OnTimerTagFilterToggled(Guid tagId, bool isSelected)
    {
        if (isSelected) _selectedTimerTagFilterIds.Add(tagId);
        else _selectedTimerTagFilterIds.Remove(tagId);
        ApplyTimerTagFilter();
    }

    private void ApplyTimerTagFilter()
    {
        FilteredTimelineItems.Clear();
        foreach (var item in TimelineItems)
            if (_selectedTimerTagFilterIds.Count == 0 || item.TagIds.Any(_selectedTimerTagFilterIds.Contains))
                FilteredTimelineItems.Add(item);
    }

    private void LoadTimerTags(List<Tag> tags)
    {
        foreach (var tag in tags) TimerTags.Add(TagViewModel.FromModel(tag, RemoveTimerTag, _ => SaveTimerTags()));
        OnPropertyChanged(nameof(HasNoTimerTags));
        RebuildTimerTagFilterOptions();
    }

    [RelayCommand]
    private void AddTimerTag()
    {
        CreateTimerTag(NewTimerTagName);
        NewTimerTagName = string.Empty;
    }

    /// <summary>See CreateTag's doc comment - same "quick-add from the assignment flyout" reasoning.</summary>
    [RelayCommand]
    private void CreateTimerTag(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name)
            ? LocalizationService.Get("MainWindowViewModel.Defaults.NewTagName")
            : name.Trim();
        var tag = new TagViewModel(Guid.NewGuid(), trimmed, string.Empty, RemoveTimerTag, _ => SaveTimerTags());
        TimerTags.Add(tag);
        OnPropertyChanged(nameof(HasNoTimerTags));
        RebuildTimerTagFilterOptions();
        SaveTimerTags();
    }

    private void RemoveTimerTag(TagViewModel tag)
    {
        TimerTags.Remove(tag);
        _usageRegistry.ClearReferencesTo(tag.Id);
        OnPropertyChanged(nameof(HasNoTimerTags));
        RebuildTimerTagFilterOptions();
        SaveTimerTags();
    }

    private void SaveTimerTags()
    {
        var settings = ThemeSettingsService.LoadSettings();
        settings.TimerTags = TimerTags.Select(t => t.ToModel()).ToList();
        ThemeSettingsService.SaveSettings(settings);
    }

    /// <summary>
    ///     Every Timer/Alarm/IntervalEvent, global or Scene-owned, is wrapped in a
    ///     TimelineDisplayItemViewModel and lives in TimelineItems - so a single loop over that
    ///     covers both, unlike FindTagUsagesById which has to loop each library separately.
    /// </summary>
    private IEnumerable<string> FindTimerTagUsagesById(Guid tagId)
    {
        foreach (var item in TimelineItems)
            if (item.TagIds.Contains(tagId))
                yield return item.Name;
    }

    private void ClearTimerTagReferencesById(Guid tagId)
    {
        foreach (var item in TimelineItems) item.TagIds.Remove(tagId);
    }
}