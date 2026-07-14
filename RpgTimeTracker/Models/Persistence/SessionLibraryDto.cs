using System.Collections.Generic;

namespace RpgTimeTracker.Models.Persistence;

/// <summary>
///     The session-local counterpart to ThemeSettingsService.ThemeSettingsDto's library lists -
///     serialized as "session-library.json" inside one Session folder (see SessionService).
///     Holds only the items the GM explicitly added as "this session only" (LibraryScope.
///     SessionLocal); Shared items keep living in the always-present AppData settings.json and
///     are not duplicated here.
/// </summary>
public sealed class SessionLibraryDto
{
    public List<MediaLibraryEntryDto> MediaLibrary { get; set; } = [];
    public List<SoundLibraryEntryDto> SoundLibrary { get; set; } = [];
    public List<MusicLibraryEntryDto> MusicLibrary { get; set; } = [];
    public List<MapLibraryEntryDto> MapLibrary { get; set; } = [];
}
