using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType JsonFileType = new("Spielstand (JSON)")
    {
        Patterns = ["*.json"]
    };

    private static readonly FilePickerFileType CalendarJsonFileType = new("Kalender (JSON)")
    {
        Patterns = ["*.json"]
    };

    internal static readonly FilePickerFileType AudioFileType = new("Sound-Datei")
    {
        Patterns = ["*.wav", "*.mp3", "*.ogg", "*.flac", "*.aiff", "*.aif", "*.m4a", "*.aac"]
    };

    internal static readonly FilePickerFileType MediaFileType = new("Bild oder Video")
    {
        Patterns =
        [
            "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp",
            "*.mp4", "*.m4v", "*.webm", "*.mkv", "*.mov", "*.avi"
        ]
    };

    // Event-Medien (Timer/Wecker/Intervall) können weiterhin auch Audio sein, aber Bild/Video
    // kommen jetzt ausschließlich aus der Medienbibliothek (siehe ChooseTriggerMediaFromLibrary) -
    // nur für Audio bleibt der direkte Datei-Dialog, da es dafür kein separates Bibliothekskonzept
    // für Event-Medien gibt (die Sound-Bibliothek ist für das "Sound"-Feld der Elemente gedacht,
    // nicht für Event-Medien).
    private static readonly FilePickerFileType TriggerAudioFileType = new("Audio")
    {
        Patterns = ["*.mp3", "*.wav", "*.ogg", "*.flac", "*.aiff", "*.aif", "*.m4a", "*.aac"]
    };

    private static readonly FilePickerFileType TextFileType = new("Textdatei")
    {
        Patterns = ["*.txt", "*.md"]
    };

    private MainWindowViewModel? _attachedViewModel;

    private PlayerWindow? _playerWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedViewModel is not null)
            _attachedViewModel.LocalFullscreenRequested -= OnLocalFullscreenRequested;

        if (DataContext is MainWindowViewModel vm)
        {
            _attachedViewModel = vm;
            vm.LocalFullscreenRequested += OnLocalFullscreenRequested;

            MediaLibraryPanel.AddDialogTitle = "Bild oder Video zur Bibliothek hinzufügen";
            MediaLibraryPanel.AddFileTypeFilter = MediaFileType;
            MediaLibraryPanel.AddItemAsync = vm.AddMediaToLibraryFromPathAsync;
            MediaLibraryPanel.ExportAsync = vm.ExportMediaLibraryAsync;
            MediaLibraryPanel.ImportAsync = vm.ImportMediaLibraryAsync;

            SoundLibraryPanel.AddDialogTitle = "Sound zur Bibliothek hinzufügen";
            SoundLibraryPanel.AddFileTypeFilter = AudioFileType;
            SoundLibraryPanel.AddItemAsync = vm.AddSoundToLibraryFromPathAsync;
            SoundLibraryPanel.ExportAsync = vm.ExportSoundLibraryAsync;
            SoundLibraryPanel.ImportAsync = vm.ImportSoundLibraryAsync;

            vm.ConfirmTriggerMediaDeleteAsync = ShowConfirmTriggerMediaDeleteAsync;
            return;
        }

        _attachedViewModel = null;
    }

    // Ein Event-Medium mit "Vollbild" pusht nicht nur remote (RPC an Clients), sondern schaltet
    // auch das eigene, lokal offene Spielerfenster fullscreen - für Solo-Tests/Vorschau ohne Client.
    private void OnLocalFullscreenRequested(bool fullscreen)
    {
        if (_playerWindow is null) return;

        _playerWindow.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
    }

    private void OnOpenPlayerWindowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (_playerWindow is not null)
        {
            _playerWindow.Activate();
            return;
        }

        _playerWindow = new PlayerWindow
        {
            DataContext = vm
        };
        vm.IsPlayerWindowOpen = true;
        vm.ReportLocalPlayerFullscreen(false);

        _playerWindow.PropertyChanged += OnPlayerWindowPropertyChanged;

        _playerWindow.Closed += (_, _) =>
        {
            _playerWindow.PropertyChanged -= OnPlayerWindowPropertyChanged;
            _playerWindow = null;
            vm.ReportLocalPlayerFullscreen(false);
            vm.IsPlayerWindowOpen = false;
        };

        _playerWindow.Show(this);
    }

    private void OnOpenAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        about.Show(this);
    }

    private void OnPlayerWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty ||
            sender is not Window window ||
            DataContext is not MainWindowViewModel vm) return;

        vm.ReportLocalPlayerFullscreen(window.WindowState == WindowState.FullScreen);
    }

    private void OnTimelineIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not Control control ||
            control.DataContext is not TimelineDisplayItemViewModel item) return;

        var picker = new IconPickerWindow(item);
        picker.Show(this);
    }

    private void OnSoundIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Sofort (nicht erst nach dem ClickCount-Check) markieren: die Kachel selbst hat
        // ein eigenes PointerPressed, das den Sound sendet (OnSoundLibraryItemPointerPressed) -
        // ein Klick aufs Icon soll das nie auslösen, auch nicht der erste Klick eines Doppelklicks.
        e.Handled = true;
        if (e.ClickCount < 2 || sender is not Control control ||
            control.DataContext is not SoundLibraryItemViewModel item) return;

        var picker = new IconPickerWindow(icon => item.Icon = icon);
        picker.Show(this);
    }

    private async void OnSendMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Bild oder Video an Spieler senden",
            AllowMultiple = false,
            FileTypeFilter = [MediaFileType]
        });

        if (files.Count == 0) return;

        try
        {
            var localPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                vm.MediaErrorMessage = "Medium konnte nicht gesendet werden: kein lokaler Dateipfad verfügbar.";
                return;
            }

            await vm.SendMediaFromPathAsync(localPath);
        }
        catch (Exception ex)
        {
            vm.MediaErrorMessage = $"Medium konnte nicht gesendet werden: {ex.Message}";
        }
    }

    private async void OnSendSoundClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Sound an Spieler senden",
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType]
        });

        if (files.Count == 0) return;

        try
        {
            var localPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                vm.MediaErrorMessage = "Sound konnte nicht gesendet werden: kein lokaler Dateipfad verfügbar.";
                return;
            }

            await vm.SendSoundFromPathAsync(localPath);
        }
        catch (Exception ex)
        {
            vm.MediaErrorMessage = $"Sound konnte nicht gesendet werden: {ex.Message}";
        }
    }

    private void OnSoundLibraryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SoundLibraryItemViewModel item)
            item.PlayCommand.Execute(null);
    }

    private void OnSentMediaItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SentMediaItemViewModel item)
            item.HighlightCommand.Execute(null);
    }

    // Event-Medium (Timer/Wecker/Intervall lösen automatisch ein Bild/Video/Audio aus): je zwei
    // Klick-Handler ("Aus Bibliothek…" für Bild/Video, "Audio…" für Audio von der Platte) für die
    // "Hinzufügen"-Reiter (DataContext = MainWindowViewModel, Ziel eine der drei Staging-Configs)
    // plus je einer für ein bereits bestehendes Element (DataContext = TimelineDisplayItemViewModel).
    private void OnChooseTimerTriggerMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) ChooseTriggerMediaFromLibrary(vm.MediaLibrary, vm.NewTimerTriggerMedia);
    }

    private async void OnChooseTimerTriggerAudioClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await ChooseTriggerAudioAsync(vm.NewTimerTriggerMedia);
    }

    private void OnChooseAlarmTriggerMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) ChooseTriggerMediaFromLibrary(vm.MediaLibrary, vm.NewAlarmTriggerMedia);
    }

    private async void OnChooseAlarmTriggerAudioClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await ChooseTriggerAudioAsync(vm.NewAlarmTriggerMedia);
    }

    private void OnChooseIntervalTriggerMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) ChooseTriggerMediaFromLibrary(vm.MediaLibrary, vm.NewIntervalTriggerMedia);
    }

    private async void OnChooseIntervalTriggerAudioClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await ChooseTriggerAudioAsync(vm.NewIntervalTriggerMedia);
    }

    private void OnChooseItemTriggerMediaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not TimelineDisplayItemViewModel item) return;
        if (DataContext is MainWindowViewModel vm) ChooseTriggerMediaFromLibrary(vm.MediaLibrary, item.TriggerMedia);
    }

    private async void OnChooseItemTriggerAudioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is TimelineDisplayItemViewModel item)
            await ChooseTriggerAudioAsync(item.TriggerMedia);
    }

    /// <summary>
    ///     Bild/Video für ein Event-Medium kommt ausschließlich aus der Medienbibliothek (kein
    ///     Datei-Dialog mehr) - damit ist sichergestellt, dass jedes Event-Medium-Bild/Video auch
    ///     als Bibliothekseintrag existiert und die Lösch-Warnung (RemoveMediaLibraryItem) es
    ///     zuverlässig erkennen kann.
    /// </summary>
    private void ChooseTriggerMediaFromLibrary(IEnumerable<MediaLibraryItemViewModel> library, TriggerMediaConfig target)
    {
        var picker = new MediaLibraryPickerWindow(library, item =>
        {
            target.Path = item.LocalPath;
            target.FileName = item.Name;
            target.Kind = item.Kind;
            target.Loop = item.Loop;
        });
        picker.Show(this);
    }

    private async Task ChooseTriggerAudioAsync(TriggerMediaConfig target)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Audio-Datei für Event-Medium wählen",
            AllowMultiple = false,
            FileTypeFilter = [TriggerAudioFileType]
        });

        if (files.Count == 0) return;

        var localPath = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath)) return;
        if (!MediaTypeHelper.TryGetKind(localPath, out var kind, out _) || kind != MediaKind.Audio) return;

        target.Path = localPath;
        target.FileName = Path.GetFileName(localPath);
        target.Kind = kind;
    }

    private void OnMediaLibraryItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is MediaLibraryItemViewModel item)
            item.ShowCommand.Execute(null);
    }

    /// <summary>
    ///     An MainWindowViewModel.ConfirmTriggerMediaDeleteAsync gehängt (siehe OnDataContextChanged) -
    ///     das ViewModel kann kein Window selbst öffnen, braucht die Rückfrage aber, bevor ein noch
    ///     zugewiesenes Bibliotheks-Bild/Video tatsächlich gelöscht wird.
    /// </summary>
    private async Task<TriggerMediaDeleteChoice> ShowConfirmTriggerMediaDeleteAsync(MediaLibraryItemViewModel item,
        IReadOnlyList<string> usedBy)
    {
        var dialog = new ConfirmTriggerMediaDeleteWindow(item.Name, usedBy);
        return await dialog.ShowDialog<TriggerMediaDeleteChoice>(this);
    }

    // Datei-Dialoge brauchen Zugriff auf den TopLevel/StorageProvider und
    // bleiben deshalb bewusst im Code-Behind statt im ViewModel.
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Spielstand speichern",
            SuggestedFileName = $"rpg-zeit-{DateTime.Now:yyyy-MM-dd_HHmm}.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType]
        });
        if (file is null) return;

        try
        {
            var json = vm.ExportStateToJson();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            vm.NotifyActionStatus("Spielstand gespeichert");
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = $"Speichern fehlgeschlagen: {ex.Message}";
        }
    }

    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Spielstand laden",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType]
        });

        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            vm.ImportStateFromJson(json);
            vm.NotifyActionStatus("Spielstand geladen");
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = $"Laden fehlgeschlagen: {ex.Message}";
        }
    }

    // Speicherziel bewusst bei jedem Export frei wählbar (statt eines festen Pfads) -
    // derselbe Datei-Dialog-Ansatz wie Speichern/Laden.
    private async void OnExportSessionLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Sitzungsprotokoll exportieren",
            SuggestedFileName = $"sitzungsprotokoll-{DateTime.Now:yyyy-MM-dd_HHmm}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [TextFileType]
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.BuildSessionLogExportText());
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = $"Protokoll-Export fehlgeschlagen: {ex.Message}";
        }
    }

    private async void OnExportCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Kalender exportieren",
            SuggestedFileName = $"kalender-{DateTime.Now:yyyy-MM-dd_HHmm}.json",
            DefaultExtension = "json",
            FileTypeChoices = [CalendarJsonFileType]
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.ExportCalendarToJson());
            vm.NotifyActionStatus("Kalender exportiert");
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = $"Kalender-Export fehlgeschlagen: {ex.Message}";
        }
    }

    private async void OnImportCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Kalender importieren",
            AllowMultiple = false,
            FileTypeFilter = [CalendarJsonFileType]
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.ImportCalendarFromJson(await reader.ReadToEndAsync());
            vm.NotifyActionStatus("Kalender importiert");
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = $"Kalender-Import fehlgeschlagen: {ex.Message}";
        }
    }
}