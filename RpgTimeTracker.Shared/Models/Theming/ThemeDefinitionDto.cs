using System.Collections.Generic;

namespace RpgTimeTracker.Shared.Models.Theming;

/// <summary>
///     GM-readable/-writable theme as a JSON file (theme.json in a theme folder, see
///     ThemeDefinitionLoader). Covers the same resources as the compiled-in
///     Themes/*.axaml themes (colors, gradient brushes, fonts, corner radii, background/
///     button images), so a theme loaded via JSON appears visually on par with the
///     built-in themes in the picker.
/// </summary>
public sealed class ThemeDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplaySubtitle { get; set; } = string.Empty;

    /// <summary>
    ///     Short, neutral name for the style picker list (e.g. "Shadowrun / Cyberpunk") - different
    ///     from DisplayName, which appears as a bold in-fiction title in the main window itself
    ///     (see MainWindow.axaml "heroTitle", ThemeDefinitionLoader.BuildResourceDictionary).
    ///     Optional; falls back to DisplayName when not set (e.g. for custom GM themes
    ///     that only want to maintain a single name).
    /// </summary>
    public string? PickerLabel { get; set; }

    public string HeaderFontFamily { get; set; } = "Segoe UI,Arial,sans-serif";
    public string BodyFontFamily { get; set; } = "Segoe UI,Arial,sans-serif";

    public int FrameCornerRadius { get; set; } = 6;
    public int CardCornerRadius { get; set; } = 6;
    public int ItemCornerRadius { get; set; } = 6;
    public int ButtonCornerRadius { get; set; } = 6;

    /// <summary>Solid-color brushes, keyed by Avalonia resource name (e.g. "AccentBrush", "TextBrush").</summary>
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>Multi-color gradient brushes, keyed by resource name (e.g. "WindowChromeBrush").</summary>
    public Dictionary<string, ThemeGradientDto> Gradients { get; set; } = new();

    /// <summary>
    ///     Named background images (file name relative to the theme folder). The first entry is
    ///     the default background; further named entries are the basis for the
    ///     time-dependent ambience automation (see AmbienceService in the host app).
    /// </summary>
    public List<ThemeImageDto> Backgrounds { get; set; } = new();

    public string? OverlayImageFile { get; set; }
    public string? ButtonImageFile { get; set; }
    public string? PrimaryButtonImageFile { get; set; }
}

public sealed class ThemeGradientDto
{
    /// <summary>Relative point "x,y" (0..1), e.g. "0,0" top-left, "1,0" top-right.</summary>
    public string StartPoint { get; set; } = "0,0";

    public string EndPoint { get; set; } = "0,1";
    public List<ThemeGradientStopDto> Stops { get; set; } = new();
}

public sealed class ThemeGradientStopDto
{
    public double Offset { get; set; }
    public string Color { get; set; } = "#FFFFFFFF";
}

public sealed class ThemeImageDto
{
    public string Name { get; set; } = "Standard";
    public string FileName { get; set; } = string.Empty;
}