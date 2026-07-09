namespace RpgTimeTracker.Models;

/// <summary>
///     Antwort auf die Warnung beim Löschen eines Medienbibliothek-Eintrags, der noch als
///     Event-Medium zugewiesen ist (siehe MainWindowViewModel.RemoveMediaLibraryItemAsync).
/// </summary>
public enum TriggerMediaDeleteChoice
{
    /// <summary>Löschen abbrechen, nichts ändern.</summary>
    Cancel,

    /// <summary>Zuweisung bei allen betroffenen Elementen entfernen, danach Bibliothekseintrag + Datei löschen.</summary>
    RemoveFromItemsAndDelete,

    /// <summary>
    ///     Nur den Bibliothekseintrag entfernen; die Datei bleibt auf der Platte, damit die
    ///     betroffenen Elemente ihr Event-Medium behalten (aber nicht mehr in der Bibliothek verwaltbar).
    /// </summary>
    KeepInItemsRemoveFromLibraryOnly
}
