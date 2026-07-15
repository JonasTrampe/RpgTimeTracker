using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Models.Persistence;

/// <summary>
///     Serializable shape of one entry in the Points of Interest library - see
///     PointOfInterestLibraryItemViewModel. Lives alongside the other hoisted library entry DTOs
///     (LibraryEntryDtos.cs/NpcLibraryDtos.cs/SceneLibraryDtos.cs) so both the Shared Library
///     (ThemeSettingsService) and a Session's own library (SessionService) can read/write the same
///     shape.
/// </summary>
public sealed class PointOfInterestLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>GM-only reference notes - never sent to players (see PlayerVisibleDescription).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     References a Media Library entry by Id (not an owned copy) - mutually exclusive with
    ///     IconGlyph, see PointOfInterestLibraryItemViewModel's doc comment.
    /// </summary>
    public Guid? IconImageId { get; set; }

    /// <summary>A Bootstrap icon name (see VisualItemHelper) - mutually exclusive with IconImageId.</summary>
    public string? IconGlyph { get; set; }

    /// <summary>
    ///     GM-only in this pass - not yet transmitted to players over the network (see
    ///     NpcVariantEntryDto.PlayerInfo's doc comment for the same "model now, wire later"
    ///     convention). Groundwork for a future "reveal to players" mechanism separate from the
    ///     per-map-token RevealMode a linked token gets.
    /// </summary>
    public bool PlayerVisibleName { get; set; }

    public bool PlayerVisibleDescription { get; set; }

    /// <summary>Freeform Tag Ids attached to this entry (see Tag).</summary>
    public List<Guid> TagIds { get; set; } = [];
}
