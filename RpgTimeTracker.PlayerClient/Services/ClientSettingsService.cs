using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace RpgTimeTracker.PlayerClient.Services;

/// <summary>
///     Speichert kleine, rein lokale Client-Einstellungen (z.B. Vollbild-Präferenz für das
///     Medien-Fenster). Wird bewusst nicht über das Netzwerk synchronisiert, da jeder Spieler-
///     Rechner ein eigenes Bildschirm-Setup hat.
/// </summary>
public static class ClientSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RpgTimeTracker.PlayerClient");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static ClientSettingsDto LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ClientSettingsDto();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ClientSettingsDto>(json) ?? new ClientSettingsDto();
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Client-Einstellungen konnten nicht geladen werden ({SettingsPath}) - verwende Standardwerte",
                SettingsPath);
            return new ClientSettingsDto();
        }
    }

    public static void SaveSettings(ClientSettingsDto settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            // Einstellungen sind Komfort; Fehler sollen die App nicht stoppen.
            Log.Warning(ex, "Client-Einstellungen konnten nicht gespeichert werden ({SettingsPath})", SettingsPath);
        }
    }

    public sealed class ClientSettingsDto
    {
        public bool MediaFullscreen { get; set; }

        /// <summary>Zuletzt verwendeter Verbindungs-PIN, siehe ClientMainWindowViewModel.Pin.</summary>
        public string Pin { get; set; } = string.Empty;
    }
}