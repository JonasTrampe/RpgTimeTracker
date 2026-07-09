using System.Collections.Generic;

namespace RpgTimeTracker.Shared.Models.Theming;

/// <summary>
///     SL-lesbares/-schreibbares Design als JSON-Datei (theme.json in einem Design-Ordner, siehe
///     ThemeDefinitionLoader). Deckt dieselben Ressourcen ab wie die fest einkompilierten
///     Themes/*.axaml-Designs (Farben, Verlaufspinsel, Schriften, Eckenradien, Hintergrund-/
///     Button-Bilder), damit ein per JSON geladenes Design optisch gleichwertig neben den
///     eingebauten Designs in der Auswahl steht.
/// </summary>
public sealed class ThemeDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplaySubtitle { get; set; } = string.Empty;

    /// <summary>
    ///     Kurzer, neutraler Name für die Stil-Auswahlliste (z.B. "Shadowrun / Cyberpunk") - anders
    ///     als DisplayName, das als plakativer In-Fiction-Titel im Hauptfenster selbst erscheint
    ///     (siehe MainWindow.axaml "heroTitle", ThemeDefinitionLoader.BuildResourceDictionary).
    ///     Optional; fällt auf DisplayName zurück, wenn nicht gesetzt (z.B. bei eigenen SL-Designs,
    ///     die nur einen Namen pflegen wollen).
    /// </summary>
    public string? PickerLabel { get; set; }

    public string HeaderFontFamily { get; set; } = "Segoe UI,Arial,sans-serif";
    public string BodyFontFamily { get; set; } = "Segoe UI,Arial,sans-serif";

    public int FrameCornerRadius { get; set; } = 6;
    public int CardCornerRadius { get; set; } = 6;
    public int ItemCornerRadius { get; set; } = 6;
    public int ButtonCornerRadius { get; set; } = 6;

    /// <summary>Einfarbige Pinsel, keyed nach Avalonia-Ressourcenname (z.B. "AccentBrush", "TextBrush").</summary>
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>Mehrfarbige Verlaufspinsel, keyed nach Ressourcenname (z.B. "WindowChromeBrush").</summary>
    public Dictionary<string, ThemeGradientDto> Gradients { get; set; } = new();

    /// <summary>
    ///     Benannte Hintergrundbilder (Dateiname relativ zum Design-Ordner). Der erste Eintrag ist
    ///     der Standard-Hintergrund; weitere benannte Einträge sind die Grundlage für die
    ///     zeit-abhängige Ambiente-Automatik (siehe AmbienceService in der Host-App).
    /// </summary>
    public List<ThemeImageDto> Backgrounds { get; set; } = new();

    public string? OverlayImageFile { get; set; }
    public string? ButtonImageFile { get; set; }
    public string? PrimaryButtonImageFile { get; set; }
}

public sealed class ThemeGradientDto
{
    /// <summary>Relativpunkt "x,y" (0..1), z.B. "0,0" oben-links, "1,0" oben-rechts.</summary>
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