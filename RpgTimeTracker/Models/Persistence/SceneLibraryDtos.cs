using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Models.Persistence;

/// <summary>
///     Serializable shape of one entry in the Scenes library - see SceneLibraryItemViewModel.
///     Lives alongside the other hoisted library entry DTOs (LibraryEntryDtos.cs/NpcLibraryDtos.cs)
///     so both the Shared Library (ThemeSettingsService) and a Session's own library
///     (SessionService) can read/write the same shape.
/// </summary>
public sealed class SceneLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>GM-only reference text (motivation, read-aloud text, secrets) - never sent to
    ///     players, same convention as NpcGmInfoBlockDto's markdown bodies.</summary>
    public string DescriptionMarkdown { get; set; } = string.Empty;

    /// <summary>GameInstant.TotalSeconds - a calendar-agnostic elapsed-time value, not a DateTime
    ///     (see the custom calendar engine's design decision). Interpreted against whichever
    ///     CalendarDefinition happens to be active when displayed.</summary>
    public long StartDateSeconds { get; set; }

    /// <summary>Media/Sound/Music/Map Ids this Scene bundles - references into the existing
    ///     libraries by Id, not owned copies, resolved the same way NPC portrait/token/sound Ids
    ///     already are. None of these are required; a Scene can bundle any subset.</summary>
    public Guid? ImageId { get; set; }
    public Guid? MapId { get; set; }
    public List<Guid> SoundIds { get; set; } = [];
    public Guid? MusicId { get; set; }

    /// <summary>Freeform Tag Ids attached to this Scene (see Tag) - separate from Scene
    ///     membership on other library items, a different, explicit mechanism.</summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>Phase 3's scene-scoped timeline - reuses the exact same TimerDto/AlarmDto/
    ///     IntervalEventDto shapes the global lists already use (see AppStateDto.cs).</summary>
    public List<TimerDto> Timers { get; set; } = [];
    public List<AlarmDto> Alarms { get; set; } = [];
    public List<IntervalEventDto> IntervalEvents { get; set; } = [];
}
