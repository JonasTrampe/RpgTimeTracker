using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.ViewModels;

public partial class CalendarEntryViewModel : ObservableObject
{
    public const string RecurrenceNone = "Keine";
    public const string RecurrenceDaily = "Täglich";
    public const string RecurrenceWeekly = "Wöchentlich";
    public const string RecurrenceMonthly = "Monatlich";
    public const string RecurrenceYearly = "Jährlich";

    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"];
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
    [ObservableProperty] private string _title = "Neuer Termin";
    [ObservableProperty] private string? _validationMessage;

    public CalendarEntryViewModel(Action<CalendarEntryViewModel> onChanged,
        Action<CalendarEntryViewModel> onDeleteRequested)
    {
        Id = Guid.NewGuid();
        _onChanged = onChanged;
        _onDeleteRequested = onDeleteRequested;
        _startDateTimeText = DateTime.Now.ToString("yyyy-MM-dd 12:00:00", CultureInfo.InvariantCulture);
        TriggerMedia.PropertyChanged += (_, _) => NotifyChanged();
        Validate();
    }

    public Guid Id { get; set; }
    public TriggerMediaConfig TriggerMedia { get; } = new();
    public ObservableCollection<string> IconOptions => VisualItemHelper.IconOptions;
    public Geometry IconGeometry => VisualItemHelper.IconGeometry(Icon);
    public IBrush? ColorBrush => VisualItemHelper.TryBrush(ColorHex);

    public static IReadOnlyList<string> RecurrenceOptions { get; } =
    [
        RecurrenceNone,
        RecurrenceDaily,
        RecurrenceWeekly,
        RecurrenceMonthly,
        RecurrenceYearly
    ];

    public string TriggerSummary => TriggerMedia.HasMedia
        ? $"{TriggerMedia.FileName} ({TriggerKindLabel})"
        : "Kein Auslöser";

    public string TriggerKindLabel => TriggerMedia.Kind switch
    {
        MediaKind.Image => "Bild",
        MediaKind.Video => "Video",
        MediaKind.Audio => "Sound",
        _ => "Kein Medium"
    };

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

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

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

        if (repeatUntil.HasValue && repeatUntil.Value.Date < start.Date)
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
            TriggerLoop = TriggerMedia.Loop
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
        vm.StartDateTimeText = definition.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        vm.Recurrence = FromRecurrenceKind(definition.RecurrenceKind);
        vm.RepeatUntilText = definition.RepeatUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ??
                             string.Empty;
        vm.IsPlayerVisible = definition.IsPlayerVisible;
        vm.TriggerMedia.Path = definition.TriggerPath;
        vm.TriggerMedia.FileName = definition.TriggerFileName;
        vm.TriggerMedia.Kind = definition.TriggerKind;
        vm.TriggerMedia.Fullscreen = definition.TriggerFullscreen;
        vm.TriggerMedia.PauseClockDuringVideo = definition.TriggerPauseClockDuringVideo;
        vm.TriggerMedia.Loop = definition.TriggerLoop;
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

    private bool TryParseStart(out DateTime start)
    {
        return DateTime.TryParseExact((string?)StartDateTimeText?.Trim(), DateTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out start);
    }

    private bool TryParseRepeatUntil(out DateTime? repeatUntil)
    {
        if (string.IsNullOrWhiteSpace(RepeatUntilText))
        {
            repeatUntil = null;
            return true;
        }

        if (DateTime.TryParseExact((string?)RepeatUntilText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            repeatUntil = parsed.Date;
            return true;
        }

        repeatUntil = null;
        return false;
    }

    public static CalendarRecurrenceKind ToRecurrenceKind(string recurrence)
    {
        return recurrence switch
        {
            RecurrenceDaily => CalendarRecurrenceKind.Daily,
            RecurrenceWeekly => CalendarRecurrenceKind.Weekly,
            RecurrenceMonthly => CalendarRecurrenceKind.Monthly,
            RecurrenceYearly => CalendarRecurrenceKind.Yearly,
            _ => CalendarRecurrenceKind.None
        };
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