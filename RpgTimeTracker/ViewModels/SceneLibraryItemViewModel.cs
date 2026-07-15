using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A named beat within a Session (Phase 2 of the Scenes/Tags/Calendars project) - an optional
///     start date on the custom calendar plus an optional bundle of Media/Sound/Playlist/Map to
///     make available to the GM when activated. Deliberately does not derive from
///     LibraryItemViewModelBase&lt;TSelf&gt;, following MapItemViewModel/NpcLibraryItemViewModel's
///     precedent: a Scene owns no single file, just references (by Id) into the Media/Sound/Map
///     libraries and the Playlists list.
///
///     A Scene can bundle multiple Images and Maps (not just one of each) - not every scene is a
///     single backdrop. StartDate is optional: not every Scene is timebound, some are purely
///     driven by player action or by another trigger's "activate Scene" hook (see
///     MainWindowViewModel.ActivateSceneById), with no calendar date attached at all.
///
///     Activating a Scene (MainWindowViewModel.ActivateSceneAsync) deliberately does NOT
///     auto-push the bundle to players - a Scene's bundle can hold several maps/images at once,
///     and blindly firing all of them the moment a Scene becomes active would be surprising, not
///     helpful. Activation only makes this Scene the ActiveScene (unpausing its own timeline, see
///     below); the GM still sends each bundled Image/Map/Sound/Playlist individually from this
///     Scene's own editor, via the same per-item send paths already used by the Media/Sound/Music
///     library tiles.
///
///     Phase 3 adds its own scoped Timer/Alarm/IntervalEvent timeline (Timers/Alarms/
///     IntervalEvents below), reusing the exact same view models as the app's global timeline.
///     These are intentionally never networked/published to players and never appear in the
///     global TimelineItems list - MainWindowViewModel.OnClockTick only advances a Scene's own
///     items while it is the ActiveScene (see MainWindowViewModel.SceneLibrary.cs), which is what
///     gives "paused while inactive" its actual mechanism: an inactive Scene's items simply never
///     get ticked, so their Elapsed/Remaining state freezes exactly where it was.
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

    /// <summary>Optional - not every Scene is timebound, see this class's doc comment.</summary>
    [ObservableProperty] private GameInstant? _startDate;
    [ObservableProperty] private LibraryScope _scope;
    [ObservableProperty] private PlaylistViewModel? _playlist;

    public Guid Id { get; }

    /// <summary>Whether this is MainWindowViewModel.ActiveScene right now - purely local UI state,
    ///     not persisted (which Scene was active isn't meaningful across a restart). Toggled by
    ///     MainWindowViewModel whenever ActiveScene changes, so the Scenes list/editor can show
    ///     which one it is without every consumer having to compare against ActiveScene itself.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>A Scene can bundle several Images/Maps at once - see this class's doc comment for
    ///     why activation doesn't auto-send them.</summary>
    public ObservableCollection<MediaLibraryItemViewModel> Images { get; } = [];

    public ObservableCollection<MapItemViewModel> Maps { get; } = [];

    public ObservableCollection<SoundLibraryItemViewModel> Sounds { get; } = [];

    /// <summary>Purely local UI state (not persisted) - the ComboBox selection in each "add" row
    ///     resets to null once picked (see AddPendingSound/AddPendingImage/AddPendingMap), so it
    ///     reads as a one-shot picker rather than a second "currently selected item" concept.</summary>
    [ObservableProperty] private SoundLibraryItemViewModel? _pendingSoundToAdd;

    [ObservableProperty] private MediaLibraryItemViewModel? _pendingImageToAdd;

    [ObservableProperty] private MapItemViewModel? _pendingMapToAdd;

    /// <summary>Freeform Tag Ids attached to this Scene - separate from Scene membership on other
    ///     library items, a different, explicit mechanism (see Tag's doc comment).</summary>
    public ObservableCollection<Guid> TagIds { get; } = [];

    /// <summary>Phase 3's scene-scoped timeline - see this class's doc comment for the
    ///     pause-while-inactive mechanism. Populated either by AddTimer/AddAlarm/AddIntervalEvent
    ///     (GM authoring a new one directly on this Scene) or by MainWindowViewModel while
    ///     reconstructing this Scene from its saved DTO.</summary>
    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];

    public ObservableCollection<AlarmItemViewModel> Alarms { get; } = [];

    public ObservableCollection<IntervalEventItemViewModel> IntervalEvents { get; } = [];

    /// <summary>Same Timers/Alarms/IntervalEvents, each wrapped in a TimelineDisplayItemViewModel
    ///     by MainWindowViewModel.AddSceneTimelineItem - lets the Scene editor reuse the exact
    ///     same rich item template (progress bar, status, full config panel) as the global
    ///     Elementliste instead of a stripped-down bespoke row, via the shared
    ///     "TimelineItemTemplate" resource in MainWindow.axaml. Kept in lock-step with
    ///     Timers/Alarms/IntervalEvents by MainWindowViewModel; this class itself never adds to it
    ///     directly.</summary>
    public ObservableCollection<TimelineDisplayItemViewModel> TimelineDisplayItems { get; } = [];

    /// <summary>See LibraryItemViewModelBase.IsSessionLocal's doc comment - same purpose here.</summary>
    public bool IsSessionLocal => Scope == LibraryScope.SessionLocal;

    public string DescriptionPreviewToggleIcon => IsDescriptionPreviewMode ? "✎" : "👁";

    public SceneLibraryItemViewModel(
        Guid id,
        string name,
        GameInstant? startDate,
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
        Images.CollectionChanged += (_, _) => NotifyChanged();
        Maps.CollectionChanged += (_, _) => NotifyChanged();
        Sounds.CollectionChanged += (_, _) => NotifyChanged();
        Timers.CollectionChanged += (_, _) => NotifyChanged();
        Alarms.CollectionChanged += (_, _) => NotifyChanged();
        IntervalEvents.CollectionChanged += (_, _) => NotifyChanged();
        _startDateText = startDate is { } start ? CalendarService.Active.FormatDateTimeText(start) : string.Empty;
        _isDescriptionPreviewMode = false;
    }

    /// <summary>Editable text form of StartDate, following CalendarEntryViewModel.
    ///     StartDateTimeText's precedent - bound to a CalendarDateInput, which works with plain
    ///     text rather than GameInstant directly. Invalid text is left as typed (not reverted)
    ///     until it parses; StartDate (the actual persisted value) only updates once it does.
    ///     Blank text means "no start date" (StartDate = null) - a Scene doesn't have to be
    ///     timebound, see this class's doc comment.</summary>
    [ObservableProperty] private string _startDateText;

    partial void OnStartDateTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            StartDate = null;
            return;
        }

        if (CalendarService.Active.TryParseDateTimeText(value.Trim(), out var parsed)) StartDate = parsed;
    }

    partial void OnNameChanged(string value) => NotifyChanged();
    partial void OnDescriptionMarkdownChanged(string value) => NotifyChanged();

    partial void OnIsDescriptionPreviewModeChanged(bool value) => OnPropertyChanged(nameof(DescriptionPreviewToggleIcon));

    [RelayCommand]
    private void ToggleDescriptionPreview() => IsDescriptionPreviewMode = !IsDescriptionPreviewMode;

    partial void OnStartDateChanged(GameInstant? value)
    {
        var formatted = value is { } date ? CalendarService.Active.FormatDateTimeText(date) : string.Empty;
        if (StartDateText != formatted) StartDateText = formatted;
        NotifyChanged();
    }

    partial void OnPlaylistChanged(PlaylistViewModel? value) => NotifyChanged();

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

    public void AddImage(MediaLibraryItemViewModel image)
    {
        if (!Images.Contains(image)) Images.Add(image);
    }

    [RelayCommand]
    private void RemoveImage(MediaLibraryItemViewModel image) => Images.Remove(image);

    [RelayCommand]
    private void AddPendingImage()
    {
        if (PendingImageToAdd is { } image) AddImage(image);
        PendingImageToAdd = null;
    }

    public void AddMap(MapItemViewModel map)
    {
        if (!Maps.Contains(map)) Maps.Add(map);
    }

    [RelayCommand]
    private void RemoveMap(MapItemViewModel map) => Maps.Remove(map);

    [RelayCommand]
    private void AddPendingMap()
    {
        if (PendingMapToAdd is { } map) AddMap(map);
        PendingMapToAdd = null;
    }

    /// <summary>Creates a new Timer directly owned by this Scene, with the same sensible
    ///     defaults AddTimer's global equivalent falls back to - the GM edits Name/Duration
    ///     afterwards via the item's own existing ApplyEdits UI, same as the global timeline.</summary>
    [RelayCommand]
    private void AddTimer()
    {
        var model = new TimerItem
        {
            Name = LocalizationService.Get("MainWindowViewModel.Defaults.TimerName"),
            Icon = VisualItemHelper.NormalizeIcon(VisualItemHelper.IconTimer),
            Duration = TimeSpan.FromMinutes(10),
            Sound = SoundService.Pling
        };
        Timers.Add(new TimerItemViewModel(model, RemoveTimer));
    }

    /// <summary>Public (unlike RemoveSound et al.) so MainWindowViewModel.FromSceneLibraryEntryDto
    ///     can pass it directly as TimerItemViewModel.FromDto's onDeleteRequested callback while
    ///     reconstructing this Scene's saved timeline.</summary>
    public void RemoveTimer(TimerItemViewModel vm) => Timers.Remove(vm);

    [RelayCommand]
    private void AddAlarm()
    {
        // A Scene without a StartDate (not every Scene is timebound - see this class's doc
        // comment) has no obvious reference instant either; fall back to the calendar epoch,
        // same as any other "no better default" case elsewhere in this file.
        var referenceTime = StartDate ?? default;
        var model = new AlarmItem
        {
            Name = LocalizationService.Get("MainWindowViewModel.Defaults.AlarmName"),
            Icon = VisualItemHelper.NormalizeIcon(VisualItemHelper.IconAlarm),
            TriggerAt = referenceTime.Add(TimeSpan.FromHours(1)),
            Sound = SoundService.Pling
        };
        Alarms.Add(new AlarmItemViewModel(model, referenceTime, RemoveAlarm));
    }

    /// <summary>See RemoveTimer's doc comment - same reasoning.</summary>
    public void RemoveAlarm(AlarmItemViewModel vm) => Alarms.Remove(vm);

    [RelayCommand]
    private void AddIntervalEvent()
    {
        var model = new IntervalEventItem
        {
            Name = LocalizationService.Get("MainWindowViewModel.Defaults.IntervalName"),
            Icon = VisualItemHelper.NormalizeIcon(VisualItemHelper.IconOnTime),
            Interval = TimeSpan.FromMinutes(10),
            ActiveDuration = TimeSpan.FromMinutes(1),
            Sound = SoundService.Pling
        };
        IntervalEvents.Add(new IntervalEventItemViewModel(model, RemoveIntervalEvent));
    }

    /// <summary>See RemoveTimer's doc comment - same reasoning.</summary>
    public void RemoveIntervalEvent(IntervalEventItemViewModel vm) => IntervalEvents.Remove(vm);

    private void NotifyChanged()
    {
        if (!_suppressChangeNotifications) _onChanged?.Invoke(this);
    }

    /// <summary>See _suppressChangeNotifications' doc comment. Must always be paired with a
    ///     matching EndBulkLoad(), even if population fails/returns early.</summary>
    public void BeginBulkLoad() => _suppressChangeNotifications = true;

    public void EndBulkLoad() => _suppressChangeNotifications = false;
}
