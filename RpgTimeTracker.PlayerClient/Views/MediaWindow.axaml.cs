using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RpgTimeTracker.PlayerClient.ViewModels;

namespace RpgTimeTracker.PlayerClient.Views;

/// <summary>
///     Standalone window for the image/video display. Opens automatically as soon as a
///     medium is received, and is closed from the GM side via "Remove" (see ClientMainWindow).
/// </summary>
public partial class MediaWindow : Window
{
    public MediaWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    // Local closing, independent of the host: stops any running video playback and only
    // hides this window - the host state remains untouched, the next medium sent by the
    // host still reopens the window at the same place (see
    // ClientMainWindow.OnMediaWindowShouldShow).
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ClientMainWindowViewModel vm) vm.MediaPlayer?.Stop();

        Hide();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // Escape only ends this window's fullscreen locally - neither the persisted
    // MediaFullscreen setting nor a fullscreen state pushed by the host is changed,
    // so the next medium/push can open fullscreen again. Pure emergency exit in case,
    // e.g., the host fullscreen push or the local toggle button gets stuck.
    //
    // Arrow keys navigate purely locally through the gallery (no access to the host needed) -
    // see ClientMainWindowViewModel.NavigateGalleryLocally. While an event medium takes
    // precedence, the commands are no-ops (see _isShowingEventMedia).
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
            return;
        }

        if (DataContext is not ClientMainWindowViewModel vm) return;

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

    // A click anywhere in the media window also advances (classic slideshow behavior) -
    // the close button in the top right marks its own click as "handled" and therefore
    // doesn't bubble up here.
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ClientMainWindowViewModel vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            vm.NextGalleryItemCommand.Execute(null);
    }
}