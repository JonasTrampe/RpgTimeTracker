using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;
using RpgTimeTracker.Shared.ViewModels;

namespace RpgTimeTracker.Views;

public partial class AboutWindow : Window
{
    private static readonly Uri ContentUri = new("avares://RpgTimeTracker/About/AboutContent.md");

    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel(LoadContentMarkdown(), typeof(App).Assembly);
    }

    private static string LoadContentMarkdown()
    {
        using var stream = AssetLoader.Open(ContentUri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}