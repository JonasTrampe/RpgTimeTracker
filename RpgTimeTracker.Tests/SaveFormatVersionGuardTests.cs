using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RpgTimeTracker.Models.Persistence;

namespace RpgTimeTracker.Tests;

/// <summary>
///     Guards against a silent save-format break: if any public property anywhere in the
///     AppStateDto object graph (including nested DTOs and Shared types like CalendarDefinition)
///     is added, removed, renamed, or retyped, this test fails unless AppStateDto.Version and the
///     baked ExpectedShape/ExpectedVersion below are updated together in the same change - forcing
///     a deliberate decision about whether the change needs a version bump (see
///     AppStateDto.Version's doc comment: old saves must fail to load cleanly, not silently
///     deserialize with wrong/zeroed fields).
/// </summary>
public class SaveFormatVersionGuardTests
{
    private const int ExpectedVersion = 5;

    [Fact]
    public void AppStateDto_Version_matches_its_default_instance_value()
    {
        var dto = new AppStateDto();

        Assert.Equal(ExpectedVersion, dto.Version);
    }

    [Fact]
    public void AppStateDto_shape_change_requires_a_deliberate_Version_bump()
    {
        var currentShape = DescribeShape(typeof(AppStateDto));

        Assert.True(currentShape == ExpectedShape,
            "AppStateDto's save-format shape changed (a property was added/removed/renamed/retyped " +
            "somewhere in its object graph). If this is an intentional, breaking format change: bump " +
            "AppStateDto.Version, bump ExpectedVersion in this test, and replace ExpectedShape below with " +
            "the current shape shown here:\n\n" + currentShape);
    }

    // DefaultEntries (CalendarEventTemplate list) was added to CalendarDefinition without a Version
    // bump - it's purely additive with a safe empty-list default, so an old save missing the field
    // deserializes correctly (no bundled default events, nothing else affected), unlike a
    // renamed/retyped/removed field. See CalendarEventTemplate's doc comment.
    //
    // TargetSceneId (Guid?) was likewise added to AlarmDto/IntervalEventDto/TimerDto/
    // CalendarEntryDefinition without a bump for the same reason (Phase 4 of the Scenes/Tags/
    // Calendars project) - an old save missing the field just means "this trigger doesn't
    // activate any Scene," a safe/dormant default, not silently-wrong data.
    //
    // TagIds (List<Guid>) was likewise added to AlarmDto/IntervalEventDto/TimerDto (a separate,
    // timer-specific Tags list - see MainWindowViewModel.Tags.cs's TimerTags) without a bump - an
    // old save missing the field just means "no tags assigned yet," a safe empty-list default.
    private const string ExpectedShape =
        "AppStateDto{ActiveCalendar:CalendarDefinition{DefaultEntries:List<CalendarEventTemplate{ColorHex:String,Day:Int32,Description:String,Hour:Int32,Icon:String,Minute:Int32,MonthIndex:Int32,Title:String}>,Description:String,FirstWeekdayIndex:Int32,HoursPerDay:Int32,LeapYear:CalendarLeapYearRule{ExtraDays:Int32,IntervalYears:Int32,Kind:CalendarLeapYearRuleKind,MonthIndexAffected:Int32},MinutesPerHour:Int32,Months:List<CalendarMonthDefinition{Days:Int32,IsIntercalary:Boolean,Name:String}>,Moons:List<CalendarMoon{ColorHex:String,CycleLengthDays:Double,FirstNewMoonDay:Int32,FirstNewMoonMonthIndex:Int32,FirstNewMoonYear:Int32,Name:String}>,Name:String,Seasons:List<CalendarSeason{ColorHex:String,Name:String,StartDay:Int32,StartMonthIndex:Int32}>,SecondsPerDay:Int32,SecondsPerMinute:Int32,Weekdays:List<String>,YearZeroOffset:Int32},Alarms:List<AlarmDto{Blink:Boolean,ColorHex:String,Icon:String,IsPlayerVisible:Boolean,IsTriggered:Boolean,Name:String,RepeatIntervalTicks:Int64?,Sound:String,SoundRepeatCount:Int32,TagIds:List<Guid>,TargetSceneId:Guid?,TriggerAtSeconds:Int64,TriggerMediaFileName:String,TriggerMediaFullscreen:Boolean,TriggerMediaKind:String,TriggerMediaLoop:Boolean,TriggerMediaPath:String,TriggerMediaPauseClock:Boolean}>,CalendarEntries:List<CalendarEntryDefinition{ColorHex:String,Description:String,HasTrigger:Boolean,Icon:String,Id:Guid,IsPlayerVisible:Boolean,RecurrenceKind:CalendarRecurrenceKind,RepeatUntil:GameInstant{TotalSeconds:Int64}?,Start:GameInstant,TargetSceneId:Guid?,Title:String,TriggerFileName:String,TriggerFullscreen:Boolean,TriggerKind:MediaKind,TriggerLoop:Boolean,TriggerPath:String,TriggerPauseClockDuringVideo:Boolean}>,CurrentGameTimeSeconds:Int64,IntervalEvents:List<IntervalEventDto{ActiveDurationTicks:Int64,Blink:Boolean,ColorHex:String,ElapsedTicks:Int64,Icon:String,IntervalTicks:Int64,IsCompleted:Boolean,IsPlayerVisible:Boolean,IsRunning:Boolean,MaxRepeats:Int32?,Name:String,Sound:String,SoundRepeatCount:Int32,TagIds:List<Guid>,TargetSceneId:Guid?,TriggerMediaFileName:String,TriggerMediaFullscreen:Boolean,TriggerMediaKind:String,TriggerMediaLoop:Boolean,TriggerMediaPath:String,TriggerMediaPauseClock:Boolean}>,IsClockRunning:Boolean,JumpMarkers:List<JumpMarkerDto{Name:String,TimeOfDayTicks:Int64}>,Sound:String,SpeedMultiplier:Double,Theme:String,Timers:List<TimerDto{Blink:Boolean,ColorHex:String,DurationTicks:Int64,ElapsedTicks:Int64,Icon:String,IsPlayerVisible:Boolean,IsRunning:Boolean,Name:String,Sound:String,SoundRepeatCount:Int32,TagIds:List<Guid>,TargetSceneId:Guid?,TriggerMediaFileName:String,TriggerMediaFullscreen:Boolean,TriggerMediaKind:String,TriggerMediaLoop:Boolean,TriggerMediaPath:String,TriggerMediaPauseClock:Boolean}>,Version:Int32}";

    private static string DescribeShape(System.Type type, HashSet<System.Type>? visited = null)
    {
        visited ??= new HashSet<System.Type>();
        if (!visited.Add(type)) return type.Name;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, System.StringComparer.Ordinal)
            .Select(p => $"{p.Name}:{DescribeType(p.PropertyType, visited)}");
        return $"{type.Name}{{{string.Join(",", props)}}}";
    }

    private static string DescribeType(System.Type type, HashSet<System.Type> visited)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return $"List<{DescribeType(type.GetGenericArguments()[0], visited)}>";

        var underlying = System.Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return $"{DescribeType(underlying, visited)}?";

        if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
            return type.Name;

        if (type.Namespace is not null && type.Namespace.StartsWith("RpgTimeTracker"))
            return DescribeShape(type, visited);

        return type.Name;
    }
}
