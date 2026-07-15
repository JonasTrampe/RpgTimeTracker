using Avalonia;
using Avalonia.Headless;
using RpgTimeTracker.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace RpgTimeTracker.Tests;

/// <summary>
///     Minimal headless Avalonia bootstrap for MapFogIntegrationTests.cs's [AvaloniaFact]/
///     [AvaloniaTheory] tests - see the comment on the Avalonia.Headless package reference in
///     RpgTimeTracker.Tests.csproj for why this is needed at all. Deliberately configured against
///     the base Avalonia.Application, not RpgTimeTracker.App/RpgTimeTracker.PlayerClient.App -
///     these tests only need a running dispatcher, not either app's real window/resource startup.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}