using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace RpgTimeTracker.Shared.Services.Logging;

/// <summary>
///     Central logging configuration for both apps (Host + PlayerClient). Both call
///     Initialize() with their own app name in Program.cs; the rest of the code then simply
///     uses Serilog.Log directly (static access, matching the rest of the app's style - e.g.
///     SoundService/ThemeSettingsService are also static services without a DI container).
/// </summary>
public static class AppLogging
{
    /// <summary>
    ///     %AppData%/RpgTimeTracker/logs (Windows) or the platform-appropriate equivalent - the same base as
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

        // Fire-and-forget tasks are common in this app (e.g. "_ = _playerServer.PublishXAsync();")
        // - without these two handlers, an exception thrown there would never be visible anywhere.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception,
                "Unhandled exception (AppDomain), IsTerminating={IsTerminating}", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved exception in a fire-and-forget task");
            e.SetObserved();
        };

        Log.Information("{App} started · Log directory: {LogDirectory}", appName, LogDirectory);
    }

    public static void Shutdown()
    {
        Log.Information("Application is shutting down");
        Log.CloseAndFlush();
    }
}