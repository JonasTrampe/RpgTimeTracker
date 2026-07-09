using System.Collections;
using System.Windows.Input;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Gemeinsamer Anzeige-Vertrag für "Kopfzeile + Zeitliste", implementiert von
///     MainWindowViewModel (Host) und ClientMainWindowViewModel (PlayerClient) - beide hatten
///     bereits identisch benannte Properties. Erlaubt PlayerHeaderView/PlayerTimelineListView
///     (Shared), gegen beide DataContext-Typen zu binden, ohne die beiden ViewModels
///     zusammenzulegen; jede App behält ihre eigenen Extras (Host: lokale Medien-Vorschau,
///     Client: Verbindungsbereich) außerhalb dieser gemeinsamen Controls.
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