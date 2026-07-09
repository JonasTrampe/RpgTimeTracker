using Avalonia.Media;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Shared display contract for a timeline row, implemented both by
///     TimelineDisplayItemViewModel (host, GM-side) and RemoteTimelineItemViewModel
///     (PlayerClient) - both already had identical property names/types, so this
///     interface needed no renaming. Makes PlayerTimelineListView (Shared) usable by both
///     apps without merging the two concrete view model types.
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