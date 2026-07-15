using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Converts a Foundry VTT "Simple Calendar" module predefined-calendar export (the raw JSON
///     files at https://github.com/vigoren/foundryvtt-simple-calendar/tree/main/src/predefined-calendars,
///     e.g. "harptos.json") into our own CalendarDefinition schema. The two schemas are structurally
///     different (different field names/shapes for weekdays, leap years, moons, etc. - see this
///     class's mapping comments), so a Simple Calendar file is not directly usable by
///     CalendarDefinitionLoader without this conversion step.
///     Known approximations (documented here rather than silently swallowed):
///     - Weekday-epoch alignment (which weekday GameInstant.Zero falls on) is inherently
///     approximate: the two engines compute elapsed days from their epoch differently, so
///     FirstWeekdayIndex is carried over as-is but is not guaranteed to reproduce the exact
///     real-world weekday of any specific historical date from the source calendar.
///     - "none", "gregorian", and "custom" leap-year rules are all represented exactly (gregorian
///     maps to CalendarLeapYearRuleKind.Gregorian - the real 4/100/400 rule, not an Interval(4)
///     approximation); other named built-in rules (if present) fall back to "none" with a
///     warning in the result.
///     - Some (not all) predefined-calendar exports bundle a sibling top-level "notes" array -
///     holidays/festivals authored against that calendar, e.g. dsa-tde5e.json's 19 real
///     Aventurian feast days. Only Yearly-repeating notes (Simple Calendar's NoteRepeat.Yearly,
///     value 3) are imported into CalendarDefinition.DefaultEntries, since that's the only
///     recurrence CalendarEventTemplate/BuildDefaultEntry supports; Never/Weekly/Monthly notes
///     are skipped with a warning. HTML in a note's "content" is stripped to plain text (a rough
///     tag-strip, not full HTML parsing) since our Description field is plain text.
/// </summary>
public static class SimpleCalendarImporter
{
    /// <summary>
    ///     True if the given JSON looks like a Simple Calendar export (has a top-level
    ///     "calendar" object) rather than our own CalendarDefinition shape.
    /// </summary>
    public static bool LooksLikeSimpleCalendarFormat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("calendar", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Attempts the conversion. On success, `warnings` lists any approximations applied
    ///     (empty if the conversion was exact); on failure, `error` explains why.
    /// </summary>
    public static bool TryConvert(string json, string calendarName, out CalendarDefinition? definition,
        out List<string> warnings, out string? error)
    {
        definition = null;
        warnings = [];
        error = null;

        JsonElement cal;
        JsonElement? notesEl = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("calendar", out cal))
            {
                error = "No top-level \"calendar\" object found - this doesn't look like a Simple Calendar export.";
                return false;
            }

            cal = cal.Clone();
            if (doc.RootElement.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                notesEl = notes.Clone();
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        var result = new CalendarDefinition { Name = calendarName };

        if (cal.TryGetProperty("months", out var monthsEl) && monthsEl.ValueKind == JsonValueKind.Array)
            foreach (var m in monthsEl.EnumerateArray())
                result.Months.Add(new CalendarMonthDefinition
                {
                    Name = GetString(m, "name"),
                    Days = GetInt(m, "numberOfDays"),
                    IsIntercalary = GetBool(m, "intercalary")
                });

        if (cal.TryGetProperty("weekdays", out var weekdaysEl) && weekdaysEl.ValueKind == JsonValueKind.Array)
            foreach (var w in weekdaysEl.EnumerateArray())
                result.Weekdays.Add(GetString(w, "name"));

        if (cal.TryGetProperty("year", out var yearEl))
        {
            // firstWeekday is Simple Calendar's own weekday-epoch anchor - carried over as the
            // closest equivalent, see this class's doc comment on why exact alignment isn't
            // guaranteed across two differently-implemented calendar engines.
            result.FirstWeekdayIndex = GetInt(yearEl, "firstWeekday");
            result.YearZeroOffset = GetInt(yearEl, "numericRepresentation");
        }

        if (cal.TryGetProperty("time", out var timeEl))
        {
            result.HoursPerDay = GetInt(timeEl, "hoursInDay", 24);
            result.MinutesPerHour = GetInt(timeEl, "minutesInHour", 60);
            result.SecondsPerMinute = GetInt(timeEl, "secondsInMinute", 60);
        }

        result.LeapYear = ConvertLeapYear(cal, result.Months, warnings);

        if (cal.TryGetProperty("seasons", out var seasonsEl) && seasonsEl.ValueKind == JsonValueKind.Array)
            foreach (var s in seasonsEl.EnumerateArray())
                result.Seasons.Add(new CalendarSeason
                {
                    Name = GetString(s, "name"),
                    StartMonthIndex = Math.Max(0, GetInt(s, "startingMonth") - 1),
                    StartDay = GetInt(s, "startingDay", 1),
                    ColorHex = GetString(s, "color")
                });

        if (cal.TryGetProperty("moons", out var moonsEl) && moonsEl.ValueKind == JsonValueKind.Array)
            foreach (var moon in moonsEl.EnumerateArray())
            {
                var firstNewMoon = moon.TryGetProperty("firstNewMoon", out var fnm) ? fnm : default;
                result.Moons.Add(new CalendarMoon
                {
                    Name = GetString(moon, "name"),
                    CycleLengthDays = GetDouble(moon, "cycleLength"),
                    FirstNewMoonYear = GetInt(firstNewMoon, "year"),
                    FirstNewMoonMonthIndex = Math.Max(0, GetInt(firstNewMoon, "month") - 1),
                    FirstNewMoonDay = GetInt(firstNewMoon, "day", 1),
                    ColorHex = GetString(moon, "color")
                });
            }

        if (result.Months.Count == 0)
        {
            error = "No months found in the source calendar.";
            return false;
        }

        if (notesEl is { } notesArray)
        {
            var skippedNonYearly = 0;
            foreach (var note in notesArray.EnumerateArray())
            {
                var template = ConvertNote(note, out var isYearly);
                if (template is null) continue;
                if (!isYearly)
                {
                    skippedNonYearly++;
                    continue;
                }

                result.DefaultEntries.Add(template);
            }

            if (skippedNonYearly > 0)
                warnings.Add(
                    $"{skippedNonYearly} bundled note(s) were skipped (only yearly-recurring notes can be imported as default calendar events).");
        }

        definition = result;
        return true;
    }

    /// <summary>
    ///     Converts one Simple Calendar "note" (see NoteRepeat in their constants.ts - Never=0,
    ///     Weekly=1, Monthly=2, Yearly=3) into a CalendarEventTemplate. Returns null if the note has
    ///     no usable start date; `isYearly` tells the caller whether to keep or skip it (only Yearly
    ///     notes map onto CalendarEventTemplate's yearly-only recurrence).
    /// </summary>
    private static CalendarEventTemplate? ConvertNote(JsonElement note, out bool isYearly)
    {
        isYearly = false;
        if (note.ValueKind != JsonValueKind.Object) return null;
        if (!note.TryGetProperty("flags", out var flags)) return null;
        if (!flags.TryGetProperty("foundryvtt-simple-calendar", out var scFlags)) return null;
        if (!scFlags.TryGetProperty("noteData", out var noteData)) return null;
        if (!noteData.TryGetProperty("startDate", out var startDate)) return null;

        isYearly = GetInt(noteData, "repeats") == 3;

        var title = GetString(note, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

        return new CalendarEventTemplate
        {
            Title = title,
            Description = StripHtml(GetString(note, "content")),
            MonthIndex = GetInt(startDate, "month"),
            Day = GetInt(startDate, "day") + 1,
            Hour = GetInt(startDate, "hour"),
            Minute = GetInt(startDate, "minute")
        };
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }

    private static CalendarLeapYearRule ConvertLeapYear(JsonElement cal, List<CalendarMonthDefinition> months,
        List<string> warnings)
    {
        if (!cal.TryGetProperty("leapYear", out var leapEl))
            return new CalendarLeapYearRule { Kind = CalendarLeapYearRuleKind.None };

        var rule = GetString(leapEl, "rule");
        switch (rule)
        {
            case "none":
            case "":
                return new CalendarLeapYearRule { Kind = CalendarLeapYearRuleKind.None };

            case "gregorian":
                return new CalendarLeapYearRule
                {
                    Kind = CalendarLeapYearRuleKind.Gregorian,
                    MonthIndexAffected = 1,
                    ExtraDays = 1
                };

            case "custom":
            {
                var intervalYears = GetInt(leapEl, "customMod");
                // Simple Calendar stores each month's leap-day count directly
                // (numberOfLeapYearDays) rather than a single "affected month + extra days" rule -
                // find the one month whose leap count differs and derive the difference, since our
                // schema only supports a single affected month.
                var monthsEl = cal.TryGetProperty("months", out var m) ? m : default;
                var monthIndex = 0;
                var extraDays = 1;
                if (monthsEl.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var month in monthsEl.EnumerateArray())
                    {
                        var normal = GetInt(month, "numberOfDays");
                        var leap = GetInt(month, "numberOfLeapYearDays", normal);
                        if (leap != normal)
                        {
                            monthIndex = index;
                            extraDays = leap - normal;
                            break;
                        }

                        index++;
                    }
                }

                return new CalendarLeapYearRule
                {
                    Kind = CalendarLeapYearRuleKind.Interval,
                    IntervalYears = intervalYears,
                    MonthIndexAffected = monthIndex,
                    ExtraDays = extraDays
                };
            }

            default:
                warnings.Add($"Unsupported leap-year rule \"{rule}\" - imported as \"no leap years\".");
                return new CalendarLeapYearRule { Kind = CalendarLeapYearRuleKind.None };
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string property, int fallback = 0)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
    }

    private static double GetDouble(JsonElement element, string property, double fallback = 0)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
               value.GetBoolean();
    }
}