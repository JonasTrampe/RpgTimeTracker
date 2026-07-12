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
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class MainWindow : Window
{
    /// <summary>
    ///     The new save format (a zip container - see MainWindowViewModel.ExportStateToZipBytes/
    ///     ImportStateFromZipOrJson). A distinct extension (rather than keeping .json now that the
    ///     content is a zip) was chosen specifically to avoid confusion with other data.
    /// </summary>
    private static readonly FilePickerFileType RttSaveFileType = new(LocalizationService.Get("MainWindow.FileTypes.GameState"))
    {
        Patterns = ["*.rtt-save"]
    };

    /// <summary>Old plain-JSON save format, kept only so Load still finds pre-.rtt-save files - see
    ///     MainWindowViewModel.ImportStateFromZipOrJson for the upgrade path.</summary>
    private static readonly FilePickerFileType JsonFileType = new(LocalizationService.Get("MainWindow.FileTypes.GameStateLegacyJson"))
    {
        Patterns = ["*.json"]
    };

    private static readonly FilePickerFileType CalendarJsonFileType = new(LocalizationService.Get("MainWindow.FileTypes.Calendar"))
    {
        Patterns = ["*.json"]
    };

    internal static readonly FilePickerFileType AudioFileType = new(LocalizationService.Get("MainWindow.FileTypes.SoundFile"))
    {
        Patterns = ["*.wav", "*.mp3", "*.ogg", "*.flac", "*.aiff", "*.aif", "*.m4a", "*.aac"]
    };

    internal static readonly FilePickerFileType MediaFileType = new(LocalizationService.Get("MainWindow.FileTypes.ImageOrVideo"))
    {
        Patterns =
        [
            "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp",
            "*.mp4", "*.m4v", "*.webm", "*.mkv", "*.mov", "*.avi"
        ]
    };

    /// <summary>Map floors are image-only for now - see MapItemViewModel/AddFloorToMapAsync.
    ///     Looping video as a floor background is a deferred follow-up (issue #16).</summary>
    private static readonly FilePickerFileType MapFloorImageFileType = new(LocalizationService.Get("MainWindow.FileTypes.MapFloorImage"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"]
    };

    /// <summary>A single exported map (image + starting fog per floor) as a self-contained zip -
    ///     see MainWindowViewModel.ExportMapToZipBytes/ImportMapFromZipBytes.</summary>
    private static readonly FilePickerFileType MapFileType = new(LocalizationService.Get("MainWindow.FileTypes.Map"))
    {
        Patterns = ["*.rtt-map"]
    };

    /// <summary>Bundles game state + all three libraries into one file - see
    ///     MainWindowViewModel.ExportFullSessionToZipBytes/ImportFullSessionFromZipBytes.</summary>
    private static readonly FilePickerFileType FullSessionFileType = new(LocalizationService.Get("MainWindow.FileTypes.FullSession"))
    {
        Patterns = ["*.rtt-session"]
    };

    // Event media (timer/alarm/interval) can still also be audio, but image/video
    // now comes exclusively from the media library (see ChooseTriggerMediaFromLibrary) -
    // only for audio does the direct file dialog remain, since there is no separate library concept
    // for event media (the sound library is meant for the items' "sound" field,
    // not for event media).
    private static readonly FilePickerFileType TriggerAudioFileType = new(LocalizationService.Get("MainWindow.FileTypes.Audio"))
    {
        Patterns = ["*.mp3", "*.wav", "*.ogg", "*.flac", "*.aiff", "*.aif", "*.m4a", "*.aac"]
    };

    private static readonly FilePickerFileType TextFileType = new(LocalizationService.Get("MainWindow.FileTypes.TextFile"))
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.TryAutoSaveOnClose();
        base.OnClosing(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedViewModel is not null)
            _attachedViewModel.LocalFullscreenRequested -= OnLocalFullscreenRequested;

        if (DataContext is MainWindowViewModel vm)
        {
            _attachedViewModel = vm;
            vm.LocalFullscreenRequested += OnLocalFullscreenRequested;

            MediaLibraryPanel.AddDialogTitle = LocalizationService.Get("MainWindow.Dialogs.AddMediaToLibrary");
            MediaLibraryPanel.AddFileTypeFilter = MediaFileType;
            MediaLibraryPanel.AddItemAsync = vm.AddMediaToLibraryFromPathAsync;
            MediaLibraryPanel.ExportAsync = vm.ExportMediaLibraryAsync;
            MediaLibraryPanel.ImportAsync = vm.ImportMediaLibraryAsync;

            SoundLibraryPanel.AddDialogTitle = LocalizationService.Get("MainWindow.Dialogs.AddSoundToLibrary");
            SoundLibraryPanel.AddFileTypeFilter = AudioFileType;
            SoundLibraryPanel.AddItemAsync = vm.AddSoundToLibraryFromPathAsync;
            SoundLibraryPanel.ExportAsync = vm.ExportSoundLibraryAsync;
            SoundLibraryPanel.ImportAsync = vm.ImportSoundLibraryAsync;

            MusicLibraryPanel.AddDialogTitle = LocalizationService.Get("MainWindow.Dialogs.AddMusicToLibrary");
            MusicLibraryPanel.AddFileTypeFilter = AudioFileType;
            MusicLibraryPanel.AddItemAsync = vm.AddMusicToLibraryFromPathAsync;
            MusicLibraryPanel.ExportAsync = vm.ExportMusicLibraryAsync;
            MusicLibraryPanel.ImportAsync = vm.ImportMusicLibraryAsync;

            vm.ConfirmTriggerMediaDeleteAsync = ShowConfirmTriggerMediaDeleteAsync;
            return;
        }

        _attachedViewModel = null;
    }

    // An event medium with "fullscreen" not only pushes remotely (RPC to clients), but also
    // switches its own, locally open player window to fullscreen - for solo tests/preview without a client.
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
        // Mark immediately (not only after the ClickCount check): the tile itself has
        // its own PointerPressed that sends the sound (OnSoundLibraryItemPointerPressed) -
        // a click on the icon should never trigger that, not even the first click of a double-click.
        e.Handled = true;
        if (e.ClickCount < 2 || sender is not Control control ||
            control.DataContext is not SoundLibraryItemViewModel item) return;

        var picker = new IconPickerWindow(icon => item.Icon = icon);
        picker.Show(this);
    }

    private void OnMusicIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Same reasoning as OnSoundIconPointerPressed: mark handled immediately so the tile's own
        // PointerPressed (OnMusicLibraryItemPointerPressed) never fires for a click on the icon.
        e.Handled = true;
        if (e.ClickCount < 2 || sender is not Control control ||
            control.DataContext is not MusicLibraryItemViewModel item) return;

        var picker = new IconPickerWindow(icon => item.Icon = icon);
        picker.Show(this);
    }

    private async void OnAddMapFloorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedMap: { } map }) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.AddFloorToMap"),
            AllowMultiple = false,
            FileTypeFilter = [MapFloorImageFileType]
        });

        if (files.Count == 0) return;

        var localPath = files[0].TryGetLocalPath();
        if (localPath is not null && DataContext is MainWindowViewModel vmForFloor)
            await vmForFloor.AddFloorToMapAsync(map, localPath);
    }

    private MapLiveWindow? _openMapLiveWindow;

    private void OnEditSelectedFloorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedMap is not { } map || map.Floors.Count == 0) return;

        // Extracted from the per-floor tile to a single top-level button, so it needs an explicit
        // floor to act on - default to the first floor if none is selected in the list yet.
        if (vm.EditingFloor is null || !map.Floors.Contains(vm.EditingFloor)) vm.EditingFloor = map.Floors[0];

        // A map currently shown to players must not also be edited (Prepare) - see
        // MainWindowViewModel.NotifyMapPrepareWindowOpened/IsSelectedMapOpenToPlayers. The Edit
        // button is already disabled for this case; this is defense-in-depth.
        if (vm.IsMapOpenToPlayers && ReferenceEquals(vm.OpenMap, map)) return;

        var editor = new MapPrepareWindow(vm, map);
        editor.Show(this);
    }

    private void OnMoveFloorUpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedMap is not { } map ||
            sender is not Control { DataContext: MapFloorItemViewModel floor }) return;

        map.MoveFloorUp(floor);
    }

    private void OnMoveFloorDownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedMap is not { } map ||
            sender is not Control { DataContext: MapFloorItemViewModel floor }) return;

        map.MoveFloorDown(floor);
    }

    private void OnShowMapClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedMap is not { } map) return;

        // A map currently being prepared must not also be streamed - see
        // MainWindowViewModel.IsSelectedMapBeingPrepared. The Show button is already disabled for
        // this case; this is defense-in-depth.
        if (vm.IsSelectedMapBeingPrepared) return;

        if (_openMapLiveWindow is not null)
        {
            _openMapLiveWindow.Activate();
            return;
        }

        var editor = new MapLiveWindow(vm, map);
        editor.Closed += (_, _) => _openMapLiveWindow = null;
        _openMapLiveWindow = editor;
        editor.Show(this);
    }

    private async void OnExportMapClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control { DataContext: MapItemViewModel map }) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ExportMap"),
            SuggestedFileName = $"{map.Name}.rtt-map",
            DefaultExtension = "rtt-map",
            FileTypeChoices = [MapFileType]
        });
        if (file is null) return;

        try
        {
            var zipBytes = vm.ExportMapToZipBytes(map);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(zipBytes);
            vm.NotifyActionStatus(string.Format(LocalizationService.Get("MainWindowViewModel.Status.MapExported"), map.Name));
        }
        catch (Exception ex)
        {
            vm.MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    private async void OnImportMapClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ImportMap"),
            AllowMultiple = false,
            FileTypeFilter = [MapFileType]
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        vm.ImportMapFromZipBytes(memoryStream.ToArray());
    }

    private async void OnExportFullSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ExportFullSession"),
            SuggestedFileName = $"rpg-zeit-sitzung-{DateTime.Now:yyyy-MM-dd_HHmm}.rtt-session",
            DefaultExtension = "rtt-session",
            FileTypeChoices = [FullSessionFileType]
        });
        if (file is null) return;

        try
        {
            var zipBytes = vm.ExportFullSessionToZipBytes();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(zipBytes);
            vm.NotifyActionStatus(LocalizationService.Get("MainWindow.Status.SessionExported"));
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ExportFailed"), ex.Message);
        }
    }

    private async void OnImportFullSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ImportFullSession"),
            AllowMultiple = false,
            FileTypeFilter = [FullSessionFileType]
        });

        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            vm.ImportFullSessionFromZipBytes(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.ImportFailed"), ex.Message);
        }
    }

    private async void OnSendMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.SendMediaToPlayers"),
            AllowMultiple = false,
            FileTypeFilter = [MediaFileType]
        });

        if (files.Count == 0) return;

        try
        {
            var localPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                vm.MediaErrorMessage = LocalizationService.Get("MainWindow.Errors.MediaNoLocalFilePath");
                return;
            }

            await vm.SendMediaFromPathAsync(localPath);
        }
        catch (Exception ex)
        {
            vm.MediaErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.MediaCouldNotBeSent"), ex.Message);
        }
    }

    private async void OnSendSoundClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.SendSoundToPlayers"),
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType]
        });

        if (files.Count == 0) return;

        try
        {
            var localPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                vm.MediaErrorMessage = LocalizationService.Get("MainWindow.Errors.SoundNoLocalFilePath");
                return;
            }

            await vm.SendSoundFromPathAsync(localPath);
        }
        catch (Exception ex)
        {
            vm.MediaErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.SoundCouldNotBeSent"), ex.Message);
        }
    }

    private void OnSoundLibraryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SoundLibraryItemViewModel item)
            item.PlayCommand.Execute(null);
    }

    private void OnMusicLibraryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is MusicLibraryItemViewModel item)
            item.PlayCommand.Execute(null);
    }

    private void OnSentMediaItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SentMediaItemViewModel item)
            item.HighlightCommand.Execute(null);
    }

    // Event medium (timer/alarm/interval automatically trigger an image/video/audio): two
    // click handlers each ("From library…" for image/video, "Audio…" for audio from disk) for the
    // "Add" tab (DataContext = MainWindowViewModel, target one of the three staging configs)
    // plus one each for an already existing item (DataContext = TimelineDisplayItemViewModel).
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
    ///     Image/video for an event medium comes exclusively from the media library (no more
    ///     file dialog) - this ensures that every event medium image/video also
    ///     exists as a library entry and the delete warning (RemoveMediaLibraryItem) can
    ///     reliably detect it.
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
            Title = LocalizationService.Get("MainWindow.Dialogs.ChooseAudioForEventMedium"),
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
    ///     Attached to MainWindowViewModel.ConfirmTriggerMediaDeleteAsync (see OnDataContextChanged) -
    ///     the view model cannot open a window itself, but needs the confirmation before an already
    ///     assigned library image/video is actually deleted.
    /// </summary>
    private async Task<TriggerMediaDeleteChoice> ShowConfirmTriggerMediaDeleteAsync(MediaLibraryItemViewModel item,
        IReadOnlyList<string> usedBy)
    {
        var dialog = new ConfirmTriggerMediaDeleteWindow(item.Name, usedBy);
        return await dialog.ShowDialog<TriggerMediaDeleteChoice>(this);
    }

    // File dialogs need access to the TopLevel/StorageProvider and
    // therefore deliberately remain in the code-behind instead of the view model.
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.SaveGameState"),
            SuggestedFileName = $"rpg-zeit-{DateTime.Now:yyyy-MM-dd_HHmm}.rtt-save",
            DefaultExtension = "rtt-save",
            FileTypeChoices = [RttSaveFileType]
        });
        if (file is null) return;

        try
        {
            var zipBytes = vm.ExportStateToZipBytes();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(zipBytes);
            var localPath = file.TryGetLocalPath();
            if (localPath is not null) ThemeSettingsService.SaveLastSaveFilePath(localPath);
            vm.NotifyActionStatus(LocalizationService.Get("MainWindow.Status.GameStateSaved"));
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.SaveFailed"), ex.Message);
        }
    }

    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.LoadGameState"),
            AllowMultiple = false,
            // Both the current .rtt-save format and the old plain-JSON format are accepted here -
            // ImportStateFromZipOrJson tells them apart and upgrades old saves transparently.
            FileTypeFilter = [RttSaveFileType, JsonFileType]
        });

        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            vm.ImportStateFromZipOrJson(memoryStream.ToArray());
            var localPath = files[0].TryGetLocalPath();
            if (localPath is not null) ThemeSettingsService.SaveLastSaveFilePath(localPath);
            vm.NotifyActionStatus(LocalizationService.Get("MainWindow.Status.GameStateLoaded"));
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.LoadFailed"), ex.Message);
        }
    }

    // Save target deliberately freely selectable on every export (instead of a fixed path) -
    // the same file dialog approach as save/load.
    private async void OnExportSessionLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ExportSessionLog"),
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
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.LogExportFailed"), ex.Message);
        }
    }

    private async void OnExportCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ExportCalendar"),
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
            vm.NotifyActionStatus(LocalizationService.Get("MainWindow.Status.CalendarExported"));
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.CalendarExportFailed"), ex.Message);
        }
    }

    private async void OnImportCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("MainWindow.Dialogs.ImportCalendar"),
            AllowMultiple = false,
            FileTypeFilter = [CalendarJsonFileType]
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.ImportCalendarFromJson(await reader.ReadToEndAsync());
            vm.NotifyActionStatus(LocalizationService.Get("MainWindow.Status.CalendarImported"));
        }
        catch (Exception ex)
        {
            vm.ClockErrorMessage = string.Format(LocalizationService.Get("MainWindow.Errors.CalendarImportFailed"), ex.Message);
        }
    }
}