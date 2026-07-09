using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

public partial class NewItemBasicsEditor : UserControl
{
    public NewItemBasicsEditor()
    {
        InitializeComponent();
    }

    private void OnChooseIconClick(object? sender, RoutedEventArgs e)
    {
        OpenIconPicker();
    }

    private void OnIconDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenIconPicker();
    }

    private void OpenIconPicker()
    {
        if (DataContext is not NewItemBasicsViewModel vm) return;

        var picker = new IconPickerWindow(icon => vm.Icon = icon);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null) picker.Show(owner);
        else picker.Show();
    }
}