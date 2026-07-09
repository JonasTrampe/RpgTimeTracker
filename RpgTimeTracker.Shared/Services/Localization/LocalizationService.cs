using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Serilog;

namespace RpgTimeTracker.Shared.Services.Localization;

/// <summary>
///     Loads UI strings from flat JSON dictionaries (one file per language, e.g.
///     Localization/en.json) and merges them into Application.Resources as plain strings -
///     mirrors ThemeDefinitionLoader/ThemeService exactly (same runtime-swap-via-
///     MergedDictionaries mechanism), so XAML can bind with {DynamicResource Some.Key} and get
///     an instant UI update when the language changes, the same way theme colors do.
///     English ("en") is the default and the fallback for any key missing from another
///     language file - a translation gap degrades to English text instead of a raw key name.
/// </summary>
public static class LocalizationService
{
    public const string DefaultLanguage = "en";

    private static readonly string[] SupportedLanguagesArray = ["en", "de"];

    private static ResourceDictionary? _current;
    private static Dictionary<string, string> _currentStrings = new();

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static IReadOnlyList<string> SupportedLanguages => SupportedLanguagesArray;

    /// <summary>
    ///     Raised after Apply() swaps in the new language dictionary. XAML-bound {DynamicResource}
    ///     text updates on its own, but computed C# properties that call Get() (e.g. a button label
    ///     that picks between two localized strings depending on state) only re-evaluate when their
    ///     own OnPropertyChanged fires - they don't know the active language changed underneath them.
    ///     ViewModels with such properties should subscribe here and call their own
    ///     OnPropertyChanged(string.Empty) (CommunityToolkit/Avalonia convention for "re-read
    ///     everything") to pick up the new language immediately instead of only on the next
    ///     unrelated state change.
    /// </summary>
    public static event Action? LanguageChanged;

    private static string LocalizationDirectory => Path.Combine(AppContext.BaseDirectory, "Localization");

    public static void Apply(string? language)
    {
        var code = SupportedLanguagesArray.Contains(language) ? language! : DefaultLanguage;
        var strings = Load(code);

        if (code != DefaultLanguage)
        {
            // Missing translations fall back to English instead of showing the raw key.
            var fallback = Load(DefaultLanguage);
            foreach (var (key, value) in fallback)
                strings.TryAdd(key, value);
        }

        var app = Application.Current;
        if (app is null) return;

        var dict = new ResourceDictionary();
        foreach (var (key, value) in strings) dict[key] = value;

        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
        _currentStrings = strings;
        CurrentLanguage = code;
        LanguageChanged?.Invoke();
    }

    /// <summary>For C#-side strings that can't use a XAML DynamicResource binding (e.g. status/error messages built in code-behind or view models).</summary>
    public static string Get(string key)
    {
        return _currentStrings.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>For optional overrides (e.g. a bundled sample theme's display name) where a missing key should fall back to caller-supplied data instead of the raw key string.</summary>
    public static bool TryGet(string key, out string value)
    {
        return _currentStrings.TryGetValue(key, out value!);
    }

    private static Dictionary<string, string> Load(string code)
    {
        var path = Path.Combine(LocalizationDirectory, $"{code}.json");
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Localization file {Path} could not be loaded", path);
            return new Dictionary<string, string>();
        }
    }
}
