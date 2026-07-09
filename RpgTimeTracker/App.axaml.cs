using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.ViewModels;
using RpgTimeTracker.Views;

namespace RpgTimeTracker;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Zuletzt gewählten Stil laden, bevor das Fenster erzeugt wird, damit beim
        // ersten Rendern schon die richtigen Theme-Ressourcen greifen.
        ApplyStartupTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyStartupTheme()
    {
        var loaded = ThemeDefinitionLoader.Resolve(ThemeSettingsService.LoadLastThemeId())
                     ?? ThemeDefinitionLoader.Resolve("shadowrun");
        if (loaded is { } theme) ThemeService.Apply(theme.Definition, theme.FolderPath);
    }
}