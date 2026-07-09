using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RpgTimeTracker.PlayerClient.ViewModels;

namespace RpgTimeTracker.PlayerClient.Views;

/// <summary>
///     Eigenständiges Fenster für die Bild-/Video-Anzeige. Wird automatisch geöffnet, sobald ein
///     Medium empfangen wird, und vom SL aus über "Entfernen" geschlossen (siehe ClientMainWindow).
/// </summary>
public partial class MediaWindow : Window
{
    public MediaWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    // Lokales Schließen, unabhängig vom Host: stoppt eine evtl. laufende Videowiedergabe und
    // versteckt nur dieses Fenster - der Host-Zustand bleibt unberührt, das nächste vom Host
    // gesendete Medium öffnet das Fenster trotzdem wieder an derselben Stelle (siehe
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

    // Escape beendet nur lokal das Vollbild dieses Fensters - weder die persistierte
    // MediaFullscreen-Einstellung noch ein vom Host gepushter Vollbild-Zustand werden verändert,
    // das nächste Medium/der nächste Push kann also wieder fullscreen öffnen. Reiner
    // Notausstieg, falls z.B. der Host-Vollbild-Push oder der lokale Umschalt-Button hängen bleibt.
    //
    // Pfeiltasten navigieren rein lokal durch die Galerie (kein Zugriff auf den Host nötig) -
    // siehe ClientMainWindowViewModel.NavigateGalleryLocally. Während ein Event-Medium Vorrang hat,
    // sind die Commands No-Ops (siehe _isShowingEventMedia).
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

    // Klick irgendwo im Medienfenster schaltet ebenfalls weiter (klassisches Diashow-Verhalten) -
    // der Schließen-Button oben rechts markiert seinen eigenen Klick als "handled" und bubbled
    // deshalb nicht hierher.
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ClientMainWindowViewModel vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            vm.NextGalleryItemCommand.Execute(null);
    }
}