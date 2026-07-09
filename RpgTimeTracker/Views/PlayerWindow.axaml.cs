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

    // Escape beendet nur lokal das Vollbild dieses Fensters, unabhängig vom SL-weiten
    // "Spieler-Vollbild"-Zustand (MainWindowViewModel.RemoteFullscreen bleibt unverändert) - so
    // lässt sich ein einzelnes verirrtes Fenster jederzeit ohne Seiteneffekte auf Remote-Clients
    // beheben. Ebenso der Button unten, für den Fall dass Escape z.B. durch ein anderes fokussiertes
    // Steuerelement abgefangen wird.
    // Pfeiltasten hier sind der SL-eigene Diashow-Navigationsweg: sie lösen denselben globalen
    // "Hervorheben"-Broadcast aus wie ein Klick in der Bibliothek-Liste (siehe
    // MainWindowViewModel.NextGalleryItemCommand) - anders als beim Spieler-Client gibt es beim
    // SL keine getrennte rein lokale Navigation.
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