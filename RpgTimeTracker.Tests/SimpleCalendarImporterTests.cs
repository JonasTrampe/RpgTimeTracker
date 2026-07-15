using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class SimpleCalendarImporterTests
{
    // Trimmed but structurally real excerpts of the Foundry VTT Simple Calendar predefined-calendar
    // JSON schema (see SimpleCalendarImporter's doc comment for the source), just enough of each
    // shape (months/weekdays/leapYear/year/time/seasons/moons) to exercise the converter without
    // depending on network access during tests.
    private const string GregorianLikeJson = """
        {
          "calendar": {
            "months": [
              { "name": "January", "numericRepresentation": 1, "numberOfDays": 31, "numberOfLeapYearDays": 31, "intercalary": false },
              { "name": "February", "numericRepresentation": 2, "numberOfDays": 28, "numberOfLeapYearDays": 29, "intercalary": false }
            ],
            "weekdays": [
              { "name": "Sunday", "numericRepresentation": 1 },
              { "name": "Monday", "numericRepresentation": 2 }
            ],
            "leapYear": { "rule": "gregorian", "customMod": 0 },
            "year": { "numericRepresentation": 2022, "firstWeekday": 4, "yearZero": 1970 },
            "time": { "hoursInDay": 24, "minutesInHour": 60, "secondsInMinute": 60 },
            "seasons": [
              { "name": "Spring", "startingMonth": 3, "startingDay": 19, "color": "#46b946" }
            ],
            "moons": [
              {
                "name": "Moon",
                "cycleLength": 29.53059,
                "firstNewMoon": { "year": 2000, "month": 1, "day": 4 },
                "color": "#ffffff"
              }
            ]
          }
        }
        """;

    private const string CustomLeapJson = """
        {
          "calendar": {
            "months": [
              { "name": "Hammer", "numberOfDays": 30, "numberOfLeapYearDays": 30, "intercalary": false },
              { "name": "Midwinter", "numberOfDays": 1, "numberOfLeapYearDays": 1, "intercalary": true },
              { "name": "Midsummer", "numberOfDays": 1, "numberOfLeapYearDays": 2, "intercalary": true }
            ],
            "weekdays": [
              { "name": "1st" }, { "name": "2nd" }
            ],
            "leapYear": { "rule": "custom", "customMod": 4 },
            "year": { "numericRepresentation": 1495, "firstWeekday": 0 },
            "time": { "hoursInDay": 24, "minutesInHour": 60, "secondsInMinute": 60 }
          }
        }
        """;

    private const string NoLeapJson = """
        {
          "calendar": {
            "months": [
              { "name": "Praios", "numberOfDays": 30, "intercalary": false },
              { "name": "Namenlose Tage", "numberOfDays": 5, "intercalary": false }
            ],
            "weekdays": [ { "name": "Windstag" } ],
            "leapYear": { "rule": "none", "customMod": 0 },
            "year": { "numericRepresentation": 1040, "firstWeekday": 3 },
            "time": { "hoursInDay": 24, "minutesInHour": 60, "secondsInMinute": 60 }
          }
        }
        """;

    // A real-shaped "notes" array (sibling of "calendar", see e.g. dsa-tde5e.json) - one Yearly
    // note (repeats:3) and one Weekly note (repeats:1) to exercise both the import and the skip path.
    private const string JsonWithNotes = """
        {
          "calendar": {
            "months": [ { "name": "Praios", "numberOfDays": 30, "intercalary": false } ],
            "weekdays": [ { "name": "Windstag" } ],
            "leapYear": { "rule": "none", "customMod": 0 },
            "year": { "numericRepresentation": 1040, "firstWeekday": 0 },
            "time": { "hoursInDay": 24, "minutesInHour": 60, "secondsInMinute": 60 }
          },
          "notes": [
            {
              "name": "Sommersonnenwende",
              "content": "<p>Beginn des neuen Jahres, <b>hoechster Feiertag</b></p>",
              "flags": {
                "foundryvtt-simple-calendar": {
                  "noteData": {
                    "startDate": { "year": 1040, "month": 0, "day": 0, "hour": 12, "minute": 0, "seconds": 0 },
                    "repeats": 3
                  }
                }
              }
            },
            {
              "name": "Weekly Market Day",
              "content": "A weekly note that should be skipped.",
              "flags": {
                "foundryvtt-simple-calendar": {
                  "noteData": {
                    "startDate": { "year": 1040, "month": 0, "day": 3, "hour": 0, "minute": 0, "seconds": 0 },
                    "repeats": 1
                  }
                }
              }
            }
          ]
        }
        """;

    [Fact]
    public void LooksLikeSimpleCalendarFormat_detects_the_calendar_wrapper()
    {
        Assert.True(SimpleCalendarImporter.LooksLikeSimpleCalendarFormat(GregorianLikeJson));
    }

    [Fact]
    public void LooksLikeSimpleCalendarFormat_rejects_our_own_schema()
    {
        var ourJson = """{ "Name": "Gregorian", "Months": [] }""";
        Assert.False(SimpleCalendarImporter.LooksLikeSimpleCalendarFormat(ourJson));
    }

    [Fact]
    public void LooksLikeSimpleCalendarFormat_rejects_garbage()
    {
        Assert.False(SimpleCalendarImporter.LooksLikeSimpleCalendarFormat("not json at all"));
    }

    [Fact]
    public void TryConvert_maps_months_weekdays_time_and_gregorian_leap_rule()
    {
        var success = SimpleCalendarImporter.TryConvert(GregorianLikeJson, "Gregorian Import", out var definition,
            out var warnings, out var error);

        Assert.True(success, error);
        Assert.NotNull(definition);
        Assert.Empty(warnings);
        Assert.Equal("Gregorian Import", definition!.Name);
        Assert.Equal(2, definition.Months.Count);
        Assert.Equal("January", definition.Months[0].Name);
        Assert.Equal(31, definition.Months[0].Days);
        Assert.Equal(["Sunday", "Monday"], definition.Weekdays);
        Assert.Equal(24, definition.HoursPerDay);
        Assert.Equal(CalendarLeapYearRuleKind.Interval, definition.LeapYear.Kind);
        Assert.Equal(4, definition.LeapYear.IntervalYears);
        Assert.Equal(1, definition.LeapYear.MonthIndexAffected);
        Assert.Equal(1, definition.LeapYear.ExtraDays);
        Assert.Single(definition.Seasons);
        Assert.Equal(2, definition.Seasons[0].StartMonthIndex);
        Assert.Single(definition.Moons);
        Assert.Equal("Moon", definition.Moons[0].Name);
    }

    [Fact]
    public void TryConvert_derives_the_affected_month_and_extra_days_for_a_custom_leap_rule()
    {
        var success = SimpleCalendarImporter.TryConvert(CustomLeapJson, "Harptos Import", out var definition,
            out _, out var error);

        Assert.True(success, error);
        Assert.Equal(CalendarLeapYearRuleKind.Interval, definition!.LeapYear.Kind);
        Assert.Equal(4, definition.LeapYear.IntervalYears);
        // Midsummer (index 2) is the one month whose numberOfLeapYearDays (2) differs from
        // numberOfDays (1) - the converter must find it rather than assuming a fixed index.
        Assert.Equal(2, definition.LeapYear.MonthIndexAffected);
        Assert.Equal(1, definition.LeapYear.ExtraDays);
    }

    [Fact]
    public void TryConvert_maps_a_none_leap_rule_directly()
    {
        var success = SimpleCalendarImporter.TryConvert(NoLeapJson, "DSA Import", out var definition, out _,
            out var error);

        Assert.True(success, error);
        Assert.Equal(CalendarLeapYearRuleKind.None, definition!.LeapYear.Kind);
    }

    [Fact]
    public void TryConvert_fails_gracefully_on_non_calendar_json()
    {
        var success = SimpleCalendarImporter.TryConvert("""{"foo": 1}""", "X", out var definition, out _, out var error);

        Assert.False(success);
        Assert.Null(definition);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryConvert_imports_yearly_notes_as_DefaultEntries_and_skips_non_yearly_ones()
    {
        var success = SimpleCalendarImporter.TryConvert(JsonWithNotes, "DSA Import", out var definition,
            out var warnings, out var error);

        Assert.True(success, error);
        Assert.Single(definition!.DefaultEntries);
        var entry = definition.DefaultEntries[0];
        Assert.Equal("Sommersonnenwende", entry.Title);
        Assert.Equal("Beginn des neuen Jahres, hoechster Feiertag", entry.Description);
        Assert.Equal(0, entry.MonthIndex);
        Assert.Equal(1, entry.Day);
        Assert.Equal(12, entry.Hour);
        Assert.Contains(warnings, w => w.Contains("skipped", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Converted_calendar_round_trips_through_GameInstant()
    {
        SimpleCalendarImporter.TryConvert(CustomLeapJson, "Harptos Import", out var definition, out _, out _);

        var instant = definition!.FromCalendarDate(3, 0, 10, 5, 6, 7);
        var date = definition.ToCalendarDate(instant);

        Assert.Equal(3, date.Year);
        Assert.Equal(0, date.MonthIndex);
        Assert.Equal(10, date.Day);
    }
}
