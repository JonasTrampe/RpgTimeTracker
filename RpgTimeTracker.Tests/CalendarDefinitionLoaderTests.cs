using System.Linq;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class CalendarDefinitionLoaderTests
{
    [Fact]
    public void LoadAll_finds_all_bundled_calendars()
    {
        var loaded = CalendarDefinitionLoader.LoadAll();

        var names = loaded.Select(c => c.Definition.Name).ToList();
        Assert.Contains("Gregorian", names);
        Assert.Contains("Harptos", names);
        Assert.Contains("Voidreach", names);
        Assert.Contains("Aventurian (DSA)", names);
        Assert.All(loaded, c => Assert.True(c.IsBundled));
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var resolved = CalendarDefinitionLoader.Resolve("gregorian");

        Assert.NotNull(resolved);
        Assert.Equal("Gregorian", resolved!.Value.Definition.Name);
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_name()
    {
        Assert.Null(CalendarDefinitionLoader.Resolve("NoSuchCalendar"));
    }

    [Theory]
    [InlineData("Gregorian", 12, 7)]
    [InlineData("Harptos", 17, 10)]
    [InlineData("Voidreach", 10, 8)]
    [InlineData("Aventurian (DSA)", 13, 7)]
    public void Bundled_calendars_have_the_expected_month_and_weekday_counts(string name, int monthCount,
        int weekdayCount)
    {
        var loaded = CalendarDefinitionLoader.Resolve(name)!.Value.Definition;

        Assert.Equal(monthCount, loaded.Months.Count);
        Assert.Equal(weekdayCount, loaded.Weekdays.Count);
    }

    [Theory]
    [InlineData("Gregorian")]
    [InlineData("Harptos")]
    [InlineData("Voidreach")]
    [InlineData("Aventurian (DSA)")]
    public void Bundled_calendars_round_trip_through_GameInstant(string name)
    {
        var calendar = CalendarDefinitionLoader.Resolve(name)!.Value.Definition;

        // Round-tripping year/month/day/hour/minute/second through GameInstant must reproduce the
        // exact same calendar date - the core correctness property every calendar (Gregorian or
        // custom) must satisfy for save/RPC round-trips and calendar-entry matching to work.
        var instant = calendar.FromCalendarDate(3, 2, 10, 5, 6, 7);
        var date = calendar.ToCalendarDate(instant);

        Assert.Equal(3, date.Year);
        Assert.Equal(2, date.MonthIndex);
        Assert.Equal(10, date.Day);
        Assert.Equal(5, date.Hour);
        Assert.Equal(6, date.Minute);
        Assert.Equal(7, date.Second);
    }
}
