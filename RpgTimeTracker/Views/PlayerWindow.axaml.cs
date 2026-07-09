using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class PlayerWindow : Window
{
    public PlayerWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
            ExitFullscreenButton.IsVisible = change.GetNewValue<WindowState>() == WindowState.FullScreen;
    }

    // Escape only locally ends this window's fullscreen, independent of the GM-wide
    // "player fullscreen" state (MainWindowViewModel.RemoteFullscreen remains unchanged) - this
    // allows a single stray window to be fixed at any time without side effects on remote clients.
    // Likewise the button below, in case Escape is intercepted by e.g. another focused
    // control.
    // Arrow keys here are the GM's own slideshow navigation path: they trigger the same global
    // "highlight" broadcast as a click in the library list (see
    // MainWindowViewModel.NextGalleryItemCommand) - unlike the player client, the
    // GM has no separate purely local navigation.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
            return;
        }

        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.Right)
        {
            vm.NextGalleryItemCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            vm.PreviousGalleryItemCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnExitFullscreenClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMediaPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            vm.NextGalleryItemCommand.Execute(null);
    }
}