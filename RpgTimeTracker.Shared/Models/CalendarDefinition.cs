using System;
using System.Collections.Generic;
using System.Linq;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     One named month: its length in days, and whether it's intercalary (a "leap month"
///     inserted outside the normal month sequence - doesn't count toward normal week-day
///     cycling in most real-world calendars that have them, but modeled here purely as
///     descriptive data since nothing currently needs that distinction functionally).
/// </summary>
public sealed class CalendarMonthDefinition
{
    public string Name { get; set; } = string.Empty;
    public int Days { get; set; }
    public bool IsIntercalary { get; set; }
}

public enum CalendarLeapYearRuleKind
{
    None,
    Interval,

    /// <summary>
    ///     The real Gregorian rule: a leap year every 4 years, except centuries (divisible by
    ///     100) unless also divisible by 400 - e.g. 2000 was a leap year, 1900 and 2100 are not.
    ///     IntervalYears is ignored for this kind (it's always base-4/100/400); MonthIndexAffected/
    ///     ExtraDays still apply, since those describe *where* the extra day goes, not *when*.
    /// </summary>
    Gregorian
}

/// <summary>
///     How many extra days get added to which month, and how often. `IntervalYears = 4,
///     MonthIndexAffected = 1, ExtraDays = 1` reproduces the Gregorian Feb-29 rule.
/// </summary>
public sealed class CalendarLeapYearRule
{
    public CalendarLeapYearRuleKind Kind { get; set; } = CalendarLeapYearRuleKind.None;
    public int IntervalYears { get; set; }
    public int MonthIndexAffected { get; set; }
    public int ExtraDays { get; set; } = 1;
}

/// <summary>
///     A named span of the year, purely descriptive (display only) - not wired to any
///     game logic yet.
/// </summary>
public sealed class CalendarSeason
{
    public string Name { get; set; } = string.Empty;
    public int StartMonthIndex { get; set; }
    public int StartDay { get; set; } = 1;
    public string ColorHex { get; set; } = string.Empty;
}

/// <summary>
///     A tracked moon: a repeating cycle of `CycleLengthDays` days, anchored to a known
///     new-moon date. Purely descriptive (display only) - not wired to any game logic yet.
/// </summary>
public sealed class CalendarMoon
{
    public string Name { get; set; } = string.Empty;
    public double CycleLengthDays { get; set; }
    public int FirstNewMoonYear { get; set; }
    public int FirstNewMoonMonthIndex { get; set; }
    public int FirstNewMoonDay { get; set; } = 1;
    public string ColorHex { get; set; } = string.Empty;
}

/// <summary>
///     A named recurring event bundled with a calendar (e.g. Harptos's five festivals) -
///     month/day-based rather than an absolute GameInstant, since the template ships with the
///     calendar itself, not with any particular campaign's start year. Converted to a real
///     CalendarEntryDefinition (anchored to a specific year, RecurrenceKind.Yearly) on demand via
///     CalendarDefinition.BuildDefaultEntry, not automatically - the GM opts in per calendar.
/// </summary>
public sealed class CalendarEventTemplate
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MonthIndex { get; set; }

    /// <summary>1-based day of month.</summary>
    public int Day { get; set; } = 1;

    public int Hour { get; set; }
    public int Minute { get; set; }
    public string ColorHex { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

public readonly struct MoonPhase
{
    public required string MoonName { get; init; }

    /// <summary>0 = new moon, 0.5 = full moon, range [0,1).</summary>
    public required double PhaseFraction { get; init; }
}

/// <summary>
///     A calendar date broken out of a GameInstant by a CalendarDefinition - the
///     human-readable form used for display and calendar-entry matching.
/// </summary>
public readonly struct CalendarDate
{
    public required int Year { get; init; }
    public required int MonthIndex { get; init; }
    public required string MonthName { get; init; }

    /// <summary>1-based day of month.</summary>
    public required int Day { get; init; }

    public required int WeekdayIndex { get; init; }
    public required string WeekdayName { get; init; }
    public required int Hour { get; init; }
    public required int Minute { get; init; }
    public required int Second { get; init; }
    public string? ActiveSeasonName { get; init; }
    public IReadOnlyList<MoonPhase> MoonPhases { get; init; } = [];

    public CalendarDate()
    {
    }
}

/// <summary>
///     A fully custom, exchangeable calendar - month names/lengths, weekday names, a leap-year
///     rule, configurable hours/day-minutes/hour-seconds/minute, and descriptive seasons/moons,
///     matching Foundry VTT Simple Calendar's predefined-calendar format closely enough that its
///     bundled calendars can be adapted to this schema. Converts a calendar-agnostic GameInstant
///     to/from a human CalendarDate; GameClockService itself never needs to know about any of
///     this - only display code and CalendarEntryDefinition's recurrence matching do.
/// </summary>
public sealed class CalendarDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<CalendarMonthDefinition> Months { get; set; } = [];
    public List<string> Weekdays { get; set; } = [];

    /// <summary>Which weekday index GameInstant.Zero (day 0) falls on.</summary>
    public int FirstWeekdayIndex { get; set; }

    public CalendarLeapYearRule LeapYear { get; set; } = new();
    public int HoursPerDay { get; set; } = 24;
    public int MinutesPerHour { get; set; } = 60;
    public int SecondsPerMinute { get; set; } = 60;

    /// <summary>
    ///     Added to the computed year number purely for display (e.g. so year 0 internally
    ///     can be shown as "Year 500").
    /// </summary>
    public int YearZeroOffset { get; set; }

    public List<CalendarSeason> Seasons { get; set; } = [];
    public List<CalendarMoon> Moons { get; set; } = [];

    /// <summary>
    ///     Optional named recurring events bundled with this calendar (e.g. Harptos's
    ///     festivals) - see CalendarEventTemplate. Empty for calendars with no notable holidays
    ///     (or none authored yet).
    /// </summary>
    public List<CalendarEventTemplate> DefaultEntries { get; set; } = [];

    public int SecondsPerDay => HoursPerDay * MinutesPerHour * SecondsPerMinute;

    /// <summary>
    ///     The number of whole days elapsed at/before this instant (floor-divided, so
    ///     negative instants before the epoch still give a consistent day number).
    /// </summary>
    public long ToDayNumber(GameInstant instant)
    {
        return FloorDiv(instant.TotalSeconds, SecondsPerDay);
    }

    /// <summary>The instant at 00:00:00 of the day this instant falls on.</summary>
    public GameInstant DayStart(GameInstant instant)
    {
        return new GameInstant(ToDayNumber(instant) * SecondsPerDay);
    }

    public bool IsLeapYear(int year)
    {
        return LeapYear.Kind switch
        {
            CalendarLeapYearRuleKind.Interval => LeapYear.IntervalYears > 0 && Mod(year, LeapYear.IntervalYears) == 0,
            CalendarLeapYearRuleKind.Gregorian => (Mod(year, 4) == 0 && Mod(year, 100) != 0) || Mod(year, 400) == 0,
            _ => false
        };
    }

    public int DaysInMonth(int year, int monthIndex)
    {
        var days = Months[monthIndex].Days;
        if (IsLeapYear(year) && monthIndex == LeapYear.MonthIndexAffected) days += LeapYear.ExtraDays;
        return days;
    }

    public int DaysInYear(int year)
    {
        var total = Months.Sum(m => m.Days);
        if (IsLeapYear(year)) total += LeapYear.ExtraDays;
        return total;
    }

    public CalendarDate ToCalendarDate(GameInstant instant)
    {
        var secondsPerDay = SecondsPerDay;
        var totalDays = FloorDiv(instant.TotalSeconds, secondsPerDay);
        var secondsOfDay = Mod(instant.TotalSeconds, secondsPerDay);

        var hour = (int)(secondsOfDay / (MinutesPerHour * SecondsPerMinute));
        var minute = (int)(secondsOfDay / SecondsPerMinute % MinutesPerHour);
        var second = (int)(secondsOfDay % SecondsPerMinute);

        var weekdayIndex = Weekdays.Count > 0 ? (int)Mod(totalDays + FirstWeekdayIndex, Weekdays.Count) : 0;

        var (year, dayOfYear) = SplitYearDay(totalDays);
        var (monthIndex, day) = SplitMonthDay(year, dayOfYear);

        return new CalendarDate
        {
            Year = year + YearZeroOffset,
            MonthIndex = monthIndex,
            MonthName = Months.Count > 0 ? Months[monthIndex].Name : string.Empty,
            Day = day,
            WeekdayIndex = weekdayIndex,
            WeekdayName = Weekdays.Count > 0 ? Weekdays[weekdayIndex] : string.Empty,
            Hour = hour,
            Minute = minute,
            Second = second,
            ActiveSeasonName = GetActiveSeasonName(monthIndex, day),
            MoonPhases = GetMoonPhases(totalDays)
        };
    }

    /// <summary>
    ///     Reproduces today's exact (pre-custom-calendar) behavior: 12 Gregorian months,
    ///     7-day weeks, the standard leap-year rule, 24h/60m/60s - the default for a fresh
    ///     campaign that hasn't picked a different calendar.
    /// </summary>
    public static CalendarDefinition CreateGregorian()
    {
        return new CalendarDefinition
        {
            Name = "Gregorian",
            Months =
            [
                new CalendarMonthDefinition { Name = "January", Days = 31 },
                new CalendarMonthDefinition { Name = "February", Days = 28 },
                new CalendarMonthDefinition { Name = "March", Days = 31 },
                new CalendarMonthDefinition { Name = "April", Days = 30 },
                new CalendarMonthDefinition { Name = "May", Days = 31 },
                new CalendarMonthDefinition { Name = "June", Days = 30 },
                new CalendarMonthDefinition { Name = "July", Days = 31 },
                new CalendarMonthDefinition { Name = "August", Days = 31 },
                new CalendarMonthDefinition { Name = "September", Days = 30 },
                new CalendarMonthDefinition { Name = "October", Days = 31 },
                new CalendarMonthDefinition { Name = "November", Days = 30 },
                new CalendarMonthDefinition { Name = "December", Days = 31 }
            ],
            Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
            FirstWeekdayIndex = 0,
            LeapYear = new CalendarLeapYearRule
            {
                Kind = CalendarLeapYearRuleKind.Interval,
                IntervalYears = 4,
                MonthIndexAffected = 1,
                ExtraDays = 1
            },
            HoursPerDay = 24,
            MinutesPerHour = 60,
            SecondsPerMinute = 60
        };
    }

    /// <summary>
    ///     Parses a "yyyy-MM-dd HH:mm:ss"-shaped string against this calendar's own
    ///     month/day/hour bounds (not Gregorian ones) - used by date-entry controls/ViewModels
    ///     instead of DateTime.TryParseExact, which would reject or misinterpret a non-Gregorian
    ///     calendar's numbers.
    /// </summary>
    public bool TryParseDateTimeText(string? text, out GameInstant instant)
    {
        instant = GameInstant.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(['-', ' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var year)) return false;
        if (!int.TryParse(parts[1], out var month)) return false;
        if (!int.TryParse(parts[2], out var day)) return false;

        var hour = parts.Length > 3 && int.TryParse(parts[3], out var h) ? h : 0;
        var minute = parts.Length > 4 && int.TryParse(parts[4], out var m) ? m : 0;
        var second = parts.Length > 5 && int.TryParse(parts[5], out var s) ? s : 0;

        instant = FromCalendarDate(year, month - 1, day, hour, minute, second);
        return true;
    }

    public string FormatDateTimeText(GameInstant instant)
    {
        var date = ToCalendarDate(instant);
        return
            $"{date.Year:0000}-{date.MonthIndex + 1:00}-{date.Day:00} {date.Hour:00}:{date.Minute:00}:{date.Second:00}";
    }

    /// <summary>
    ///     Date-only counterpart of TryParseDateTimeText (no time component - defaults to
    ///     the start of the day).
    /// </summary>
    public bool TryParseDateText(string? text, out GameInstant instant)
    {
        instant = GameInstant.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var year)) return false;
        if (!int.TryParse(parts[1], out var month)) return false;
        if (!int.TryParse(parts[2], out var day)) return false;

        instant = FromCalendarDate(year, month - 1, day, 0, 0, 0);
        return true;
    }

    public string FormatDateText(GameInstant instant)
    {
        var date = ToCalendarDate(instant);
        return $"{date.Year:0000}-{date.MonthIndex + 1:00}-{date.Day:00}";
    }

    /// <summary>
    ///     Anchors a DefaultEntries template to a concrete year, producing a real yearly-
    ///     recurring CalendarEntryDefinition the GM can add to their campaign - see
    ///     CalendarEventTemplate's doc comment for why this isn't done automatically.
    /// </summary>
    public CalendarEntryDefinition BuildDefaultEntry(CalendarEventTemplate template, int year)
    {
        return new CalendarEntryDefinition
        {
            Id = Guid.NewGuid(),
            Title = template.Title,
            Description = template.Description,
            Icon = string.IsNullOrWhiteSpace(template.Icon) ? "Bootstrap: Calendar" : template.Icon,
            ColorHex = template.ColorHex,
            Start = FromCalendarDate(year, template.MonthIndex, template.Day, template.Hour, template.Minute, 0),
            RecurrenceKind = CalendarRecurrenceKind.Yearly,
            IsPlayerVisible = true
        };
    }

    public GameInstant FromCalendarDate(int year, int monthIndex, int day, int hour, int minute, int second)
    {
        var internalYear = year - YearZeroOffset;
        var totalDays = YearMonthDayToDays(internalYear, monthIndex, day);
        var totalSeconds = totalDays * SecondsPerDay
                           + (long)hour * MinutesPerHour * SecondsPerMinute
                           + (long)minute * SecondsPerMinute
                           + second;
        return new GameInstant(totalSeconds);
    }

    private long YearMonthDayToDays(int year, int monthIndex, int day)
    {
        long totalDays = 0;
        if (year >= 0)
            for (var y = 0; y < year; y++)
                totalDays += DaysInYear(y);
        else
            for (var y = year; y < 0; y++)
                totalDays -= DaysInYear(y);

        for (var m = 0; m < monthIndex; m++) totalDays += DaysInMonth(year, m);
        totalDays += day - 1;
        return totalDays;
    }

    private (int Year, int DayOfYear) SplitYearDay(long totalDays)
    {
        var year = 0;
        var remaining = totalDays;
        if (remaining >= 0)
            while (remaining >= DaysInYear(year))
            {
                remaining -= DaysInYear(year);
                year++;
            }
        else
            while (remaining < 0)
            {
                year--;
                remaining += DaysInYear(year);
            }

        return (year, (int)remaining);
    }

    private (int MonthIndex, int Day) SplitMonthDay(int year, int dayOfYear)
    {
        var remaining = dayOfYear;
        for (var m = 0; m < Months.Count; m++)
        {
            var len = DaysInMonth(year, m);
            if (remaining < len) return (m, remaining + 1);
            remaining -= len;
        }

        return (Math.Max(Months.Count - 1, 0), Months.Count > 0 ? DaysInMonth(year, Months.Count - 1) : 1);
    }

    private string? GetActiveSeasonName(int monthIndex, int day)
    {
        if (Seasons.Count == 0) return null;

        var ordered = Seasons.OrderBy(s => s.StartMonthIndex).ThenBy(s => s.StartDay).ToList();
        CalendarSeason? active = null;
        foreach (var season in ordered)
            if (season.StartMonthIndex < monthIndex ||
                (season.StartMonthIndex == monthIndex && season.StartDay <= day))
                active = season;

        return (active ?? ordered[^1]).Name;
    }

    private List<MoonPhase> GetMoonPhases(long totalDays)
    {
        var phases = new List<MoonPhase>(Moons.Count);
        foreach (var moon in Moons)
        {
            if (moon.CycleLengthDays <= 0) continue;

            var anchorDays = YearMonthDayToDays(moon.FirstNewMoonYear - YearZeroOffset, moon.FirstNewMoonMonthIndex,
                moon.FirstNewMoonDay);
            var daysSinceAnchor = totalDays - anchorDays;
            var fraction = DoubleMod(daysSinceAnchor, moon.CycleLengthDays) / moon.CycleLengthDays;
            phases.Add(new MoonPhase { MoonName = moon.Name, PhaseFraction = fraction });
        }

        return phases;
    }

    private static long FloorDiv(long a, long b)
    {
        var q = a / b;
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) q--;
        return q;
    }

    private static long Mod(long a, long b)
    {
        var r = a % b;
        if (r != 0 && r < 0 != b < 0) r += b;
        return r;
    }

    private static double DoubleMod(double a, double b)
    {
        var r = a % b;
        if (r < 0) r += b;
        return r;
    }
}