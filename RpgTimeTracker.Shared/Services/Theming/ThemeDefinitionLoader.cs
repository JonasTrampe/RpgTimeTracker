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
///     Loads GM themes from JSON files (theme.json + image files in one folder per theme) and
///     builds an Avalonia ResourceDictionary at runtime with the same resource names that
///     the compiled-in Themes/*.axaml themes use - both sources are thus
///     indistinguishable for the views (only DynamicResource lookups).
/// </summary>
public static class ThemeDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Themes created/edited by the GM themselves - one folder per theme, e.g. .../Themes/MyCampaign/theme.json.</summary>
    public static string CustomThemesDirectory => Path.Combine(GetUserConfigDirectory(), "RpgTimeTracker", "Themes");

    /// <summary>
    ///     JSON templates shipped with the app (extracts of the built-in themes) - purely readable examples for
    ///     copying/adapting.
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
    ///     Sample templates first, then the GM's own themes - with the same Id, the GM's own theme wins (it comes later in
    ///     the list).
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
                    Log.Warning("Theme JSON without a valid Id skipped: {Path}", jsonPath);
                    continue;
                }

                result.Add(new LoadedTheme(def, themeDir, isSample));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Theme JSON could not be loaded: {Path}", jsonPath);
            }
        }
    }

    /// <summary>
    ///     Resolves a stored/network-received theme Id against LoadAll() - case-
    ///     insensitive, and backward-compatible with two older formats: the former "custom:&lt;Id&gt;"
    ///     prefix (from when all themes still distinguished between built-in AXAML themes and JSON "custom"
    ///     themes) and the even older PascalCase AppTheme enum names (e.g. "PostApocalyptic"),
    ///     which happen to match exactly today's (lowercase) sample Ids. Returns null
    ///     if nothing matches - callers decide for themselves whether that means a fallback (startup)
    ///     or "leave theme unchanged" (client sync, see ClientMainWindowViewModel.ApplyTheme).
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
                Log.Warning("Theme {Id}: invalid color {Hex} for {Key} skipped", def.Id, hex, key);

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

    /// <summary>Also used by the ambience automation to specifically set a particular named background image.</summary>
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
            Log.Warning(ex, "Theme {Id}: image {FileName} could not be loaded", themeId, fileName);
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
                Log.Warning("Theme {Id}: invalid gradient color value {Color} skipped", themeId, stop.Color);

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