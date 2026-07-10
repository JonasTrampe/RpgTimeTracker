using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace RpgTimeTracker.Services;

/// <summary>
///     Stores small app settings locally in the user profile.
/// </summary>
public static class ThemeSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsDirectory =>
        Path.Combine(GetUserConfigDirectory(), "RpgTimeTracker");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    ///     Legacy storage location of the old "+ add sound" settings feature - only
    ///     relevant for the one-time migration to SoundLibraryDirectory.
    /// </summary>
    public static string CustomSoundsDirectory => Path.Combine(SettingsDirectory, "Sounds");

    public static string MediaCacheDirectory => Path.Combine(SettingsDirectory, "Media");

    public static string MediaLibraryDirectory => Path.Combine(SettingsDirectory, "MediaLibrary");

    public static string SoundLibraryDirectory => Path.Combine(SettingsDirectory, "SoundLibrary");

    public static string MapLibraryDirectory => Path.Combine(SettingsDirectory, "MapLibrary");

    private static string GetUserConfigDirectory()
    {
        // Cross-platform:
        // Windows: %AppData%
        // Linux:   $XDG_CONFIG_HOME or ~/.config
        // macOS:   ~/Library/Application Support
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return appData;

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig)) return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? AppContext.BaseDirectory
            : Path.Combine(home, ".config");
    }

    public static ThemeSettingsDto LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ThemeSettingsDto();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ThemeSettingsDto>(json) ?? new ThemeSettingsDto();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Settings could not be loaded ({SettingsPath}) - using defaults",
                SettingsPath);
            return new ThemeSettingsDto();
        }
    }

    public static void SaveSettings(ThemeSettingsDto settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            // Settings are a convenience; errors should not stop the app.
            Log.Warning(ex, "Settings could not be saved ({SettingsPath})", SettingsPath);
        }
    }

    /// <summary>
    ///     Last chosen theme id (see ThemeDefinitionLoader/ThemeDefinitionDto.Id, e.g.
    ///     "shadowrun") - resolved via ThemeDefinitionLoader.Resolve, which also understands
    ///     older stored values (former "custom:" prefixes, even older PascalCase enum names).
    /// </summary>
    public static string? LoadLastThemeId()
    {
        return LoadSettings().LastTheme;
    }

    public static void SaveLastThemeId(string id)
    {
        var settings = LoadSettings();
        settings.LastTheme = id;
        SaveSettings(settings);
    }

    /// <summary>Records the file path of the last successful manual save/load (see LastSaveFilePath).</summary>
    public static void SaveLastSaveFilePath(string path)
    {
        var settings = LoadSettings();
        settings.LastSaveFilePath = path;
        SaveSettings(settings);
    }

    /// <summary>
    ///     Legacy: a single sound added via the old settings page. Only read
    ///     once on startup and migrated to SoundLibrary (see SoundService.MigrateLegacyCustomSounds) -
    ///     new sounds now only end up in SoundLibrary, not here anymore.
    /// </summary>
    public sealed class CustomSoundDto
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public sealed class MediaLibraryEntryDto
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public bool Loop { get; set; }
    }

    public sealed class SoundLibraryEntryDto
    {
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
    }

    public sealed class MapLibraryEntryDto
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public List<MapFloorEntryDto> Floors { get; set; } = [];
    }

    public sealed class ThemeSettingsDto
    {
        public string? LastTheme { get; set; }
        public string PlayerHeaderTitle { get; set; } = string.Empty;
        public string PlayerHeaderSubtitle { get; set; } = string.Empty;
        public bool HeadsUpWarningEnabled { get; set; }
        public double HeadsUpLeadMinutes { get; set; } = 2;
        public bool AmbienceAutomationEnabled { get; set; }

        /// <summary>
        ///     Display name transmitted in the LAN/mDNS announcement, so that multiple servers on the same
        ///     network are distinguishable in the client's server list (see
        ///     PlayerMdnsAnnouncer/LanDiscoveryResponder).
        /// </summary>
        public string ServerName { get; set; } = "RpgTimeTracker";

        /// <summary>Optional PIN for establishing a connection (see MainWindowViewModel.ConnectionPin); empty = no PIN.</summary>
        public string ConnectionPin { get; set; } = string.Empty;

        /// <summary>
        ///     Path of the last file written/read via the manual 💾/📂 buttons - updated on every
        ///     successful save or load regardless of the two toggles below, so that enabling
        ///     auto-save/auto-load later has somewhere to point at.
        /// </summary>
        public string? LastSaveFilePath { get; set; }

        /// <summary>Auto-writes to LastSaveFilePath when the app closes (only if that path is known).</summary>
        public bool AutoSaveOnCloseEnabled { get; set; }

        /// <summary>Auto-loads LastSaveFilePath on startup instead of starting with a blank state.</summary>
        public bool AutoLoadOnStartupEnabled { get; set; }

        /// <summary>UI language (see LocalizationService.SupportedLanguages) - separate from the PlayerClient, see ClientSettingsService.</summary>
        public string Language { get; set; } = "en";

        /// <summary>Only read for the one-time migration - see CustomSoundDto comment.</summary>
        public List<CustomSoundDto> CustomSounds { get; set; } = [];

        /// <summary>
        ///     Whether the migration from CustomSounds to SoundLibrary has already run (prevents
        ///     re-importing if the user later deletes the migrated sound again).
        /// </summary>
        public bool LegacyCustomSoundsMigrated { get; set; }

        public List<MediaLibraryEntryDto> MediaLibrary { get; set; } = [];
        public List<SoundLibraryEntryDto> SoundLibrary { get; set; } = [];
        public List<MapLibraryEntryDto> MapLibrary { get; set; } = [];
    }
}