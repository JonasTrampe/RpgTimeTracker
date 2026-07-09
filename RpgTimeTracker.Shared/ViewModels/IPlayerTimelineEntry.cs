using Avalonia.Media;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Gemeinsamer Anzeige-Vertrag für eine Zeitleisten-Zeile, implementiert sowohl von
///     TimelineDisplayItemViewModel (Host, SL-seitig) als auch RemoteTimelineItemViewModel
///     (PlayerClient) - beide hatten bereits identische Property-Namen/-Typen, sodass dieses
///     Interface keine Umbenennungen brauchte. Macht PlayerTimelineListView (Shared) für beide
///     Apps verwendbar, ohne die beiden konkreten ViewModel-Typen zusammenzulegen.
/// </summary>
public interface IPlayerTimelineEntry
{
    string Name { get; }
    string KindLabel { get; }
    string StatusText { get; }
    string DetailText { get; }
    string PrimaryValue { get; }
    double Progress { get; }
    bool HasProgress { get; }
    Geometry IconGeometry { get; }
    IBrush? ItemBorderBrush { get; }
    bool IsActive { get; }
    bool IsCompleted { get; }
    bool IsBlinkActive { get; }
}