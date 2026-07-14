using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RpgTimeTracker.Models.Persistence;
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

    /// <summary>Separate from SoundLibraryDirectory on purpose - music tracks are a distinct
    ///     library from sound effects (see issue tracking the Music Library/playlist feature),
    ///     and will later be referenced by Scenes.</summary>
    public static string MusicLibraryDirectory => Path.Combine(SettingsDirectory, "MusicLibrary");

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

    /// <summary>Looks up a remembered Music/Sound/Image/Video/Map routing preference by ClientId -
    ///     returns null if this client has never been seen before (caller should then default to
    ///     enabled).</summary>
    public static ClientRoutingPreferenceDto? LoadClientRoutingPreference(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return null;
        return LoadSettings().ClientAudioPreferences.Find(p => p.ClientId == clientId);
    }

    /// <summary>Upserts this client's Music/Sound/Image/Video/Map routing preference (see
    ///     TcpPlayerServerService.SetClientMusicEnabled/SetClientSoundEnabled/SetClientImageEnabled/
    ///     SetClientVideoEnabled/SetClientMapEnabled) - a no-op if clientId is empty (an older
    ///     client build that doesn't send one yet).</summary>
    public static void SaveClientRoutingPreference(string clientId, bool musicEnabled, bool soundEnabled,
        bool imageEnabled, bool videoEnabled, bool mapEnabled)
    {
        if (string.IsNullOrEmpty(clientId)) return;

        var settings = LoadSettings();
        var existing = settings.ClientAudioPreferences.Find(p => p.ClientId == clientId);
        if (existing is not null)
        {
            existing.MusicEnabled = musicEnabled;
            existing.SoundEnabled = soundEnabled;
            existing.ImageEnabled = imageEnabled;
            existing.VideoEnabled = videoEnabled;
            existing.MapEnabled = mapEnabled;
        }
        else
        {
            settings.ClientAudioPreferences.Add(new ClientRoutingPreferenceDto
            {
                ClientId = clientId, MusicEnabled = musicEnabled, SoundEnabled = soundEnabled,
                ImageEnabled = imageEnabled, VideoEnabled = videoEnabled, MapEnabled = mapEnabled
            });
        }

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

    // MediaLibraryEntryDto, SoundLibraryEntryDto, MusicLibraryEntryDto, PlaylistEntryDto,
    // MapFloorEntryDto, and MapLibraryEntryDto moved to
    // RpgTimeTracker.Models.Persistence.LibraryEntryDtos.cs - both this Shared-library store and
    // the new session-local store (SessionService) need the same shapes.

    /// <summary>Remembered Music/Sound/Image/Video/Map routing preference for one player window,
    ///     keyed by its stable ClientId (see SessionHelloParams.ClientId) rather than its ephemeral
    ///     RemoteEndpoint - so a reconnecting window gets its previous routing back instead of
    ///     resetting to enabled every time (see TcpPlayerServerService.PerformHandshakeAsync).</summary>
    public sealed class ClientRoutingPreferenceDto
    {
        public string ClientId { get; set; } = string.Empty;
        public bool MusicEnabled { get; set; } = true;
        public bool SoundEnabled { get; set; } = true;
        public bool ImageEnabled { get; set; } = true;
        public bool VideoEnabled { get; set; } = true;
        public bool MapEnabled { get; set; } = true;
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
        ///     Minimum sound duration (milliseconds) worth estimating a mid-playback seek for when
        ///     resending it to a client whose Sound routing was just re-enabled (see
        ///     MainWindowViewModel.ResendActiveSoundsToClient) - a short one-off effect isn't worth
        ///     seeking into, so it's just resent from the start below this threshold.
        /// </summary>
        public int SoundSeekThresholdMs { get; set; } = 10_000;

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
        public List<MusicLibraryEntryDto> MusicLibrary { get; set; } = [];
        public List<PlaylistEntryDto> Playlists { get; set; } = [];
        public List<MapLibraryEntryDto> MapLibrary { get; set; } = [];
        public List<NpcLibraryEntryDto> NpcLibrary { get; set; } = [];
        // Property name kept as "ClientAudioPreferences" (not renamed to match the DTO type)
        // purely for JSON backward-compat - System.Text.Json round-trips by property name, so
        // renaming this would silently drop every existing user's saved Music/Sound routing
        // preferences on upgrade.
        public List<ClientRoutingPreferenceDto> ClientAudioPreferences { get; set; } = [];

        /// <summary>
        ///     Player-side fog-of-war render style (see MapDisplayViewModel/FogOverlayRenderer) -
        ///     one global GM preference, not per-map/per-floor (see issue #22 design note), pushed
        ///     to all clients on connect (session.snapshot) and live on change (map.renderStyleChanged).
        /// </summary>
        public string FogColorHex { get; set; } = "#0C0C0C";

        public int FogOpacityPercent { get; set; } = 100;
        public double FogBlurRadius { get; set; }
        public bool FogBlurEnabled { get; set; } = true;
    }
}