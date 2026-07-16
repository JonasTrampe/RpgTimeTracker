using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Side-panel companion to the initiative order (#70) - Add controls (Character/freeform),
///     Start/Stop + Next transport, and a drag-reorderable list of entries. Built the same way as
///     MapTokenPanelView (code-behind-built rows, Configure(vm, map) entry point) rather than
///     bound XAML, matching that control's own reasoning: per-row content varies enough (a gear
///     flyout for Character entries' Variant, none for freeform) that imperative construction is
///     simpler than fighting a DataTemplateSelector for it.
/// </summary>
public partial class InitiativeTrackerPanelView : UserControl
{
    private MainWindowViewModel? _vm;
    private MapItemViewModel? _map;

    // Drag-reorder state (see OnRowPointerPressed/Moved/Released) - only one row can be dragged
    // at a time, and the visual StackPanel children are moved directly (not via a full RenderRows
    // rebuild) so pointer capture on the dragged row survives the whole gesture.
    private InitiativeEntryViewModel? _draggingEntry;
    private Control? _draggingRow;
    private int _draggingIndex;
    private double _dragStartPointerY;
    private double _dragStartRowTop;
    private bool _suppressRenderRows;

    public InitiativeTrackerPanelView()
    {
        InitializeComponent();
    }

    public void Configure(MainWindowViewModel vm, MapItemViewModel map)
    {
        _vm = vm;
        _map = map;
        CharacterPicker.ItemsSource = vm.NpcLibrary;
        map.InitiativeEntries.CollectionChanged += OnEntriesCollectionChanged;
        RenderRows();
        RefreshTransport();
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressRenderRows) return;
        RenderRows();
    }

    private void OnAddCharacterEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_map is null || CharacterPicker.SelectedItem is not NpcLibraryItemViewModel npc) return;

        _map.AddInitiativeEntry(TokenLinkKind.Character, npc.Id, null, string.Empty,
            _map.RemoveInitiativeEntry, _ => _vm?.OnInitiativeEntryChanged(_map));
    }

    private void OnAddFreeformEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_map is null) return;

        var name = FreeformNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _map.AddInitiativeEntry(TokenLinkKind.None, null, null, name,
            _map.RemoveInitiativeEntry, _ => _vm?.OnInitiativeEntryChanged(_map));
        FreeformNameBox.Text = string.Empty;
    }

    private void OnStartStopClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _map is null) return;

        if (_map.InitiativeIsRunning) _vm.StopInitiative(_map);
        else _vm.StartInitiative(_map);

        RefreshTransport();
        RenderRows();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _map is null) return;

        _vm.AdvanceInitiative(_map);
        RefreshTransport();
        RenderRows();
    }

    private void RefreshTransport()
    {
        if (_map is null) return;

        StartStopButton.Content = LocalizationService.Get(_map.InitiativeIsRunning
            ? "MapEditor.InitiativeStopButton"
            : "MapEditor.InitiativeStartButton");
        NextButton.IsEnabled = _map.InitiativeIsRunning;
        RoundLabel.Text = _map.InitiativeIsRunning
            ? string.Format(LocalizationService.Get("MapEditor.InitiativeRoundLabel"), _map.InitiativeRound)
            : string.Empty;
    }

    private void RenderRows()
    {
        EntryList.Children.Clear();
        if (_map is null || _vm is null) return;

        EmptyStateLabel.IsVisible = _map.InitiativeEntries.Count == 0;
        for (var i = 0; i < _map.InitiativeEntries.Count; i++)
            EntryList.Children.Add(BuildRow(_map.InitiativeEntries[i], i));
    }

    private Control BuildRow(InitiativeEntryViewModel entry, int index)
    {
        var isCurrent = _map!.InitiativeIsRunning && index == _map.InitiativeCurrentIndex;
        var row = new Border
        {
            Classes = { "item" },
            Padding = new Thickness(6),
            BorderThickness = new Thickness(isCurrent ? 2 : 0),
            BorderBrush = Avalonia.Media.Brushes.Gold,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        var dragHandle = new TextBlock
        {
            Text = "☰", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(dragHandle, 0);

        var label = new TextBlock
        {
            Text = ResolveEntryName(entry), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);

        var statusLabel = new TextBlock
        {
            Classes = { "muted" }, Text = ResolveEntryStatusText(entry), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0)
        };
        Grid.SetColumn(statusLabel, 2);

        var deleteButton = new Button { Classes = { "iconsquare" }, Content = "✕", Width = 26, Height = 26 };
        deleteButton.Click += (_, _) =>
        {
            _map!.RemoveInitiativeEntry(entry);
            _vm!.OnInitiativeEntryChanged(_map);
        };
        Grid.SetColumn(deleteButton, 3);

        grid.Children.Add(dragHandle);
        grid.Children.Add(label);
        grid.Children.Add(statusLabel);
        grid.Children.Add(deleteButton);
        row.Child = grid;

        dragHandle.PointerPressed += (_, e) => OnRowPointerPressed(entry, row, e);
        row.PointerMoved += (_, e) => OnRowPointerMoved(row, e);
        row.PointerReleased += (_, e) => OnRowPointerReleased(e);

        return row;
    }

    private string ResolveEntryName(InitiativeEntryViewModel entry)
    {
        if (entry.LinkKind == TokenLinkKind.Character)
        {
            var npc = _vm!.NpcLibrary.FirstOrDefault(n => n.Id == entry.LinkedId);
            if (npc is not null) return npc.Name;
        }

        return entry.FreeformName;
    }

    /// <summary>Health/Status badge for a Character entry - mirrors what the GM's map token tooltip already shows.</summary>
    private string? ResolveEntryStatusText(InitiativeEntryViewModel entry)
    {
        if (entry.LinkKind != TokenLinkKind.Character) return null;

        var npc = _vm!.NpcLibrary.FirstOrDefault(n => n.Id == entry.LinkedId);
        var variant = npc?.Variants.FirstOrDefault(v => v.Id == entry.LinkedVariantId) ?? npc?.DefaultVariant;
        return variant?.Health;
    }

    /// <summary>
    ///     Only the drag-handle glyph starts a drag (not the whole row) so clicking the delete
    ///     button or the row itself doesn't accidentally begin reordering.
    /// </summary>
    private void OnRowPointerPressed(InitiativeEntryViewModel entry, Border row, PointerPressedEventArgs e)
    {
        if (_map is null || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;

        _draggingEntry = entry;
        _draggingRow = row;
        _draggingIndex = _map.InitiativeEntries.IndexOf(entry);
        _dragStartPointerY = e.GetPosition(EntryList).Y;
        _dragStartRowTop = row.Bounds.Top;
        e.Pointer.Capture(row);
        e.Handled = true;
    }

    private void OnRowPointerMoved(Control row, PointerEventArgs e)
    {
        if (_map is null || _draggingEntry is null || !ReferenceEquals(_draggingRow, row) ||
            !ReferenceEquals(e.Pointer.Captured, row))
            return;

        var deltaY = e.GetPosition(EntryList).Y - _dragStartPointerY;
        var rowHeight = row.Bounds.Height <= 0 ? 32 : row.Bounds.Height;
        var targetIndex = Math.Clamp((int)Math.Round((_dragStartRowTop + deltaY) / rowHeight), 0,
            _map.InitiativeEntries.Count - 1);
        if (targetIndex == _draggingIndex) return;

        _suppressRenderRows = true;
        _map.MoveInitiativeEntryTo(_draggingEntry, targetIndex);
        EntryList.Children.RemoveAt(_draggingIndex);
        EntryList.Children.Insert(targetIndex, row);
        _draggingIndex = targetIndex;
        e.Handled = true;
    }

    private void OnRowPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingRow is null) return;

        e.Pointer.Capture(null);
        _draggingEntry = null;
        _draggingRow = null;
        _suppressRenderRows = false;
        RenderRows();
        _vm?.OnInitiativeEntryChanged(_map!);
        e.Handled = true;
    }
}
