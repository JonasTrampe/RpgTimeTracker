using System;
using Avalonia;
using RpgTimeTracker.Shared.Services.Logging;
using Serilog;

namespace RpgTimeTracker;

internal static class Program
{
    /// <summary>
    ///     Set from the <c>--no-discovery</c> CLI flag - skips starting the mDNS/LAN-broadcast
    ///     responders when the network server is started (see TcpPlayerServerService.Start),
    ///     for restricted networks or when running multiple test instances that don't need
    ///     LAN discovery to find each other.
    /// </summary>
    public static bool DisableNetworkDiscovery { get; private set; }

    // The entry point must not return an AppBuilder directly,
    // otherwise the Avalonia designer/preview host runs into problems.
    [STAThread]
    public static void Main(string[] args)
    {
        DisableNetworkDiscovery =
            Array.Exists(args, a => a.Equals("--no-discovery", StringComparison.OrdinalIgnoreCase));

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