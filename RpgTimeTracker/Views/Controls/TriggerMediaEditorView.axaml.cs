using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Reusable "event medium" (image/video, chosen from the Media Library) editor - the same
///     choose/filename/clear row + fullscreen/pause-clock checkboxes previously duplicated four
///     times (new-Timer/new-Alarm/new-Interval "Element Anlegen" forms and the Elementliste's
///     per-item edit panel), each only differing in the description text above it and which
///     <see cref="TriggerMediaConfig"/> instance it edits (the control's own DataContext).
/// </summary>
public partial class TriggerMediaEditorView : UserControl
{
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<TriggerMediaEditorView, string?>(nameof(Description));

    public static readonly StyledProperty<IEnumerable<MediaLibraryItemViewModel>?> MediaLibraryProperty =
        AvaloniaProperty.Register<TriggerMediaEditorView, IEnumerable<MediaLibraryItemViewModel>?>(nameof(MediaLibrary));

    public TriggerMediaEditorView()
    {
        InitializeComponent();
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public IEnumerable<MediaLibraryItemViewModel>? MediaLibrary
    {
        get => GetValue(MediaLibraryProperty);
        set => SetValue(MediaLibraryProperty, value);
    }

    /// <summary>Image/video for an event medium comes exclusively from the Media Library (no
    ///     file dialog) - this ensures every event medium image/video also exists as a library
    ///     entry, so the delete warning (RemoveMediaLibraryItem) can reliably detect it.</summary>
    private void OnChooseFromLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TriggerMediaConfig target || MediaLibrary is null) return;

        var picker = new MediaLibraryPickerWindow(MediaLibrary, item =>
        {
            target.Path = item.LocalPath;
            target.FileName = item.Name;
            target.Kind = item.Kind;
            target.Loop = item.Loop;
        });
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null) picker.Show(owner);
        else picker.Show();
    }
}
