using System;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     A GM calendar entry with optional recurrence. Recurrence is computed against a
///     CalendarDefinition (passed in by the caller, since an entry doesn't own a copy of the
///     campaign's active calendar) rather than assuming Gregorian rules - see
///     CalendarDefinition.ToCalendarDate/FromCalendarDate for the month/day/leap-year math this
///     class builds on.
/// </summary>
public sealed class CalendarEntryDefinition
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = VisualItemHelper.IconCalendar;
    public string ColorHex { get; set; } = string.Empty;
    public GameInstant Start { get; set; }
    public CalendarRecurrenceKind RecurrenceKind { get; set; }
    public GameInstant? RepeatUntil { get; set; }
    public bool IsPlayerVisible { get; set; }
    public string? TriggerPath { get; set; }
    public string? TriggerFileName { get; set; }
    public MediaKind TriggerKind { get; set; } = MediaKind.None;
    public bool TriggerFullscreen { get; set; }
    public bool TriggerPauseClockDuringVideo { get; set; }
    public bool TriggerLoop { get; set; }

    public bool HasTrigger => TriggerKind != MediaKind.None && !string.IsNullOrWhiteSpace(TriggerPath);

    public bool TryGetOccurrenceOn(CalendarDefinition calendar, GameInstant date, out GameInstant occurrence)
    {
        var targetDayNumber = calendar.ToDayNumber(date);
        var startDayNumber = calendar.ToDayNumber(Start);
        var startDayStart = calendar.DayStart(Start);
        var timeOfDaySeconds = Start.TotalSeconds - startDayStart.TotalSeconds;
        occurrence = new GameInstant(calendar.DayStart(date).TotalSeconds + timeOfDaySeconds);

        if (targetDayNumber < startDayNumber)
            return false;

        if (RepeatUntil.HasValue && targetDayNumber > calendar.ToDayNumber(RepeatUntil.Value))
            return false;

        switch (RecurrenceKind)
        {
            case CalendarRecurrenceKind.None:
                return targetDayNumber == startDayNumber;

            case CalendarRecurrenceKind.Daily:
                return true;

            case CalendarRecurrenceKind.Weekly:
                var weekLength = calendar.Weekdays.Count > 0 ? calendar.Weekdays.Count : 7;
                return Mod(targetDayNumber - startDayNumber, weekLength) == 0;

            case CalendarRecurrenceKind.Monthly:
            {
                var targetDate = calendar.ToCalendarDate(date);
                var startDate = calendar.ToCalendarDate(Start);
                if (targetDate.Day != startDate.Day)
                    return false;

                var monthCount = calendar.Months.Count;
                var monthDistance = ToAbsoluteMonth(targetDate.Year, targetDate.MonthIndex, monthCount) -
                                     ToAbsoluteMonth(startDate.Year, startDate.MonthIndex, monthCount);
                return monthDistance >= 0;
            }

            case CalendarRecurrenceKind.Yearly:
            {
                var targetDate = calendar.ToCalendarDate(date);
                var startDate = calendar.ToCalendarDate(Start);
                return targetDate.MonthIndex == startDate.MonthIndex && targetDate.Day == startDate.Day;
            }

            default:
                return false;
        }
    }

    public bool HasOccurrenceInMonth(CalendarDefinition calendar, GameInstant month)
    {
        var monthDate = calendar.ToCalendarDate(month);
        var internalYear = monthDate.Year - calendar.YearZeroOffset;
        var days = calendar.DaysInMonth(internalYear, monthDate.MonthIndex);
        var first = calendar.FromCalendarDate(monthDate.Year, monthDate.MonthIndex, 1, 0, 0, 0);

        for (var day = 0; day < days; day++)
            if (TryGetOccurrenceOn(calendar, first.Add(TimeSpan.FromSeconds((double)day * calendar.SecondsPerDay)), out _))
                return true;

        return false;
    }

    public GameInstant? GetNextOccurrenceAtOrAfter(CalendarDefinition calendar, GameInstant from)
    {
        if (RepeatUntil.HasValue && calendar.ToDayNumber(from) > calendar.ToDayNumber(RepeatUntil.Value))
            return null;

        return RecurrenceKind switch
        {
            CalendarRecurrenceKind.None => Start >= from ? Start : null,
            CalendarRecurrenceKind.Daily => ClampToEnd(calendar, NextDaily(calendar, from)),
            CalendarRecurrenceKind.Weekly => ClampToEnd(calendar, NextWeekly(calendar, from)),
            CalendarRecurrenceKind.Monthly => ClampToEnd(calendar, NextMonthly(calendar, from)),
            CalendarRecurrenceKind.Yearly => ClampToEnd(calendar, NextYearly(calendar, from)),
            _ => null
        };
    }

    private GameInstant? ClampToEnd(CalendarDefinition calendar, GameInstant? candidate)
    {
        if (candidate is null)
            return null;

        if (RepeatUntil.HasValue && calendar.ToDayNumber(candidate.Value) > calendar.ToDayNumber(RepeatUntil.Value))
            return null;

        return candidate;
    }

    private GameInstant NextDaily(CalendarDefinition calendar, GameInstant from)
    {
        if (from <= Start)
            return Start;

        var days = calendar.ToDayNumber(from) - calendar.ToDayNumber(Start);
        var candidate = Start.Add(TimeSpan.FromSeconds((double)days * calendar.SecondsPerDay));
        return candidate >= from ? candidate : candidate.Add(TimeSpan.FromSeconds(calendar.SecondsPerDay));
    }

    private GameInstant NextWeekly(CalendarDefinition calendar, GameInstant from)
    {
        if (from <= Start)
            return Start;

        var weekLength = calendar.Weekdays.Count > 0 ? calendar.Weekdays.Count : 7;
        var totalDays = calendar.ToDayNumber(from) - calendar.ToDayNumber(Start);
        var weeks = Math.Max(0, totalDays / weekLength);
        var candidate = Start.Add(TimeSpan.FromSeconds((double)weeks * weekLength * calendar.SecondsPerDay));
        while (candidate < from)
            candidate = candidate.Add(TimeSpan.FromSeconds((double)weekLength * calendar.SecondsPerDay));

        return candidate;
    }

    private GameInstant? NextMonthly(CalendarDefinition calendar, GameInstant from)
    {
        if (from <= Start)
            return Start;

        var startDate = calendar.ToCalendarDate(Start);
        var fromDate = calendar.ToCalendarDate(from);
        var monthCount = calendar.Months.Count;
        var startAbsoluteMonth = ToAbsoluteMonth(startDate.Year, startDate.MonthIndex, monthCount);
        var monthCursor = Math.Max(ToAbsoluteMonth(fromDate.Year, fromDate.MonthIndex, monthCount), startAbsoluteMonth);

        while (true)
        {
            var (year, monthIndex) = FromAbsoluteMonth(monthCursor, monthCount);
            var internalYear = year - calendar.YearZeroOffset;
            var daysInMonth = calendar.DaysInMonth(internalYear, monthIndex);
            if (startDate.Day <= daysInMonth)
            {
                var candidate = calendar.FromCalendarDate(year, monthIndex, startDate.Day, startDate.Hour,
                    startDate.Minute, startDate.Second);
                if (candidate >= from)
                    return candidate;
            }

            monthCursor++;
            if (RepeatUntil.HasValue)
            {
                var (nextYear, nextMonthIndex) = FromAbsoluteMonth(monthCursor, monthCount);
                var probe = calendar.FromCalendarDate(nextYear, nextMonthIndex, 1, 0, 0, 0);
                if (calendar.ToDayNumber(probe) > calendar.ToDayNumber(RepeatUntil.Value))
                    return null;
            }
        }
    }

    private GameInstant? NextYearly(CalendarDefinition calendar, GameInstant from)
    {
        if (from <= Start)
            return Start;

        var startDate = calendar.ToCalendarDate(Start);
        var fromDate = calendar.ToCalendarDate(from);

        for (var year = fromDate.Year; year <= fromDate.Year + 200; year++)
        {
            var internalYear = year - calendar.YearZeroOffset;
            var daysInMonth = calendar.DaysInMonth(internalYear, startDate.MonthIndex);
            if (startDate.Day > daysInMonth)
                continue;

            var candidate = calendar.FromCalendarDate(year, startDate.MonthIndex, startDate.Day, startDate.Hour,
                startDate.Minute, startDate.Second);
            if (candidate < Start)
                continue;
            if (candidate >= from)
                return candidate;
            if (RepeatUntil.HasValue && calendar.ToDayNumber(candidate) > calendar.ToDayNumber(RepeatUntil.Value))
                return null;
        }

        return null;
    }

    private static int ToAbsoluteMonth(int year, int monthIndex, int monthCount) => year * monthCount + monthIndex;

    private static (int Year, int MonthIndex) FromAbsoluteMonth(int absoluteMonth, int monthCount)
    {
        var year = FloorDiv(absoluteMonth, monthCount);
        var monthIndex = Mod(absoluteMonth, monthCount);
        return (year, monthIndex);
    }

    private static long Mod(long a, long b)
    {
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) r += b;
        return r;
    }

    private static int FloorDiv(int a, int b)
    {
        var q = a / b;
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) q--;
        return q;
    }

    private static int Mod(int a, int b)
    {
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) r += b;
        return r;
    }
}
