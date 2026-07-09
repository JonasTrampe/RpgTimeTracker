using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RpgTimeTracker.Shared.Models.Theming;
using Serilog;

namespace RpgTimeTracker.Shared.Services.Theming;

/// <summary>
///     Lädt SL-Designs aus JSON-Dateien (theme.json + Bilddateien in einem Ordner pro Design) und
///     baut daraus zur Laufzeit ein Avalonia-ResourceDictionary mit denselben Ressourcennamen, die
///     die fest einkompilierten Themes/*.axaml-Designs verwenden - beide Quellen sind dadurch für
///     die Views ununterscheidbar (nur DynamicResource-Lookups).
/// </summary>
public static class ThemeDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Vom SL selbst angelegte/bearbeitete Designs - ein Ordner pro Design, z.B. .../Themes/MeineKampagne/theme.json.</summary>
    public static string CustomThemesDirectory => Path.Combine(GetUserConfigDirectory(), "RpgTimeTracker", "Themes");

    /// <summary>
    ///     Mit der App ausgelieferte JSON-Vorlagen (Extrakte der eingebauten Designs) - rein lesbare Beispiele zum
    ///     Kopieren/Anpassen.
    /// </summary>
    public static string SampleThemesDirectory => Path.Combine(AppContext.BaseDirectory, "SampleThemes");

    private static string GetUserConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return appData;

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig)) return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : Path.Combine(home, ".config");
    }

    /// <summary>
    ///     Beispiel-Vorlagen zuerst, dann SL-eigene Designs - bei gleicher Id gewinnt das SL-eigene (steht später in der
    ///     Liste).
    /// </summary>
    public static List<LoadedTheme> LoadAll()
    {
        var result = new List<LoadedTheme>();
        CollectFrom(SampleThemesDirectory, true, result);
        CollectFrom(CustomThemesDirectory, false, result);
        return result;
    }

    private static void CollectFrom(string rootDir, bool isSample, List<LoadedTheme> result)
    {
        if (!Directory.Exists(rootDir)) return;

        foreach (var themeDir in Directory.GetDirectories(rootDir))
        {
            var jsonPath = Path.Combine(themeDir, "theme.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var json = File.ReadAllText(jsonPath);
                var def = JsonSerializer.Deserialize<ThemeDefinitionDto>(json, JsonOptions);
                if (def is null || string.IsNullOrWhiteSpace(def.Id))
                {
                    Log.Warning("Design-JSON ohne gültige Id übersprungen: {Path}", jsonPath);
                    continue;
                }

                result.Add(new LoadedTheme(def, themeDir, isSample));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Design-JSON konnte nicht geladen werden: {Path}", jsonPath);
            }
        }
    }

    /// <summary>
    ///     Löst eine gespeicherte/über das Netz empfangene Theme-Id gegen LoadAll() auf - case-
    ///     insensitiv, und abwärtskompatibel zu zwei älteren Formaten: dem früheren "custom:&lt;Id&gt;"-
    ///     Präfix (als alle Designs noch zwischen eingebauten AXAML-Designs und JSON-"custom"-Designs
    ///     unterschieden) und den noch älteren PascalCase AppTheme-Enum-Namen (z.B. "PostApocalyptic"),
    ///     die zufällig genau den heutigen (kleingeschriebenen) Sample-Ids entsprechen. Gibt null
    ///     zurück, wenn nichts passt - Aufrufer entscheiden selbst, ob das einen Fallback (Startup)
    ///     oder ein "Design unverändert lassen" (Client-Sync, siehe ClientMainWindowViewModel.ApplyTheme)
    ///     bedeutet.
    /// </summary>
    public static LoadedTheme? Resolve(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;

        var id = rawId.StartsWith("custom:", StringComparison.Ordinal) ? rawId["custom:".Length..] : rawId;

        foreach (var loaded in LoadAll())
            if (string.Equals(loaded.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                return loaded;

        return null;
    }

    public static ResourceDictionary BuildResourceDictionary(ThemeDefinitionDto def, string folderPath)
    {
        var dict = new ResourceDictionary();

        foreach (var (key, hex) in def.Colors)
            if (Color.TryParse(hex, out var color))
                dict[key] = new SolidColorBrush(color);
            else
                Log.Warning("Design {Id}: ungültige Farbe {Hex} für {Key} übersprungen", def.Id, hex, key);

        foreach (var (key, gradient) in def.Gradients) dict[key] = BuildGradientBrush(def.Id, gradient);

        if (def.Backgrounds.Count > 0)
            TrySetImageBrush(dict, "WindowBackgroundImageBrush", folderPath, def.Backgrounds[0].FileName, def.Id);
        if (!string.IsNullOrWhiteSpace(def.OverlayImageFile))
            TrySetImageBrush(dict, "WindowOverlayImageBrush", folderPath, def.OverlayImageFile, def.Id);
        if (!string.IsNullOrWhiteSpace(def.ButtonImageFile))
            TrySetImageBrush(dict, "ButtonBrush", folderPath, def.ButtonImageFile, def.Id);
        if (!string.IsNullOrWhiteSpace(def.PrimaryButtonImageFile))
            TrySetImageBrush(dict, "PrimaryButtonBrush", folderPath, def.PrimaryButtonImageFile, def.Id);

        dict["HeaderFontFamily"] = new FontFamily(def.HeaderFontFamily);
        dict["BodyFontFamily"] = new FontFamily(def.BodyFontFamily);
        dict["FrameCornerRadius"] = new CornerRadius(def.FrameCornerRadius);
        dict["CardCornerRadius"] = new CornerRadius(def.CardCornerRadius);
        dict["ItemCornerRadius"] = new CornerRadius(def.ItemCornerRadius);
        dict["ButtonCornerRadius"] = new CornerRadius(def.ButtonCornerRadius);
        dict["ThemeDisplayName"] = def.DisplayName;
        dict["ThemeDisplaySubtitle"] = def.DisplaySubtitle;

        return dict;
    }

    /// <summary>Auch für die Ambiente-Automatik genutzt, um ein bestimmtes benanntes Hintergrundbild gezielt zu setzen.</summary>
    public static bool TrySetImageBrush(IResourceDictionary dict, string resourceKey, string folderPath,
        string fileName, string themeId)
    {
        try
        {
            var fullPath = Path.Combine(folderPath, fileName);
            using var stream = File.OpenRead(fullPath);
            var bitmap = new Bitmap(stream);
            dict[resourceKey] = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Design {Id}: Bild {FileName} konnte nicht geladen werden", themeId, fileName);
            return false;
        }
    }

    private static IBrush BuildGradientBrush(string themeId, ThemeGradientDto gradient)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = ParseRelativePoint(gradient.StartPoint),
            EndPoint = ParseRelativePoint(gradient.EndPoint)
        };

        foreach (var stop in gradient.Stops)
            if (Color.TryParse(stop.Color, out var color))
                brush.GradientStops.Add(new GradientStop(color, stop.Offset));
            else
                Log.Warning("Design {Id}: ungültiger Verlaufsfarbwert {Color} übersprungen", themeId, stop.Color);

        return brush;
    }

    private static RelativePoint ParseRelativePoint(string value)
    {
        var parts = value.Split(',');
        var x = parts.Length > 0 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var y = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return new RelativePoint(x, y, RelativeUnit.Relative);
    }

    public readonly record struct LoadedTheme(ThemeDefinitionDto Definition, string FolderPath, bool IsSample);
}