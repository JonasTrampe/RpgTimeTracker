using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var viewModel = new AboutViewModel(LoadContentMarkdown, typeof(App).Assembly);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private static string LoadContentMarkdown()
    {
        var uri = new Uri($"avares://RpgTimeTracker/About/AboutContent.{LocalizationService.CurrentLanguage}.md");
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}