using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RpgTimeTracker.PlayerClient.Services;
using RpgTimeTracker.PlayerClient.ViewModels;
using RpgTimeTracker.PlayerClient.Views;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.PlayerClient;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Zuletzt gewählten Stil laden, bevor das Fenster erzeugt wird, damit beim ersten
        // Rendern schon die richtigen Theme-Ressourcen greifen (siehe App.axaml-Kommentar:
        // Application.Resources bleibt bewusst leer, damit dieser Merge nicht mit direkt
        // gesetzten Keys kollidiert). Vor dem ersten session.snapshot vom Host gibt es noch
        // keine Theme-Vorgabe, daher der feste "shadowrun"-Fallback.
        var loaded = ThemeDefinitionLoader.Resolve("shadowrun");
        if (loaded is { } theme) ClientThemeService.Apply(theme.Definition, theme.FolderPath);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ClientMainWindow
            {
                DataContext = new ClientMainWindowViewModel()
            };

        base.OnFrameworkInitializationCompleted();
    }
}