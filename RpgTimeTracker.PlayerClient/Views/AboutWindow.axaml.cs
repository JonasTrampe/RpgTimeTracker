using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.PlayerClient.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel(LoadContentMarkdown(), typeof(App).Assembly);
    }

    private static string LoadContentMarkdown()
    {
        var uri = new Uri(
            $"avares://RpgTimeTracker.PlayerClient/About/AboutContent.{LocalizationService.CurrentLanguage}.md");
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}