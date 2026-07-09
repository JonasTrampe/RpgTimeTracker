using System;
using System.IO;
using System.Reflection;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Data basis for the About screen (RpgTimeTracker.Shared.Views.AboutView), shared
///     by host and client. The actual content (title + description, as Markdown) comes per
///     app from the caller (see AboutWindow in both projects, loads its own
///     About/AboutContent.md) - version and license notices are loaded centrally here from the
///     actual data instead of being retyped in every app.
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

    /// <summary>Own project license (MIT), see LICENSE at the repo root.</summary>
    public string LicenseText { get; }

    public string ThirdPartyNoticesText { get; }

    /// <summary>
    ///     Reads the "informational version" instead of the plain AssemblyVersion, since the latter
    ///     cannot represent a prerelease suffix like "-alpha" (from Directory.Build.props/&lt;Version&gt;)
    ///     at all (AssemblyVersion must be purely numeric) - without this workaround the
    ///     About screen would show e.g. "1.0.0" instead of "1.0.0-alpha" and hide the alpha status.
    /// </summary>
    private static string FormatVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        var version = assembly.GetName().Version;
        return version is null
            ? LocalizationService.Get("About.UnknownVersion")
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string LoadTextFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Format(LocalizationService.Get("About.FileNotFound"), fileName);
        }
    }
}