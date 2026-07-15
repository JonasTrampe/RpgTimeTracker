using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class SoundLibraryPickerWindow : Window
{
    private readonly Action<SoundLibraryItemViewModel>? _onSelected;

    /// <summary>Only for the Avalonia XAML loader (Previewer/AVLN3001); in app code always constructed with library/callback.</summary>
    public SoundLibraryPickerWindow()
    {
        InitializeComponent();
    }

    public SoundLibraryPickerWindow(IEnumerable<SoundLibraryItemViewModel> library,
        Action<SoundLibraryItemViewModel> onSelected)
    {
        _onSelected = onSelected;
        InitializeComponent();

        var items = library.ToList();
        SoundList.ItemsSource = items;
        EmptyText.IsVisible = items.Count == 0;
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SoundList.SelectedItem is SoundLibraryItemViewModel item)
        {
            _onSelected?.Invoke(item);
            Close();
        }
    }
}