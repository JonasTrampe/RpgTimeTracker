using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.Views;

public partial class ConfirmTriggerMediaDeleteWindow : Window
{
    /// <summary>Only for the Avalonia XAML loader (Previewer/AVLN3001); in app code always constructed with a usage list.</summary>
    public ConfirmTriggerMediaDeleteWindow()
    {
        InitializeComponent();
    }

    public ConfirmTriggerMediaDeleteWindow(string mediaName, IReadOnlyList<string> usedBy)
    {
        InitializeComponent();
        UsageText.Text = string.Format(LocalizationService.Get("ConfirmTriggerMediaDelete.UsageTextFormat"),
            mediaName, string.Join(", ", usedBy));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(TriggerMediaDeleteChoice.Cancel);
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        Close(TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly);
    }

    private void OnRemoveAndDeleteClick(object? sender, RoutedEventArgs e)
    {
        Close(TriggerMediaDeleteChoice.RemoveFromItemsAndDelete);
    }
}