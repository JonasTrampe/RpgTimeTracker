using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named beat within a Session (Phase 2 of the Scenes/Tags/Calendars project) - a start date
///     on the custom calendar plus an optional bundle of Media/Sound/Music/Map to push to players
///     when activated. Deliberately does not derive from LibraryItemViewModelBase&lt;TSelf&gt;,
///     following MapItemViewModel/NpcLibraryItemViewModel's precedent: a Scene owns no single file,
///     just references (by Id) into the Media/Sound/Music/Map libraries. Activation orchestration
///     and its own scoped timeline (Phases 3-4 of the plan) aren't wired yet - this is the data/
///     persistence layer only.
/// </summary>
public sealed partial class SceneLibraryItemViewModel : ObservableObject, ITaggable
{
    private readonly Action<SceneLibraryItemViewModel> _onDeleteRequested;
    private readonly Action<SceneLibraryItemViewModel>? _onChanged;

    /// <summary>See NpcLibraryItemViewModel._suppressChangeNotifications' doc comment - same
    ///     purpose here, set while MainWindowViewModel.FromSceneLibraryEntryDto is reconstructing
    ///     this Scene from its saved DTO.</summary>
    private bool _suppressChangeNotifications;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _descriptionMarkdown = string.Empty;

    /// <summary>See NpcGmInfoBlockViewModel.IsPreviewMode's doc comment - same purely-local,
    ///     not-persisted purpose here.</summary>
    [ObservableProperty] private bool _isDescriptionPreviewMode;

    [ObservableProperty] private GameInstant _startDate;
    [ObservableProperty] private LibraryScope _scope;
    [ObservableProperty] private MediaLibraryItemViewModel? _image;
    [ObservableProperty] private MapItemViewModel? _map;
    [ObservableProperty] private MusicLibraryItemViewModel? _music;

    public Guid Id { get; }

    public ObservableCollection<SoundLibraryItemViewModel> Sounds { get; } = [];

    /// <summary>Purely local UI state (not persisted) - the ComboBox selection in the "add sound"
    ///     row resets to null once picked (see AddPendingSound), so it reads as a one-shot picker
    ///     rather than a second "currently selected sound" concept.</summary>
    [ObservableProperty] private SoundLibraryItemViewModel? _pendingSoundToAdd;

    /// <summary>Freeform Tag Ids attached to this Scene - separate from Scene membership on other
    ///     library items, a different, explicit mechanism (see Tag's doc comment).</summary>
    public ObservableCollection<Guid> TagIds { get; } = [];

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    public string DescriptionPreviewToggleIcon => IsDescriptionPreviewMode ? "✎" : "👁";

    public SceneLibraryItemViewModel(
        Guid id,
        string name,
        GameInstant startDate,
        Action<SceneLibraryItemViewModel> onDeleteRequested,
        Action<SceneLibraryItemViewModel>? onChanged,
        LibraryScope scope = LibraryScope.Shared,
        IEnumerable<Guid>? tagIds = null)
    {
        Id = id;
        _name = name;
        _startDate = startDate;
        _scope = scope;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
        if (tagIds is not null) foreach (var tagId in tagIds) TagIds.Add(tagId);
        TagIds.CollectionChanged += (_, _) => NotifyChanged();
        Sounds.CollectionChanged += (_, _) => NotifyChanged();
        _startDateText = CalendarService.Active.FormatDateTimeText(startDate);
        _isDescriptionPreviewMode = false;
    }

    /// <summary>Editable text form of StartDate, following CalendarEntryViewModel.
    ///     StartDateTimeText's precedent - bound to a CalendarDateInput, which works with plain
    ///     text rather than GameInstant directly. Invalid text is left as typed (not reverted)
    ///     until it parses; StartDate (the actual persisted value) only updates once it does.</summary>
    [ObservableProperty] private string _startDateText;

    partial void OnStartDateTextChanged(string value)
    {
        if (CalendarService.Active.TryParseDateTimeText(value?.Trim(), out var parsed)) StartDate = parsed;
    }

    partial void OnNameChanged(string value) => NotifyChanged();
    partial void OnDescriptionMarkdownChanged(string value) => NotifyChanged();

    partial void OnIsDescriptionPreviewModeChanged(bool value) => OnPropertyChanged(nameof(DescriptionPreviewToggleIcon));

    [RelayCommand]
    private void ToggleDescriptionPreview() => IsDescriptionPreviewMode = !IsDescriptionPreviewMode;

    partial void OnStartDateChanged(GameInstant value)
    {
        var formatted = CalendarService.Active.FormatDateTimeText(value);
        if (StartDateText != formatted) StartDateText = formatted;
        NotifyChanged();
    }

    partial void OnImageChanged(MediaLibraryItemViewModel? value) => NotifyChanged();
    partial void OnMapChanged(MapItemViewModel? value) => NotifyChanged();
    partial void OnMusicChanged(MusicLibraryItemViewModel? value) => NotifyChanged();

    partial void OnScopeChanged(LibraryScope value)
    {
        OnPropertyChanged(nameof(IsSessionLocal));
        NotifyChanged();
    }

    [RelayCommand]
    private void Delete() => _onDeleteRequested(this);

    public void AddSound(SoundLibraryItemViewModel sound)
    {
        if (!Sounds.Contains(sound)) Sounds.Add(sound);
    }

    [RelayCommand]
    private void RemoveSound(SoundLibraryItemViewModel sound) => Sounds.Remove(sound);

    [RelayCommand]
    private void AddPendingSound()
    {
        if (PendingSoundToAdd is { } sound) AddSound(sound);
        PendingSoundToAdd = null;
    }

    private void NotifyChanged()
    {
        if (!_suppressChangeNotifications) _onChanged?.Invoke(this);
    }

    /// <summary>See _suppressChangeNotifications' doc comment. Must always be paired with a
    ///     matching EndBulkLoad(), even if population fails/returns early.</summary>
    public void BeginBulkLoad() => _suppressChangeNotifications = true;

    public void EndBulkLoad() => _suppressChangeNotifications = false;
}
