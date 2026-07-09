using System.Collections;
using System.Windows.Input;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Shared display contract for "header + timeline", implemented by
///     MainWindowViewModel (host) and ClientMainWindowViewModel (PlayerClient) - both already had
///     identically named properties. Allows PlayerHeaderView/PlayerTimelineListView
///     (Shared) to bind against both DataContext types without merging the two
///     view models; each app keeps its own extras (host: local media preview,
///     client: connection area) outside of these shared controls.
/// </summary>
public interface IPlayerDisplayContext
{
    string PlayerHeaderTitle { get; }
    string PlayerHeaderSubtitle { get; }
    string CurrentGameTimeText { get; }
    string SpeedMultiplierDisplay { get; }
    bool HasPlayerVisibleCalendarEntries { get; }
    bool ShowPlayerCalendarView { get; set; }
    bool ShowPlayerTimelineView { get; }
    string PlayerCalendarMonthLabel { get; }
    string PlayerCalendarSelectedDateLabel { get; }
    bool HasPlayerCalendarEntriesForSelectedDate { get; }
    ICommand ShowPlayerTimelineCommand { get; }
    ICommand ShowPlayerCalendarCommand { get; }
    ICommand PreviousPlayerCalendarMonthCommand { get; }
    ICommand NextPlayerCalendarMonthCommand { get; }

    IEnumerable TimelineEntries { get; }
    IEnumerable CalendarMonthDays { get; }
    IEnumerable CalendarEntries { get; }
}