using System;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.Models;

public sealed class CalendarEntryDefinition
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = VisualItemHelper.IconCalendar;
    public string ColorHex { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public CalendarRecurrenceKind RecurrenceKind { get; set; }
    public DateTime? RepeatUntil { get; set; }
    public bool IsPlayerVisible { get; set; }
    public string? TriggerPath { get; set; }
    public string? TriggerFileName { get; set; }
    public MediaKind TriggerKind { get; set; } = MediaKind.None;
    public bool TriggerFullscreen { get; set; }
    public bool TriggerPauseClockDuringVideo { get; set; }
    public bool TriggerLoop { get; set; }

    public bool HasTrigger => TriggerKind != MediaKind.None && !string.IsNullOrWhiteSpace(TriggerPath);

    public bool TryGetOccurrenceOn(DateTime date, out DateTime occurrence)
    {
        var targetDate = date.Date;
        occurrence = targetDate.Add(Start.TimeOfDay);

        if (targetDate < Start.Date)
            return false;

        if (RepeatUntil.HasValue && targetDate > RepeatUntil.Value.Date)
            return false;

        switch (RecurrenceKind)
        {
            case CalendarRecurrenceKind.None:
                return targetDate == Start.Date;

            case CalendarRecurrenceKind.Daily:
                return true;

            case CalendarRecurrenceKind.Weekly:
                return (targetDate - Start.Date).Days % 7 == 0;

            case CalendarRecurrenceKind.Monthly:
                if (targetDate.Day != Start.Day)
                    return false;

                var monthDistance = (targetDate.Year - Start.Year) * 12 + targetDate.Month - Start.Month;
                return monthDistance >= 0;

            case CalendarRecurrenceKind.Yearly:
                return targetDate.Month == Start.Month && targetDate.Day == Start.Day;

            default:
                return false;
        }
    }

    public bool HasOccurrenceInMonth(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        for (var day = 0; day < days; day++)
            if (TryGetOccurrenceOn(first.AddDays(day), out _))
                return true;

        return false;
    }

    public DateTime? GetNextOccurrenceAtOrAfter(DateTime from)
    {
        if (RepeatUntil.HasValue && from.Date > RepeatUntil.Value.Date)
            return null;

        switch (RecurrenceKind)
        {
            case CalendarRecurrenceKind.None:
                return Start >= from ? Start : null;

            case CalendarRecurrenceKind.Daily:
                return ClampToEnd(NextDaily(from));

            case CalendarRecurrenceKind.Weekly:
                return ClampToEnd(NextWeekly(from));

            case CalendarRecurrenceKind.Monthly:
                return ClampToEnd(NextMonthly(from));

            case CalendarRecurrenceKind.Yearly:
                return ClampToEnd(NextYearly(from));

            default:
                return null;
        }
    }

    private DateTime? ClampToEnd(DateTime? candidate)
    {
        if (candidate is null)
            return null;

        if (RepeatUntil.HasValue && candidate.Value.Date > RepeatUntil.Value.Date)
            return null;

        return candidate;
    }

    private DateTime NextDaily(DateTime from)
    {
        if (from <= Start)
            return Start;

        var days = (from.Date - Start.Date).Days;
        var candidate = Start.AddDays(days);
        return candidate >= from ? candidate : candidate.AddDays(1);
    }

    private DateTime NextWeekly(DateTime from)
    {
        if (from <= Start)
            return Start;

        var totalDays = (from.Date - Start.Date).Days;
        var weeks = Math.Max(0, totalDays / 7);
        var candidate = Start.AddDays(weeks * 7);
        while (candidate < from)
            candidate = candidate.AddDays(7);

        return candidate;
    }

    private DateTime? NextMonthly(DateTime from)
    {
        if (from <= Start)
            return Start;

        var monthCursor = new DateTime(from.Year, from.Month, 1);
        while (true)
        {
            if (monthCursor < new DateTime(Start.Year, Start.Month, 1))
                monthCursor = new DateTime(Start.Year, Start.Month, 1);

            var daysInMonth = DateTime.DaysInMonth(monthCursor.Year, monthCursor.Month);
            if (Start.Day <= daysInMonth)
            {
                var candidate = new DateTime(monthCursor.Year, monthCursor.Month, Start.Day,
                    Start.Hour, Start.Minute, Start.Second);
                if (candidate >= from)
                    return candidate;
            }

            monthCursor = monthCursor.AddMonths(1);
            if (RepeatUntil.HasValue && monthCursor.Date > RepeatUntil.Value.Date)
                return null;
        }
    }

    private DateTime? NextYearly(DateTime from)
    {
        if (from <= Start)
            return Start;

        for (var year = from.Year; year <= from.Year + 200; year++)
        {
            if (Start.Month == 2 && Start.Day == 29 && !DateTime.IsLeapYear(year))
                continue;

            var candidate = new DateTime(year, Start.Month, Start.Day, Start.Hour, Start.Minute, Start.Second);
            if (candidate < Start)
                continue;
            if (candidate >= from)
                return candidate;
            if (RepeatUntil.HasValue && candidate.Date > RepeatUntil.Value.Date)
                return null;
        }

        return null;
    }
}