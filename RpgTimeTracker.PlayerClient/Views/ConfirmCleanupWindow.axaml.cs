using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RpgTimeTracker.PlayerClient.Views;

public partial class ConfirmCleanupWindow : Window
{
    public ConfirmCleanupWindow()
    {
        InitializeComponent();
    }

    private void OnYesClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnNoClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}