using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views;

public partial class IconPickerWindow : Window
{
    private readonly ObservableCollection<BootstrapIconChoice> _icons = [];
    private readonly Action<string>? _onIconSelected;
    private readonly TimelineDisplayItemViewModel? _target;

    /// <summary>Nur für den Avalonia-XAML-Loader (Previewer/AVLN3001); im App-Code stets mit target/callback konstruiert.</summary>
    public IconPickerWindow()
    {
        InitializeComponent();
        IconList.ItemsSource = _icons;
        RefreshIcons(string.Empty);
    }

    public IconPickerWindow(TimelineDisplayItemViewModel target)
    {
        _target = target;
        InitializeComponent();
        IconList.ItemsSource = _icons;
        RefreshIcons(string.Empty);
    }

    public IconPickerWindow(Action<string> onIconSelected)
    {
        _onIconSelected = onIconSelected;
        InitializeComponent();
        IconList.ItemsSource = _icons;
        RefreshIcons(string.Empty);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshIcons(SearchBox.Text ?? string.Empty);
    }

    private void OnIconDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IconList.SelectedItem is BootstrapIconChoice choice)
        {
            if (_target is not null)
                _target.Icon = choice.Name;
            else
                _onIconSelected?.Invoke(choice.Name);
            Close();
        }
    }

    private void RefreshIcons(string searchText)
    {
        _icons.Clear();
        foreach (var icon in VisualItemHelper.SearchIconChoices(searchText).Take(200)) _icons.Add(icon);

        if (_icons.Count > 0) IconList.SelectedIndex = 0;
    }
}