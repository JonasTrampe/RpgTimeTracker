using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace RpgTimeTracker.Services;

/// <summary>
///     Speichert kleine App-Einstellungen lokal im Benutzerprofil.
/// </summary>
public static class ThemeSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsDirectory =>
        Path.Combine(GetUserConfigDirectory(), "RpgTimeTracker");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    ///     Legacy-Speicherort der alten "+ Sound hinzufügen"-Einstellungen-Funktion - nur noch
    ///     für die einmalige Migration nach SoundLibraryDirectory relevant.
    /// </summary>
    public static string CustomSoundsDirectory => Path.Combine(SettingsDirectory, "Sounds");

    public static string MediaCacheDirectory => Path.Combine(SettingsDirectory, "Media");

    public static string MediaLibraryDirectory => Path.Combine(SettingsDirectory, "MediaLibrary");

    public static string SoundLibraryDirectory => Path.Combine(SettingsDirectory, "SoundLibrary");

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
            Log.Warning(ex, "Einstellungen konnten nicht geladen werden ({SettingsPath}) - verwende Standardwerte",
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
            // Einstellungen sind Komfort; Fehler sollen die App nicht stoppen.
            Log.Warning(ex, "Einstellungen konnten nicht gespeichert werden ({SettingsPath})", SettingsPath);
        }
    }

    /// <summary>
    ///     Zuletzt gewählte Design-Id (siehe ThemeDefinitionLoader/ThemeDefinitionDto.Id, z.B.
    ///     "shadowrun") - wird über ThemeDefinitionLoader.Resolve aufgelöst, das auch ältere
    ///     gespeicherte Werte (frühere "custom:"-Präfixe, noch ältere PascalCase-Enum-Namen)
    ///     versteht.
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

    /// <summary>
    ///     Legacy: einzelner über die alte Einstellungen-Seite hinzugefügter Sound. Wird nur
    ///     noch einmalig beim Start gelesen und in SoundLibrary überführt (siehe SoundService.MigrateLegacyCustomSounds) -
    ///     neue Sounds landen nur noch in SoundLibrary, nicht mehr hier.
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

    public sealed class ThemeSettingsDto
    {
        public string? LastTheme { get; set; }
        public string PlayerHeaderTitle { get; set; } = "Spieleranzeige";
        public string PlayerHeaderSubtitle { get; set; } = "Timer, Wecker und OnTime";
        public bool HeadsUpWarningEnabled { get; set; }
        public double HeadsUpLeadMinutes { get; set; } = 2;
        public bool AmbienceAutomationEnabled { get; set; }

        /// <summary>
        ///     Im LAN/mDNS-Announcement übertragener Anzeigename, damit mehrere Server im selben
        ///     Netzwerk in der Client-Serverliste unterscheidbar sind (siehe
        ///     PlayerMdnsAnnouncer/LanDiscoveryResponder).
        /// </summary>
        public string ServerName { get; set; } = "RpgTimeTracker";

        /// <summary>Optionaler PIN für den Verbindungsaufbau (siehe MainWindowViewModel.ConnectionPin); leer = kein PIN.</summary>
        public string ConnectionPin { get; set; } = string.Empty;

        /// <summary>Nur noch für die einmalige Migration gelesen - siehe CustomSoundDto-Kommentar.</summary>
        public List<CustomSoundDto> CustomSounds { get; set; } = [];

        /// <summary>
        ///     Ob die Migration aus CustomSounds nach SoundLibrary schon gelaufen ist (verhindert
        ///     erneutes Importieren, falls der Nutzer den migrierten Sound danach wieder löscht).
        /// </summary>
        public bool LegacyCustomSoundsMigrated { get; set; }

        public List<MediaLibraryEntryDto> MediaLibrary { get; set; } = [];
        public List<SoundLibraryEntryDto> SoundLibrary { get; set; } = [];
    }
}