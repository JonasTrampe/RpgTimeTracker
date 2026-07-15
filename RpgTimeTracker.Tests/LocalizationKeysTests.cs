using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RpgTimeTracker.Tests;

/// <summary>
///     Guards against a translation gap silently degrading to English text (see
///     LocalizationService's fallback) by asserting every supported language file has the exact
///     same key set as en.json - catches a typo'd or forgotten key at CI time instead of only
///     being noticed by whoever happens to switch the app to that language.
/// </summary>
public class LocalizationKeysTests
{
    private static readonly string[] Languages = ["en", "de"];

    private static Dictionary<string, string> LoadKeys(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", $"{language}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    [Fact]
    public void All_language_files_have_the_same_keys_as_english()
    {
        var english = LoadKeys("en");

        foreach (var language in Languages.Where(l => l != "en"))
        {
            var other = LoadKeys(language);

            var missing = english.Keys.Except(other.Keys).OrderBy(k => k).ToList();
            var extra = other.Keys.Except(english.Keys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"{language}.json is out of sync with en.json.\n" +
                (missing.Count > 0 ? $"Missing keys: {string.Join(", ", missing)}\n" : "") +
                (extra.Count > 0 ? $"Extra keys: {string.Join(", ", extra)}\n" : ""));
        }
    }
}
