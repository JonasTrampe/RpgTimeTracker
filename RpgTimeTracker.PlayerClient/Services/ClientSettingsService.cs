using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace RpgTimeTracker.PlayerClient.Services;

/// <summary>
///     Stores small, purely local client settings (e.g. fullscreen preference for the
///     media window). Deliberately not synchronized over the network, since each player
///     machine has its own screen setup.
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
                "Client settings could not be loaded ({SettingsPath}) - using default values",
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
            // Settings are a convenience; errors should not stop the app.
            Log.Warning(ex, "Client settings could not be saved ({SettingsPath})", SettingsPath);
        }
    }

    public sealed class ClientSettingsDto
    {
        public bool MediaFullscreen { get; set; }

        /// <summary>Last used connection PIN, see ClientMainWindowViewModel.Pin.</summary>
        public string Pin { get; set; } = string.Empty;

        /// <summary>UI language (see LocalizationService.SupportedLanguages) - separate from the host, see ThemeSettingsService.</summary>
        public string Language { get; set; } = "en";
    }
}