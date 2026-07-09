using Avalonia;
using Avalonia.Controls;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.Services;

/// <summary>
///     Lädt ein per JSON definiertes Design (siehe ThemeDefinitionLoader - sowohl die mit der App
///     ausgelieferten SampleThemes als auch SL-eigene Designs laufen über denselben Mechanismus,
///     es gibt keine separate Klasse "eingebauter" Designs mehr) und tauscht es zur Laufzeit im
///     Application.Resources-MergedDictionaries-Slot aus. Dadurch aktualisieren sich alle
///     DynamicResource-Bindings in den Views sofort, ohne Neustart der App.
/// </summary>
public static class ThemeService
{
    // Merkt sich das aktuell geladene Theme-Dictionary, damit es beim Wechsel gezielt entfernt
    // (statt versehentlich alle MergedDictionaries geleert) werden kann.
    private static ResourceDictionary? _current;

    /// <summary>
    ///     Der Ordner des zuletzt angewendeten Designs (für die Ambiente-Automatik, um weitere
    ///     Hintergründe desselben Designs nachzuladen).
    /// </summary>
    public static string? CurrentThemeFolderPath { get; private set; }

    public static void Apply(ThemeDefinitionDto definition, string folderPath)
    {
        var app = Application.Current;
        if (app is null) return;

        var dict = ThemeDefinitionLoader.BuildResourceDictionary(definition, folderPath);

        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
        CurrentThemeFolderPath = folderPath;
    }
}
