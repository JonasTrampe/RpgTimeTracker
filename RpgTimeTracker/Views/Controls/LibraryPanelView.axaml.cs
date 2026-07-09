using System;
using System.Collections;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RpgTimeTracker.Views.Controls;

public partial class LibraryPanelView : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(Description));

    public static readonly StyledProperty<string?> AddButtonLabelProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(AddButtonLabel), "+ Zur Bibliothek hinzufügen");

    public static readonly StyledProperty<string?> EmptyStateTextProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(EmptyStateText));

    public static readonly StyledProperty<bool> HasNoItemsProperty =
        AvaloniaProperty.Register<LibraryPanelView, bool>(nameof(HasNoItems));

    public static readonly StyledProperty<string?> ErrorMessageProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(ErrorMessage));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<LibraryPanelView, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<LibraryPanelView, IDataTemplate?>(nameof(ItemTemplate));

    public LibraryPanelView()
    {
        InitializeComponent();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? AddButtonLabel
    {
        get => GetValue(AddButtonLabelProperty);
        set => SetValue(AddButtonLabelProperty, value);
    }

    public string? EmptyStateText
    {
        get => GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    public bool HasNoItems
    {
        get => GetValue(HasNoItemsProperty);
        set => SetValue(HasNoItemsProperty, value);
    }

    public string? ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    // Datei-Dialog-Konfiguration und die eigentliche Add/Export/Import-Verarbeitung kommen vom
    // Aufrufer (siehe MainWindow.axaml.cs, OnDataContextChanged) statt hier für jede Bibliothek
    // dupliziert zu werden - beide unterscheiden sich nur im Dateityp-Filter und der
    // ViewModel-Methode, die den gewählten Pfad tatsächlich verarbeitet.
    public string AddDialogTitle { get; set; } = "Datei hinzufügen";
    public FilePickerFileType AddFileTypeFilter { get; set; } = new("Alle Dateien") { Patterns = ["*.*"] };
    public Func<string, Task>? AddItemAsync { get; set; }
    public Func<string, Task>? ExportAsync { get; set; }
    public Func<string, Task>? ImportAsync { get; set; }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (AddItemAsync is null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AddDialogTitle,
            AllowMultiple = false,
            FileTypeFilter = [AddFileTypeFilter]
        });

        if (files.Count == 0) return;

        try
        {
            var localPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                ErrorMessage = "Datei konnte nicht hinzugefügt werden: kein lokaler Dateipfad verfügbar.";
                return;
            }

            await AddItemAsync(localPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Datei konnte nicht hinzugefügt werden: {ex.Message}";
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (ExportAsync is null) return;

        var folder = await PickFolderAsync("Bibliothek exportieren nach");
        if (folder is not null) await ExportAsync(folder);
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (ImportAsync is null) return;

        var folder = await PickFolderAsync("Bibliothek importieren von");
        if (folder is not null) await ImportAsync(folder);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}