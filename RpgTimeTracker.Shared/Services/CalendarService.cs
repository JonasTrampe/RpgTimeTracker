using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Holds the campaign's currently active CalendarDefinition, following the same static
///     cross-cutting-service convention already used by LocalizationService/SoundService/
///     VisualItemHelper - lets ViewModels (CalendarEntryViewModel, AlarmItemViewModel, etc.)
///     format/parse dates without threading a CalendarDefinition through every constructor.
///     Defaults to the Gregorian calendar, matching the app's pre-custom-calendar behavior for a
///     fresh campaign. MainWindowViewModel updates this when the GM picks a different calendar or
///     loads a save; PlayerClient updates it from the definition sent in SessionSnapshotParams.
/// </summary>
public static class CalendarService
{
    public static CalendarDefinition Active { get; set; } = CalendarDefinition.CreateGregorian();
}