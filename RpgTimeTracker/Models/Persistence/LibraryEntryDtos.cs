using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Models.Persistence;

// These are the per-library "entry" DTOs - the serializable shape of one item in the Media/
// Sound/Music/Playlist/Map libraries. Hoisted out of ThemeSettingsService (which originally
// nested them as the only place a library needed to be read/written) so both the Shared
// Library (ThemeSettingsService, %AppData%/RpgTimeTracker/settings.json) and a Session's own
// session-local library (SessionService, one folder per campaign) can read/write the exact
// same shapes without duplicating the class definitions. Property names are unchanged from
// before this move, so existing settings.json files still deserialize identically.

public sealed class MediaLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public bool Loop { get; set; }
}

public sealed class SoundLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;
    public bool Loop { get; set; }
    public int Volume { get; set; } = 100;
    public int RepeatCount { get; set; } = 1;
    public long TrimStartMs { get; set; }
    public long TrimEndMs { get; set; }
}

/// <summary>
///     A track in the Music Library - separate from SoundLibraryEntryDto (music plays
///     independently of sound effects, on its own playback channel/transport, and will later
///     be referenced by Scenes). Has a stable Id (unlike Media/SoundLibraryEntryDto, which
///     historically were matched by name/path - see MediaLibraryEntryDto.Id/SoundLibraryEntryDto.Id,
///     added later for the same reason: a Scene/NPC needs to reference a library item by value).
/// </summary>
public sealed class MusicLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;

    /// <summary>Default volume (0-100) with which this track is played.</summary>
    public int Volume { get; set; } = 100;
}

/// <summary>
///     A named, ordered playlist of Music Library tracks (referenced by
///     MusicLibraryEntryDto.Id - see that type's doc comment). Playback itself (which track is
///     currently playing, live transport state) is session-only and not persisted here; this
///     is just the editable definition of "what's in this playlist and how it cycles."
/// </summary>
public sealed class PlaylistEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<Guid> TrackIds { get; set; } = [];
    public bool LoopPlaylist { get; set; } = true;
    public bool Shuffle { get; set; }
}

/// <summary>One floor image of a map, plus its "starting" fog template (see FogMask/FogMaskSerializer) -
///     the GM-authored initial reveal state, as opposed to the session-specific "current" fog
///     that lives in the save file (AppStateDto.MapProgress, added in a later milestone).</summary>
public sealed class MapFloorEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string FogPath { get; set; } = string.Empty;
    public int CellSizePx { get; set; } = 32;
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>Explicit floor/layer order (0-based), saved on every write so the GM's
    ///     up/down-reordered floor sequence survives a save/load round-trip rather than
    ///     relying on implicit list order - see MapItemViewModel.MoveFloorUp/MoveFloorDown.</summary>
    public int Order { get; set; }
}

public sealed class MapLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<MapFloorEntryDto> Floors { get; set; } = [];

    /// <summary>See MapItemViewModel.CurrentFormatVersion/FormatVersion - lets a future
    ///     format change detect an older map on load and apply an upgrade step.</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>Per-map fog render style override, falling back to the global
    ///     ThemeSettingsDto.FogColorHex/etc. when null (see
    ///     MainWindowViewModel.GetEffectiveFogStyle) - unset by default so existing maps keep
    ///     using the global style until the GM explicitly overrides one from MapPrepareWindow.</summary>
    public string? FogColorHex { get; set; }
    public int? FogOpacityPercent { get; set; }
    public double? FogBlurRadius { get; set; }
    public bool? FogBlurEnabled { get; set; }

    /// <summary>Per-map default CellSizePx for newly added floors, seeded from the current
    ///     global DefaultMapCellSizePx constant at map-creation time (not a live reference to
    ///     it), so a later change to that constant never retroactively affects this map's
    ///     future floors.</summary>
    public int DefaultCellSizePx { get; set; } = 8;
}
