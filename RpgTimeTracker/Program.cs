using System;
using Avalonia;
using RpgTimeTracker.Shared.Services.Logging;
using Serilog;

namespace RpgTimeTracker;

internal static class Program
{
    // The entry point must not return an AppBuilder directly,
    // otherwise the Avalonia designer/preview host runs into problems.
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize("RpgTimeTracker");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception during startup/operation - application is shutting down.");
            throw;
        }
        finally
        {
            AppLogging.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}