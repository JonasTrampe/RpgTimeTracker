using Avalonia;
using Avalonia.Controls;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.PlayerClient.Services;

/// <summary>
///     Wendet ein per JSON definiertes Design an (siehe ThemeDefinitionLoader) - spiegelt
///     ThemeService auf der Host-Seite. Es gibt keine separate Klasse "eingebauter" Designs mehr:
///     die mit der App ausgelieferten SampleThemes laufen über denselben Mechanismus wie SL-eigene
///     Designs.
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
