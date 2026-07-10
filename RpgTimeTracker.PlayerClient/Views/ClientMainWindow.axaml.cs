using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RpgTimeTracker.PlayerClient.ViewModels;

namespace RpgTimeTracker.PlayerClient.Views;

public partial class ClientMainWindow : Window
{
    private bool _closeConfirmed;
    private MediaWindow? _mediaWindow;

    public ClientMainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnKeyDown;
    }

    // Escape only ends the fullscreen of THIS window locally (the timeline) - if a media
    // window is currently fullscreen, MediaWindow.OnKeyDown is responsible, since keyboard
    // events are delivered to whichever window is focused. A fullscreen state pushed by the
    // host remains unchanged, so the next push can open fullscreen again.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || WindowState != WindowState.FullScreen) return;

        WindowState = WindowState.Normal;
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClientMainWindowViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.MediaWindowShouldShow += OnMediaWindowShouldShow;
            vm.MediaWindowShouldHide += OnMediaWindowShouldHide;
            vm.RemoteFullscreenRequested += OnRemoteFullscreenRequested;
        }
    }

    // Fires unconditionally on every new medium (see doc comment on the event) instead of only
    // on a HasMedia change - this way the window reopens even if the player closed it manually
    // and a second medium of the same kind is then sent.
    //
    // The window is deliberately NOT closed/recreated when the GM removes a medium
    // (see OnMediaWindowShouldHide) - only Hide()/Show() on the same instance, so position
    // and size are preserved across multiple media ("back in the foreground at the last
    // position with the last size").
    private void OnMediaWindowShouldShow()
    {
        if (DataContext is not ClientMainWindowViewModel vm) return;

        if (_mediaWindow is null)
        {
            _mediaWindow = new MediaWindow { DataContext = vm };
            _mediaWindow.Closed += (_, _) => _mediaWindow = null;
        }

        SyncMediaWindowToMainWindow();

        if (vm.MediaFullscreen) _mediaWindow.WindowState = WindowState.FullScreen;

        if (!_mediaWindow.IsVisible) _mediaWindow.Show();

        UpdateMediaWindowTopmost();
        _mediaWindow.Activate();
    }

    // The media window should visually take the place of the timeline seamlessly - the same
    // position/size/state as the main window, instead of appearing at its own (possibly quite
    // different) screen position. Switch to Normal first before setting position/size, since
    // many window managers ignore explicit geometry changes while a window is still
    // maximized/fullscreen.
    private void SyncMediaWindowToMainWindow()
    {
        if (_mediaWindow is null) return;

        _mediaWindow.WindowState = WindowState.Normal;
        _mediaWindow.Position = Position;
        _mediaWindow.Width = Width;
        _mediaWindow.Height = Height;
        _mediaWindow.WindowState = WindowState;
    }

    private void OnMediaWindowShouldHide()
    {
        _mediaWindow?.Hide();
    }

    private void OnOpenAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        about.Show(this);
    }

    // Goal: medium takes precedence (if one is currently visible), otherwise the timeline -
    // mirrors the same priority as the local fullscreen toggle on the host side.
    private void OnRemoteFullscreenRequested(bool fullscreen)
    {
        var vm = DataContext as ClientMainWindowViewModel;
        var hasVisibleMedia = (vm?.HasMedia == true || vm?.MapDisplay.IsShowingMap == true) &&
                              _mediaWindow is { IsVisible: true };
        Window target = hasVisibleMedia ? _mediaWindow! : this;

        target.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
        UpdateMediaWindowTopmost();
    }

    // Otherwise a normal window appears BEHIND a fullscreen sibling window on many systems
    // instead of in front - Topmost forces the medium to stay visible above the fullscreen
    // timeline. Only active while the list is actually fullscreen, so the media window
    // otherwise behaves normally like any other window.
    private void UpdateMediaWindowTopmost()
    {
        if (_mediaWindow is not { IsVisible: true }) return;
        _mediaWindow.Topmost = WindowState == WindowState.FullScreen;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not ClientMainWindowViewModel vm) return;
        if (e.PropertyName != nameof(ClientMainWindowViewModel.MediaFullscreen)) return;
        if (_mediaWindow is null) return;

        if (vm.MediaFullscreen)
            _mediaWindow.WindowState = WindowState.FullScreen;
        else
            SyncMediaWindowToMainWindow();
    }

    // Asks before actually closing whether locally cached media temp files should be
    // deleted - but only if any exist at all (see ClientMainWindowViewModel.HasTempFilesToClean).
    // Window.Closing cannot be directly async, hence the standard Avalonia pattern: cancel
    // once, await the dialog, then close again afterward (this time without asking).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && DataContext is ClientMainWindowViewModel { HasTempFilesToClean: true } vm)
        {
            e.Cancel = true;
            _ = ConfirmCleanupAndCloseAsync(vm);
            return;
        }

        base.OnClosing(e);
    }

    private async Task ConfirmCleanupAndCloseAsync(ClientMainWindowViewModel vm)
    {
        var dialog = new ConfirmCleanupWindow();
        var deleteFiles = await dialog.ShowDialog<bool>(this);
        if (deleteFiles) vm.DeleteAllTempFiles();

        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _mediaWindow?.Close();

        if (DataContext is ClientMainWindowViewModel vm) vm.Dispose();

        base.OnClosed(e);
    }
}