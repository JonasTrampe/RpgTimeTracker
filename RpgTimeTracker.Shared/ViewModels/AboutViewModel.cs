using System;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.Shared.ViewModels;

/// <summary>
///     Data basis for the About screen (RpgTimeTracker.Shared.Views.AboutView), shared
///     by host and client. The actual content (title + description, as Markdown) comes per
///     app from the caller (see AboutWindow in both projects, which passes a loader for its
///     own About/AboutContent.{lang}.md) - version and license notices are loaded centrally
///     here from the actual data instead of being retyped in every app.
/// </summary>
public sealed partial class AboutViewModel : ObservableObject, IDisposable
{
    private const string ThirdPartyNoticesFileName = "THIRD-PARTY-NOTICES.txt";
    private const string LicenseFileName = "LICENSE.txt";

    private readonly Func<string> _loadContentMarkdown;
    private readonly Assembly _assembly;

    [ObservableProperty] private string _contentMarkdown;
    [ObservableProperty] private string _versionText;

    /// <summary>Own project license (MIT), see LICENSE at the repo root.</summary>
    [ObservableProperty] private string _licenseText;

    [ObservableProperty] private string _thirdPartyNoticesText;

    public AboutViewModel(Func<string> loadContentMarkdown, Assembly assembly)
    {
        _loadContentMarkdown = loadContentMarkdown;
        _assembly = assembly;

        _contentMarkdown = loadContentMarkdown();
        _versionText = FormatVersion(assembly);
        _licenseText = LoadTextFile(LicenseFileName);
        _thirdPartyNoticesText = LoadTextFile(ThirdPartyNoticesFileName);

        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    ///     The About window stays open across a language switch (unlike the main
    ///     windows, it isn't reconstructed), so unlike the properties in most other
    ///     ViewModels its Get()-based content needs to be re-read from disk, not just
    ///     re-announced via OnPropertyChanged.
    /// </summary>
    private void OnLanguageChanged()
    {
        ContentMarkdown = _loadContentMarkdown();
        VersionText = FormatVersion(_assembly);
        LicenseText = LoadTextFile(LicenseFileName);
        ThirdPartyNoticesText = LoadTextFile(ThirdPartyNoticesFileName);
    }

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
