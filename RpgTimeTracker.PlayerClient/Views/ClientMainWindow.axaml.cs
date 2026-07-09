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

    // Escape beendet nur lokal das Vollbild DIESES Fensters (die Zeitliste) - trifft ein
    // Medienfenster gerade fullscreen, ist das MediaWindow.OnKeyDown zuständig, da Tastatur-
    // Events dem jeweils fokussierten Fenster zugestellt werden. Ein vom Host gepushter
    // Vollbild-Zustand bleibt unverändert, der nächste Push kann also wieder fullscreen öffnen.
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

    // Feuert unconditionally bei jedem neuen Medium (siehe Doc-Kommentar am Event) statt nur
    // bei einer HasMedia-Änderung - so öffnet sich das Fenster auch wieder, wenn der Spieler es
    // manuell geschlossen hatte und danach ein zweites Medium derselben Art gesendet wird.
    //
    // Das Fenster wird bewusst NICHT geschlossen/neu erzeugt, wenn der SL ein Medium entfernt
    // (siehe OnMediaWindowShouldHide) - nur Hide()/Show() auf derselben Instanz, damit Position
    // und Größe über mehrere Medien hinweg erhalten bleiben ("an der letzten Position mit der
    // letzten Größe wieder im Vordergrund").
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

    // Das Medienfenster soll optisch nahtlos an die Stelle der Zeitliste treten - dieselbe
    // Position/Größe/Zustand wie das Hauptfenster, statt an einer eigenen (u.U. ganz anderen)
    // Bildschirmposition aufzutauchen. Erst in Normal schalten, bevor Position/Größe gesetzt
    // werden, da viele Fenstermanager explizite Geometrie-Änderungen ignorieren, solange ein
    // Fenster noch maximiert/fullscreen ist.
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

    // Ziel: Medium hat Vorrang (falls gerade eines sichtbar ist), sonst die Zeitliste -
    // spiegelt dieselbe Priorität wie der lokale Vollbild-Umschalter auf der Host-Seite.
    private void OnRemoteFullscreenRequested(bool fullscreen)
    {
        var hasVisibleMedia = (DataContext as ClientMainWindowViewModel)?.HasMedia == true &&
                              _mediaWindow is { IsVisible: true };
        Window target = hasVisibleMedia ? _mediaWindow! : this;

        target.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
        UpdateMediaWindowTopmost();
    }

    // Ein normales Fenster erscheint auf vielen Systemen sonst HINTER einem fullscreen
    // geschalteten Geschwisterfenster statt davor - Topmost erzwingt, dass das Medium über der
    // fullscreen geschalteten Zeitliste sichtbar bleibt. Nur aktiv, solange die Liste tatsächlich
    // fullscreen ist, damit das Medienfenster sich sonst normal wie jedes andere Fenster verhält.
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

    // Fragt vor dem tatsächlichen Schließen nach, ob lokal zwischengespeicherte Medien-Temp-
    // Dateien gelöscht werden sollen - aber nur, wenn überhaupt welche vorhanden sind (siehe
    // ClientMainWindowViewModel.HasTempFilesToClean). Window.Closing kann nicht direkt async
    // sein, daher das Standard-Avalonia-Muster: einmal abbrechen, den Dialog awaiten, danach
    // erneut (diesmal ohne Nachfrage) schließen.
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