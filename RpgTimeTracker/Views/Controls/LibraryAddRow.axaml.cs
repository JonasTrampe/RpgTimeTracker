using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     "Pick from a library, then Add" row (ComboBox + Add button) - the exact shape previously
///     repeated four times in the Scene editor's bundle section (Images/Maps/Playlists/Sounds),
///     each only differing in which library/pending-item/add-command it points at. The item rows
///     themselves (with their per-kind Send/Stop/Remove buttons) are NOT covered here - those
///     differ too much in per-kind commands and visibility converters to genericize safely.
/// </summary>
public partial class LibraryAddRow : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<LibraryAddRow, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<LibraryAddRow, object?>(nameof(SelectedItem));

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<LibraryAddRow, string?>(nameof(PlaceholderText));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<LibraryAddRow, ICommand?>(nameof(AddCommand));

    public LibraryAddRow()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }
}
