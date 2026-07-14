using System;
using System.IO;
using System.Text.Json;
using RpgTimeTracker.Models.Persistence;
using Serilog;

namespace RpgTimeTracker.Services;

/// <summary>
///     Tracks the currently-open Session - an optional, folder-scoped campaign the GM creates or
///     opens via a folder dialog (mirroring the existing Save/Load file-picker mental model, see
///     MainWindow.axaml.cs's OnSaveClick/OnLoadClick), sitting alongside the always-present
///     Shared Library (ThemeSettingsService). With no session open (CurrentSessionPath is null),
///     the app behaves exactly as before this feature existed - Sessions are strictly additive.
///
///     Folder layout of one session (see MediaDirectory/SoundDirectory/etc. below):
///     &lt;folder&gt;/
///       session.json           - game state (same AppStateDto shape as .rtt-save's inner file)
///       session-library.json   - this session's own library entries (SessionLibraryDto)
///       media/ sound/ music/ maps/ npcs/ - this session's own asset files
///
///     This class only owns the "which folder is open, and what's its layout" concern. Reading
///     game state into the live clock, and merging this session's library entries into
///     MainWindowViewModel's displayed collections, are handled by the caller (see
///     MainWindowViewModel's Open/CloseSessionAsync) - kept separate so this class stays testable
///     without a full MainWindowViewModel in the loop.
/// </summary>
public sealed class SessionService
{
    public const string StateFileName = "session.json";
    public const string LibraryFileName = "session-library.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string? CurrentSessionPath { get; private set; }

    public bool IsSessionOpen => CurrentSessionPath is not null;

    public string MediaDirectory => RequireSubdirectory("media");
    public string SoundDirectory => RequireSubdirectory("sound");
    public string MusicDirectory => RequireSubdirectory("music");
    public string MapDirectory => RequireSubdirectory("maps");
    public string NpcDirectory => RequireSubdirectory("npcs");

    private string RequireSubdirectory(string name)
    {
        if (CurrentSessionPath is null)
            throw new InvalidOperationException("No session is currently open.");
        return Path.Combine(CurrentSessionPath, name);
    }

    /// <summary>Creates a brand-new, empty session in an existing (ideally empty) folder, then
    ///     opens it.</summary>
    public void CreateSession(string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        CurrentSessionPath = folderPath;
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(SoundDirectory);
        Directory.CreateDirectory(MusicDirectory);
        Directory.CreateDirectory(MapDirectory);
        Directory.CreateDirectory(NpcDirectory);
        SaveLibrary(new SessionLibraryDto());
        Log.Information("Session created: {FolderPath}", folderPath);
    }

    /// <summary>Opens an existing session folder (creating any missing subfolders, e.g. because
    ///     the folder predates a newer library type being added).</summary>
    public void OpenSession(string folderPath)
    {
        CurrentSessionPath = folderPath;
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(SoundDirectory);
        Directory.CreateDirectory(MusicDirectory);
        Directory.CreateDirectory(MapDirectory);
        Directory.CreateDirectory(NpcDirectory);
        Log.Information("Session opened: {FolderPath}", folderPath);
    }

    public void CloseSession()
    {
        Log.Information("Session closed: {FolderPath}", CurrentSessionPath);
        CurrentSessionPath = null;
    }

    private string LibraryPath => Path.Combine(CurrentSessionPath!, LibraryFileName);

    public SessionLibraryDto LoadLibrary()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return new SessionLibraryDto();

            var json = File.ReadAllText(LibraryPath);
            return JsonSerializer.Deserialize<SessionLibraryDto>(json) ?? new SessionLibraryDto();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session library could not be loaded ({LibraryPath}) - using an empty one",
                LibraryPath);
            return new SessionLibraryDto();
        }
    }

    public void SaveLibrary(SessionLibraryDto library)
    {
        try
        {
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(library, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session library could not be saved ({LibraryPath})", LibraryPath);
        }
    }

    private string StatePath => Path.Combine(CurrentSessionPath!, StateFileName);

    public bool StateFileExists => CurrentSessionPath is not null && File.Exists(StatePath);

    public string ReadStateJson()
    {
        return File.ReadAllText(StatePath);
    }

    public void WriteStateJson(string json)
    {
        File.WriteAllText(StatePath, json);
    }
}
