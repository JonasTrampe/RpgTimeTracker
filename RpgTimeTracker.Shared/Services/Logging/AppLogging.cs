using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace RpgTimeTracker.Shared.Services.Logging;

/// <summary>
///     Zentrale Logging-Konfiguration für beide Apps (Host + PlayerClient). Beide rufen
///     Initialize() mit ihrem eigenen App-Namen in Program.cs auf; der Rest des Codes nutzt danach
///     einfach Serilog.Log direkt (statischer Zugriff, passend zum übrigen Stil der App - z.B.
///     SoundService/ThemeSettingsService sind ebenfalls statische Services ohne DI-Container).
/// </summary>
public static class AppLogging
{
    /// <summary>
    ///     %AppData%/RpgTimeTracker/logs (Windows) bzw. das plattformübliche Äquivalent - dieselbe Basis wie
    ///     ThemeSettingsService.SettingsDirectory.
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RpgTimeTracker", "logs");

    public static void Initialize(string appName)
    {
        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.WithProperty("App", appName)
            .WriteTo.File(
                Path.Combine(LogDirectory, $"{appName}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Fire-and-forget-Tasks sind in dieser App üblich (z.B. "_ = _playerServer.PublishXAsync();")
        // - ohne diese beiden Handler würde eine dort geworfene Ausnahme nirgends sichtbar.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception,
                "Unbehandelte Ausnahme (AppDomain), IsTerminating={IsTerminating}", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbeobachtete Ausnahme in einem Fire-and-Forget-Task");
            e.SetObserved();
        };

        Log.Information("{App} gestartet · Log-Verzeichnis: {LogDirectory}", appName, LogDirectory);
    }

    public static void Shutdown()
    {
        Log.Information("Anwendung wird beendet");
        Log.CloseAndFlush();
    }
}