using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Localization;
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
        // Load the last chosen style/language before the window is created, so that the
        // correct resources are already in effect on first render.
        LocalizationService.Apply(ThemeSettingsService.LoadSettings().Language);
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