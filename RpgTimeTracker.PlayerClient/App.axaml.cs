using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RpgTimeTracker.PlayerClient.Services;
using RpgTimeTracker.PlayerClient.ViewModels;
using RpgTimeTracker.PlayerClient.Views;
using RpgTimeTracker.Shared.Services.Localization;
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
        LocalizationService.Apply(ClientSettingsService.LoadSettings().Language);

        // Load the last selected style before the window is created, so that the correct
        // theme resources are already in effect on the first render (see App.axaml comment:
        // Application.Resources is deliberately left empty so this merge doesn't collide with
        // directly set keys). Before the first session.snapshot from the host there is no
        // theme specified yet, hence the fixed "shadowrun" fallback.
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