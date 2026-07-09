using System;
using System.IO;
using System.Reflection;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Datengrundlage für den About-Screen (RpgTimeTracker.Shared.Views.AboutView), gemeinsam
///     für Host und Client. Der eigentliche Inhalt (Titel + Beschreibung, als Markdown) kommt pro
///     App vom Aufrufer (siehe AboutWindow in beiden Projekten, lädt seine eigene
///     About/AboutContent.md) - Version und Lizenzhinweise werden hier zentral aus den
///     tatsächlichen Daten geladen statt in jeder App erneut getippt zu werden.
/// </summary>
public sealed class AboutViewModel
{
    private const string ThirdPartyNoticesFileName = "THIRD-PARTY-NOTICES.txt";
    private const string LicenseFileName = "LICENSE.txt";

    public AboutViewModel(string contentMarkdown, Assembly assembly)
    {
        ContentMarkdown = contentMarkdown;
        VersionText = FormatVersion(assembly);
        LicenseText = LoadTextFile(LicenseFileName);
        ThirdPartyNoticesText = LoadTextFile(ThirdPartyNoticesFileName);
    }

    public string ContentMarkdown { get; }
    public string VersionText { get; }

    /// <summary>Eigene Projektlizenz (MIT), siehe LICENSE an der Repo-Wurzel.</summary>
    public string LicenseText { get; }

    public string ThirdPartyNoticesText { get; }

    /// <summary>
    ///     Liest die "informational version" statt der reinen AssemblyVersion, da Letztere ein
    ///     Prerelease-Suffix wie "-alpha" (aus Directory.Build.props/&lt;Version&gt;) gar nicht
    ///     abbilden kann (AssemblyVersion muss rein numerisch sein) - ohne diesen Umweg würde der
    ///     About-Screen z.B. "1.0.0" statt "1.0.0-alpha" anzeigen und den Alpha-Status verschleiern.
    /// </summary>
    private static string FormatVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        var version = assembly.GetName().Version;
        return version is null ? "unbekannt" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string LoadTextFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return $"Konnte nicht geladen werden ({fileName} fehlt im Anwendungsverzeichnis).";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Konnte nicht geladen werden ({fileName} fehlt im Anwendungsverzeichnis).";
        }
    }
}