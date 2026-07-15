using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.Views;

public partial class ChooseLibraryScopeWindow : Window
{
    public ChooseLibraryScopeWindow()
    {
        InitializeComponent();
    }

    private void OnSharedClick(object? sender, RoutedEventArgs e)
    {
        Close(LibraryScope.Shared);
    }

    private void OnSessionClick(object? sender, RoutedEventArgs e)
    {
        Close(LibraryScope.SessionLocal);
    }
}