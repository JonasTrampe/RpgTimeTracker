using System;
using Avalonia;
using RpgTimeTracker.Shared.Services.Logging;
using Serilog;

namespace RpgTimeTracker.PlayerClient;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize("RpgTimeTracker.PlayerClient");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unbehandelte Ausnahme beim Start/Betrieb - Anwendung wird beendet.");
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