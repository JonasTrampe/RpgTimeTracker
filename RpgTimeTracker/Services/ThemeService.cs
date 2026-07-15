using Avalonia;
using Avalonia.Controls;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.Services;

/// <summary>
///     Loads a JSON-defined theme (see ThemeDefinitionLoader - both the SampleThemes shipped with the app
///     and GM's own themes go through the same mechanism, there is no longer a separate class of
///     "built-in" themes) and swaps it at runtime into the
///     Application.Resources MergedDictionaries slot. This causes all
///     DynamicResource bindings in the views to update immediately, without restarting the app.
/// </summary>
public static class ThemeService
{
    // Remembers the currently loaded theme dictionary, so that it can be removed specifically
    // on change (instead of accidentally clearing all MergedDictionaries).
    private static ResourceDictionary? _current;

    /// <summary>
    ///     The folder of the last applied theme (for the ambience automation, to load
    ///     further backgrounds of the same theme).
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