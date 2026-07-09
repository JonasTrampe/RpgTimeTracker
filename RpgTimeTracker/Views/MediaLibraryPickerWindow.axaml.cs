using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class MediaLibraryPickerWindow : Window
{
    private readonly Action<MediaLibraryItemViewModel>? _onSelected;

    /// <summary>Nur für den Avalonia-XAML-Loader (Previewer/AVLN3001); im App-Code stets mit Bibliothek/Callback konstruiert.</summary>
    public MediaLibraryPickerWindow()
    {
        InitializeComponent();
    }

    public MediaLibraryPickerWindow(IEnumerable<MediaLibraryItemViewModel> library,
        Action<MediaLibraryItemViewModel> onSelected)
    {
        _onSelected = onSelected;
        InitializeComponent();

        var items = library.ToList();
        MediaList.ItemsSource = items;
        EmptyText.IsVisible = items.Count == 0;
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (MediaList.SelectedItem is MediaLibraryItemViewModel item)
        {
            _onSelected?.Invoke(item);
            Close();
        }
    }
}
