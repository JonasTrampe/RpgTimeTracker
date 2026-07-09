using Avalonia;
using Avalonia.Controls;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.PlayerClient.Services;

/// <summary>
///     Applies a theme defined via JSON (see ThemeDefinitionLoader) - mirrors ThemeService on
///     the host side. There is no longer a separate class of "built-in" themes: the SampleThemes
///     shipped with the app run through the same mechanism as the GM's own themes.
/// </summary>
public static class ClientThemeService
{
    private static ResourceDictionary? _current;

    public static void Apply(ThemeDefinitionDto definition, string folderPath)
    {
        var app = Application.Current;
        if (app is null) return;

        var dict = ThemeDefinitionLoader.BuildResourceDictionary(definition, folderPath);

        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
    }
}
