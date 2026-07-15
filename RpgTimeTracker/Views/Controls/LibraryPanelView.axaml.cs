using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

public partial class LibraryPanelView : UserControl
{
    public static readonly StyledProperty<ObservableCollection<TagViewModel>?> AllTagsProperty =
        AvaloniaProperty.Register<LibraryPanelView, ObservableCollection<TagViewModel>?>(nameof(AllTags));

    public static readonly DirectProperty<LibraryPanelView, ObservableCollection<TagFilterOptionViewModel>> FilterOptionsProperty =
        AvaloniaProperty.RegisterDirect<LibraryPanelView, ObservableCollection<TagFilterOptionViewModel>>(
            nameof(FilterOptions), o => o.FilterOptions);

    public static readonly DirectProperty<LibraryPanelView, IEnumerable> FilteredItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<LibraryPanelView, IEnumerable>(
            nameof(FilteredItemsSource), o => o.FilteredItemsSource);

    public static readonly DirectProperty<LibraryPanelView, bool> HasNoFilterTagsProperty =
        AvaloniaProperty.RegisterDirect<LibraryPanelView, bool>(nameof(HasNoFilterTags), o => o.HasNoFilterTags);

    private readonly ObservableCollection<TagFilterOptionViewModel> _filterOptions = [];
    private readonly ObservableCollection<object> _filteredItems = [];
    private readonly HashSet<Guid> _selectedFilterTagIds = [];
    private readonly List<ITaggable> _subscribedItems = [];
    private bool _hasNoFilterTags = true;
    private INotifyCollectionChanged? _subscribedSource;

    public ObservableCollection<TagViewModel>? AllTags
    {
        get => GetValue(AllTagsProperty);
        set => SetValue(AllTagsProperty, value);
    }

    public ObservableCollection<TagFilterOptionViewModel> FilterOptions => _filterOptions;

    public IEnumerable FilteredItemsSource => _filteredItems;

    public bool HasNoFilterTags
    {
        get => _hasNoFilterTags;
        private set => SetAndRaise(HasNoFilterTagsProperty, ref _hasNoFilterTags, value);
    }

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(Description));

    public static readonly StyledProperty<string?> AddButtonLabelProperty =
        AvaloniaProperty.Register<LibraryPanelView, string?>(nameof(AddButtonLabel), LocalizationService.Get("LibraryPanel.AddButtonLabel"));

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
        AllTagsProperty.Changed.AddClassHandler<LibraryPanelView>((c, _) => c.RebuildFilterOptions());
        ItemsSourceProperty.Changed.AddClassHandler<LibraryPanelView>((c, _) => c.ResubscribeItemsAndFilter());
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        UnsubscribeAllItems();
    }

    private void RebuildFilterOptions()
    {
        _filterOptions.Clear();
        _selectedFilterTagIds.Clear();
        if (AllTags is not null)
            foreach (var tag in AllTags)
                _filterOptions.Add(new TagFilterOptionViewModel(tag, OnFilterTagToggled));

        HasNoFilterTags = _filterOptions.Count == 0;
        ApplyFilter();
    }

    private void OnFilterTagToggled(Guid tagId, bool isSelected)
    {
        if (isSelected) _selectedFilterTagIds.Add(tagId);
        else _selectedFilterTagIds.Remove(tagId);
        ApplyFilter();
    }

    private void ResubscribeItemsAndFilter()
    {
        UnsubscribeAllItems();

        if (ItemsSource is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnSourceCollectionChanged;
            _subscribedSource = incc;
        }

        if (ItemsSource is not null)
            foreach (var item in ItemsSource)
                if (item is ITaggable taggable)
                {
                    taggable.TagIds.CollectionChanged += OnItemTagIdsChanged;
                    _subscribedItems.Add(taggable);
                }

        ApplyFilter();
    }

    private void UnsubscribeAllItems()
    {
        foreach (var item in _subscribedItems) item.TagIds.CollectionChanged -= OnItemTagIdsChanged;
        _subscribedItems.Clear();

        if (_subscribedSource is not null) _subscribedSource.CollectionChanged -= OnSourceCollectionChanged;
        _subscribedSource = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ResubscribeItemsAndFilter();

    private void OnItemTagIdsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        _filteredItems.Clear();
        if (ItemsSource is null) return;

        foreach (var item in ItemsSource)
        {
            if (_selectedFilterTagIds.Count == 0 ||
                (item is ITaggable taggable && taggable.TagIds.Any(_selectedFilterTagIds.Contains)))
                _filteredItems.Add(item);
        }
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

    // File dialog configuration and the actual add/export/import processing come from the
    // caller (see MainWindow.axaml.cs, OnDataContextChanged) instead of being duplicated here
    // for each library - both differ only in the file type filter and the
    // view model method that actually processes the chosen path.
    public string AddDialogTitle { get; set; } = LocalizationService.Get("LibraryPanel.AddDialogTitle");
    public FilePickerFileType AddFileTypeFilter { get; set; } = new(LocalizationService.Get("LibraryPanel.AllFiles")) { Patterns = ["*.*"] };
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
                ErrorMessage = LocalizationService.Get("LibraryPanel.Errors.NoLocalFilePath");
                return;
            }

            await AddItemAsync(localPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = string.Format(LocalizationService.Get("LibraryPanel.Errors.FileCouldNotBeAdded"), ex.Message);
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