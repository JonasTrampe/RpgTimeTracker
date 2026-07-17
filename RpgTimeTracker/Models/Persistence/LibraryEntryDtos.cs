using System;
using System.Collections.Generic;
using RpgTimeTracker.Models;

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

    /// <summary>
    ///     Freeform Tag Ids attached to this item (see Tag) - separate from Scene
    ///     membership, a different, explicit mechanism.
    /// </summary>
    public List<Guid> TagIds { get; set; } = [];
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
    public List<Guid> TagIds { get; set; } = [];
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

    public List<Guid> TagIds { get; set; } = [];
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

/// <summary>
///     One floor image of a map, plus its "starting" fog template (see FogMask/FogMaskSerializer) -
///     the GM-authored initial reveal state, as opposed to the session-specific "current" fog
///     that lives in the save file (AppStateDto.MapProgress, added in a later milestone).
/// </summary>
public sealed class MapFloorEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string FogPath { get; set; } = string.Empty;
    public int CellSizePx { get; set; } = 32;
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>
    ///     Explicit floor/layer order (0-based), saved on every write so the GM's
    ///     up/down-reordered floor sequence survives a save/load round-trip rather than
    ///     relying on implicit list order - see MapItemViewModel.MoveFloorUp/MoveFloorDown.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Semi-permanent and permanent map annotation lines (see MapLineDto) - Temporary lines
    ///     (the original ephemeral stroke feature) are never persisted here, they only ever exist
    ///     as a live RPC/fade animation. Belongs to the Floor (not the Map) since points are in
    ///     that floor's own image-space coordinates.
    /// </summary>
    public List<MapLineDto> Lines { get; set; } = [];
}

/// <summary>How long a drawn map line should stick around - see MapLineDto.</summary>
public enum MapLineDurability
{
    /// <summary>Fades after a few seconds, never persisted - the original ephemeral stroke feature.</summary>
    Temporary,

    /// <summary>Fades after several minutes (see MapDisplayView/MapEditCanvasControl's SemiPermanentFadeDuration) - persisted so it survives a reconnect while still active, but not written back once expired.</summary>
    SemiPermanent,

    /// <summary>Never fades - persisted with the floor like a token, until explicitly erased.</summary>
    Permanent
}

/// <summary>
///     One freehand line drawn on a map floor (see MapDisplayView/MapEditCanvasControl's Draw
///     tool) - only SemiPermanent/Permanent lines are ever persisted (see MapFloorEntryDto.Lines);
///     Temporary ones are purely a live, non-persisted RPC + fade animation.
/// </summary>
public sealed class MapLineDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FloorId { get; set; }
    public List<MapLinePointDto> Points { get; set; } = [];
    public string ColorHex { get; set; } = "#FFD700";
    public MapLineDurability Durability { get; set; } = MapLineDurability.Temporary;

    /// <summary>Stroke width in image pixels - GM-adjustable via the Draw tool's brush-size slider, same convention as fog's brush radius.</summary>
    public double Thickness { get; set; } = 4;

    /// <summary>
    ///     GM-only per-line fog gate (defaults to visible, matching player-drawn lines - see
    ///     MapTokenDto.RevealMode's similar "opt into hiding" convention): when true, this line is
    ///     withheld from players entirely until the GM flips it back to false, rather than being
    ///     resolved cell-by-cell against the floor's fog mask.
    /// </summary>
    public bool HiddenUntilRevealed { get; set; }

    /// <summary>Empty for a GM-drawn line; otherwise the drawing player's ClientId (see PainterTagHelper), used for "players can only erase their own".</summary>
    public string OwnerClientId { get; set; } = string.Empty;
}

/// <summary>Image-space point of a MapLineDto - kept separate from RpcParams.AnnotationPoint so this persistence-layer file has no dependency on the Shared RPC namespace.</summary>
public sealed class MapLinePointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class MapLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<MapFloorEntryDto> Floors { get; set; } = [];

    /// <summary>
    ///     See MapItemViewModel.CurrentFormatVersion/FormatVersion - lets a future
    ///     format change detect an older map on load and apply an upgrade step.
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    ///     Per-map fog render style override, falling back to the global
    ///     ThemeSettingsDto.FogColorHex/etc. when null (see
    ///     MainWindowViewModel.GetEffectiveFogStyle) - unset by default so existing maps keep
    ///     using the global style until the GM explicitly overrides one from MapPrepareWindow.
    /// </summary>
    public string? FogColorHex { get; set; }

    public int? FogOpacityPercent { get; set; }
    public double? FogBlurRadius { get; set; }
    public bool? FogBlurEnabled { get; set; }

    /// <summary>
    ///     Per-map default CellSizePx for newly added floors, seeded from the current
    ///     global DefaultMapCellSizePx constant at map-creation time (not a live reference to
    ///     it), so a later change to that constant never retroactively affects this map's
    ///     future floors.
    /// </summary>
    public int DefaultCellSizePx { get; set; } = 8;

    public List<Guid> TagIds { get; set; } = [];

    /// <summary>See MapTokenDto's doc comment - belongs to the Map, not a Floor.</summary>
    public List<MapTokenDto> Tokens { get; set; } = [];

    /// <summary>See InitiativeEntryDto's doc comment - the map's combat state (#70).</summary>
    public List<InitiativeEntryDto> InitiativeEntries { get; set; } = [];

    public int InitiativeCurrentIndex { get; set; }
    public bool InitiativeIsRunning { get; set; }
    public int InitiativeRound { get; set; }
}

/// <summary>
///     Serializable shape of one MapTokenViewModel - see its doc comment for the "single source
///     of truth" rule this DTO follows: only the link + position + visibility settings are
///     persisted, never a linked Character/PointOfInterest's Name/Portrait/Health.
/// </summary>
public sealed class MapTokenDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FloorId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    public TokenLinkKind LinkKind { get; set; } = TokenLinkKind.None;
    public Guid? LinkedId { get; set; }
    public Guid? LinkedVariantId { get; set; }

    /// <summary>Only meaningful when LinkKind == None - see MapTokenViewModel's doc comment.</summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public Guid? IconImageId { get; set; }
    public string? IconGlyph { get; set; }

    public TokenRevealMode RevealMode { get; set; } = TokenRevealMode.AlwaysVisible;
    public bool PlayerVisibleName { get; set; }
    public bool PlayerVisiblePortrait { get; set; }
    public bool PlayerVisibleDetail { get; set; }

    /// <summary>See MapTokenViewModel.FacingDegrees' doc comment (#70).</summary>
    public double FacingDegrees { get; set; }
}

/// <summary>
///     Serializable shape of one InitiativeEntryViewModel (#70) - see its doc comment. Belongs
///     to the Map, not a Floor, same as MapTokenDto.
/// </summary>
public sealed class InitiativeEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TokenLinkKind LinkKind { get; set; } = TokenLinkKind.None;
    public Guid? LinkedId { get; set; }
    public Guid? LinkedVariantId { get; set; }
    public string FreeformName { get; set; } = string.Empty;
}