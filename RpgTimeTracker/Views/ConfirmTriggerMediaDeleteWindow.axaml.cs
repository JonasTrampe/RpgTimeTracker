using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.Views;

public partial class ConfirmTriggerMediaDeleteWindow : Window
{
    /// <summary>Nur für den Avalonia-XAML-Loader (Previewer/AVLN3001); im App-Code stets mit Verwendungsliste konstruiert.</summary>
    public ConfirmTriggerMediaDeleteWindow()
    {
        InitializeComponent();
    }

    public ConfirmTriggerMediaDeleteWindow(string mediaName, IReadOnlyList<string> usedBy)
    {
        InitializeComponent();
        UsageText.Text = $"\"{mediaName}\" wird aktuell als Event-Medium verwendet von: {string.Join(", ", usedBy)}.";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(TriggerMediaDeleteChoice.Cancel);
    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close(TriggerMediaDeleteChoice.KeepInItemsRemoveFromLibraryOnly);
    private void OnRemoveAndDeleteClick(object? sender, RoutedEventArgs e) => Close(TriggerMediaDeleteChoice.RemoveFromItemsAndDelete);
}
