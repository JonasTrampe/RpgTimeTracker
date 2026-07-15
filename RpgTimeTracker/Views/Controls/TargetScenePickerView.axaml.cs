using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     "Activate Scene on fire" picker (Phase 4 of the Scenes/Tags/Calendars project) - a Guid?
///     ComboBox (matched against SceneLibrary by Id, since a bound Guid? can cross into the
///     Shared project's CalendarEntryViewModel, unlike a direct SceneLibraryItemViewModel
///     reference) plus a clear button. Previously duplicated across four editors (new-Timer/
///     new-Alarm/new-Interval creation forms, the Elementliste's per-item edit panel, and the
///     Calendar entry editor), each with its own Clear*TargetScene command; the clear button here
///     just sets the bound property directly, so none of those per-VM commands are needed anymore.
/// </summary>
public partial class TargetScenePickerView : UserControl
{
    public static readonly StyledProperty<ObservableCollection<SceneLibraryItemViewModel>?> SceneLibraryProperty =
        AvaloniaProperty.Register<TargetScenePickerView, ObservableCollection<SceneLibraryItemViewModel>?>(nameof(SceneLibrary));

    public static readonly StyledProperty<Guid?> TargetSceneIdProperty =
        AvaloniaProperty.Register<TargetScenePickerView, Guid?>(nameof(TargetSceneId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public TargetScenePickerView()
    {
        InitializeComponent();
    }

    public ObservableCollection<SceneLibraryItemViewModel>? SceneLibrary
    {
        get => GetValue(SceneLibraryProperty);
        set => SetValue(SceneLibraryProperty, value);
    }

    public Guid? TargetSceneId
    {
        get => GetValue(TargetSceneIdProperty);
        set => SetValue(TargetSceneIdProperty, value);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        TargetSceneId = null;
    }
}
