using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.ViewModels;

public partial class CalendarEntryViewModel : ObservableObject
{
    // These are display strings, re-evaluated on each access via LocalizationService so
    // language switches propagate immediately. They are never serialized: persisted data
    // uses CalendarRecurrenceKind (enum) via ToRecurrenceKind/FromRecurrenceKind below, and
    // these strings are only compared against each other (both always in the current
    // language), never against a stored value from a previous language.
    public static string RecurrenceNone => LocalizationService.Get("Calendar.RecurrenceNone");
    public static string RecurrenceDaily => LocalizationService.Get("Calendar.RecurrenceDaily");
    public static string RecurrenceWeekly => LocalizationService.Get("Calendar.RecurrenceWeekly");
    public static string RecurrenceMonthly => LocalizationService.Get("Calendar.RecurrenceMonthly");
    public static string RecurrenceYearly => LocalizationService.Get("Calendar.RecurrenceYearly");

    private readonly Action<CalendarEntryViewModel> _onChanged;
    private readonly Action<CalendarEntryViewModel> _onDeleteRequested;

    [ObservableProperty] private string _colorHex = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _icon = VisualItemHelper.IconCalendar;
    [ObservableProperty] private bool _isPlayerVisible = true;
    [ObservableProperty] private string _recurrence = RecurrenceNone;
    [ObservableProperty] private string _repeatUntilText = string.Empty;
    [ObservableProperty] private string _startDateTimeText;
    private bool _suppressChangeNotifications;
    [ObservableProperty] private string _title = LocalizationService.Get("Calendar.NewEntryTitle");
    [ObservableProperty] private string? _validationMessage;

    public CalendarEntryViewModel(Action<CalendarEntryViewModel> onChanged,
        Action<CalendarEntryViewModel> onDeleteRequested)
    {
        Id = Guid.NewGuid();
        _onChanged = onChanged;
        _onDeleteRequested = onDeleteRequested;
        // Was DateTime.Now (real wall-clock "today") as a convenience default - that no longer
        // has meaning under an arbitrary game-calendar epoch, so this defaults to a fixed
        // placeholder (Year 1, Jan 1, noon) instead; the GM edits it before saving anyway.
        _startDateTimeText = CalendarService.Active.FormatDateTimeText(
            CalendarService.Active.FromCalendarDate(1, 0, 1, 12, 0, 0));
        TriggerMedia.PropertyChanged += (_, _) => NotifyChanged();
        Validate();
    }

    public Guid Id { get; set; }
    public TriggerMediaConfig TriggerMedia { get; } = new();

    /// <summary>Phase 4 of the Scenes/Tags/Calendars project: if set, this entry occurring also
    ///     activates the named Scene - see MainWindowViewModel.ActivateSceneById. Host-only UI
    ///     concern (the picker itself lives in MainWindow.axaml), but kept here alongside
    ///     TriggerMedia since both round-trip through TryBuildDefinition/FromDefinition the same
    ///     way.</summary>
    [ObservableProperty] private Guid? _targetSceneId;
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);
    public IBrush? ColorBrush => VisualItemHelper.TryBrush(ColorHex);

    public static IReadOnlyList<string> RecurrenceOptions =>
    [
        RecurrenceNone,
        RecurrenceDaily,
        RecurrenceWeekly,
        RecurrenceMonthly,
        RecurrenceYearly
    ];

    public string TriggerSummary => TriggerMedia.HasMedia
        ? $"{TriggerMedia.FileName} ({TriggerKindLabel})"
        : LocalizationService.Get("Calendar.NoTrigger");

    public string TriggerKindLabel => TriggerMedia.Kind switch
    {
        MediaKind.Image => LocalizationService.Get("Calendar.MediaKind.Image"),
        MediaKind.Video => LocalizationService.Get("Calendar.MediaKind.Video"),
        MediaKind.Audio => LocalizationService.Get("Calendar.MediaKind.Audio"),
        _ => LocalizationService.Get("Calendar.MediaKind.None")
    };

    /// <summary>
    ///     Called by MainWindowViewModel on LocalizationService.LanguageChanged (see
    ///     LibraryItemViewModelBase.RefreshLocalizedText for the same pattern). Deliberately
    ///     does not touch Recurrence itself - that stored string only gets compared against the
    ///     current-language RecurrenceOptions elsewhere, never persisted, so re-deriving it here
    ///     would trigger NotifyChanged() and mark an untouched entry as edited just because the
    ///     UI language changed.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }

    partial void OnTitleChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnDescriptionChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnIconChanged(string value)
    {
        OnPropertyChanged(nameof(IconGeometry));
        NotifyChanged();
    }

    partial void OnStartDateTimeTextChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnRecurrenceChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnRepeatUntilTextChanged(string value)
    {
        NotifyChanged();
    }

    partial void OnIsPlayerVisibleChanged(bool value)
    {
        NotifyChanged();
    }

    partial void OnColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(ColorBrush));
        NotifyChanged();
    }

    partial void OnTargetSceneIdChanged(Guid? value)
    {
        NotifyChanged();
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    /// <summary>Clears TargetSceneId - the picker itself has no built-in "none" selection once a
    ///     Scene is chosen (it's a plain ComboBox over a live SceneLibrary list, not a nullable
    ///     enum), so this button is the only way back to "no target Scene".</summary>
    [RelayCommand]
    private void ClearTargetScene() => TargetSceneId = null;

    public bool TryBuildDefinition(out CalendarEntryDefinition definition)
    {
        definition = new CalendarEntryDefinition();
        if (!TryParseStart(out var start))
        {
            ValidationMessage = "Start muss im Format JJJJ-MM-TT HH:MM:SS angegeben werden.";
            return false;
        }

        if (!TryParseRepeatUntil(out var repeatUntil))
        {
            ValidationMessage = "Enddatum muss im Format JJJJ-MM-TT angegeben werden.";
            return false;
        }

        if (repeatUntil.HasValue && CalendarService.Active.ToDayNumber(repeatUntil.Value) < CalendarService.Active.ToDayNumber(start))
        {
            ValidationMessage = "Enddatum darf nicht vor dem Start liegen.";
            return false;
        }

        ValidationMessage = null;
        definition = new CalendarEntryDefinition
        {
            Id = Id,
            Title = Title.Trim(),
            Description = Description.Trim(),
            Icon = VisualItemHelper.NormalizeIcon(Icon),
            ColorHex = ColorHex,
            Start = start,
            RecurrenceKind = ToRecurrenceKind(Recurrence),
            RepeatUntil = repeatUntil,
            IsPlayerVisible = IsPlayerVisible,
            TriggerPath = TriggerMedia.Path,
            TriggerFileName = TriggerMedia.FileName,
            TriggerKind = TriggerMedia.Kind,
            TriggerFullscreen = TriggerMedia.Fullscreen,
            TriggerPauseClockDuringVideo = TriggerMedia.PauseClockDuringVideo,
            TriggerLoop = TriggerMedia.Loop,
            TargetSceneId = TargetSceneId
        };
        return true;
    }

    public static CalendarEntryViewModel FromDefinition(
        CalendarEntryDefinition definition,
        Action<CalendarEntryViewModel> onChanged,
        Action<CalendarEntryViewModel> onDeleteRequested)
    {
        var vm = new CalendarEntryViewModel(onChanged, onDeleteRequested);
        vm._suppressChangeNotifications = true;
        vm.Id = definition.Id == Guid.Empty ? Guid.NewGuid() : definition.Id;
        vm.Title = definition.Title;
        vm.Description = definition.Description;
        vm.Icon = VisualItemHelper.NormalizeIcon(definition.Icon);
        vm.ColorHex = definition.ColorHex;
        vm.StartDateTimeText = CalendarService.Active.FormatDateTimeText(definition.Start);
        vm.Recurrence = FromRecurrenceKind(definition.RecurrenceKind);
        vm.RepeatUntilText = definition.RepeatUntil.HasValue
            ? CalendarService.Active.FormatDateText(definition.RepeatUntil.Value)
            : string.Empty;
        vm.IsPlayerVisible = definition.IsPlayerVisible;
        vm.TriggerMedia.Path = definition.TriggerPath;
        vm.TriggerMedia.FileName = definition.TriggerFileName;
        vm.TriggerMedia.Kind = definition.TriggerKind;
        vm.TriggerMedia.Fullscreen = definition.TriggerFullscreen;
        vm.TriggerMedia.PauseClockDuringVideo = definition.TriggerPauseClockDuringVideo;
        vm.TriggerMedia.Loop = definition.TriggerLoop;
        vm.TargetSceneId = definition.TargetSceneId;
        vm._suppressChangeNotifications = false;
        vm.Validate();
        return vm;
    }

    private void NotifyChanged()
    {
        if (_suppressChangeNotifications)
            return;

        Validate();
        _onChanged(this);
        OnPropertyChanged(nameof(TriggerSummary));
        OnPropertyChanged(nameof(TriggerKindLabel));
    }

    private void Validate()
    {
        if (!TryParseStart(out _))
        {
            ValidationMessage = "Start muss im Format JJJJ-MM-TT HH:MM:SS angegeben werden.";
            return;
        }

        if (!TryParseRepeatUntil(out _))
        {
            ValidationMessage = "Enddatum muss im Format JJJJ-MM-TT angegeben werden.";
            return;
        }

        ValidationMessage = null;
    }

    private bool TryParseStart(out GameInstant start)
    {
        return CalendarService.Active.TryParseDateTimeText(StartDateTimeText?.Trim(), out start);
    }

    private bool TryParseRepeatUntil(out GameInstant? repeatUntil)
    {
        if (string.IsNullOrWhiteSpace(RepeatUntilText))
        {
            repeatUntil = null;
            return true;
        }

        if (CalendarService.Active.TryParseDateText(RepeatUntilText.Trim(), out var parsed))
        {
            repeatUntil = parsed;
            return true;
        }

        repeatUntil = null;
        return false;
    }

    public static CalendarRecurrenceKind ToRecurrenceKind(string recurrence)
    {
        // RecurrenceDaily/Weekly/... are localized display strings (not compile-time
        // constants), so this can't be a constant-pattern switch anymore - compare by value
        // instead, against whichever language is currently active.
        if (recurrence == RecurrenceDaily) return CalendarRecurrenceKind.Daily;
        if (recurrence == RecurrenceWeekly) return CalendarRecurrenceKind.Weekly;
        if (recurrence == RecurrenceMonthly) return CalendarRecurrenceKind.Monthly;
        if (recurrence == RecurrenceYearly) return CalendarRecurrenceKind.Yearly;
        return CalendarRecurrenceKind.None;
    }

    private static string FromRecurrenceKind(CalendarRecurrenceKind kind)
    {
        return kind switch
        {
            CalendarRecurrenceKind.Daily => RecurrenceDaily,
            CalendarRecurrenceKind.Weekly => RecurrenceWeekly,
            CalendarRecurrenceKind.Monthly => RecurrenceMonthly,
            CalendarRecurrenceKind.Yearly => RecurrenceYearly,
            _ => RecurrenceNone
        };
    }
}