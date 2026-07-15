using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Side-panel companion to MapEditCanvasControl's token overlay (#32) - Add buttons (freeform/
///     Character/PointOfInterest) and a list of every token on this Map (all floors, not just the
///     one currently shown - the canvas overlay already filters by floor; this list is the one
///     place a GM can find/edit a token that's on a different floor right now, e.g. to change
///     "move to floor" back). Each row's gear button opens a full settings flyout built in code
///     (not XAML data templates), since the visible fields genuinely change shape based on
///     TokenLinkKind (freeform vs. Character vs. PointOfInterest) - simpler to show/hide sections
///     imperatively than to fight DataTemplateSelector for something this dynamic.
/// </summary>
public partial class MapTokenPanelView : UserControl
{
    private MainWindowViewModel? _vm;
    private MapItemViewModel? _map;
    private MapTokenViewModel? _selectedToken;

    public MapTokenPanelView()
    {
        InitializeComponent();
    }

    /// <summary>Fired after any panel-driven token mutation - the owner window forwards this to EditCanvas.RefreshTokens().</summary>
    public event Action? TokensMutated;

    /// <summary>
    ///     Set by the owner (via MapEditCanvasControl.TokenSelected) when a canvas marker is
    ///     clicked - highlights the matching row so the GM can find it in this list without
    ///     hunting.
    /// </summary>
    public MapTokenViewModel? SelectedToken
    {
        get => _selectedToken;
        set
        {
            _selectedToken = value;
            RenderRows();
        }
    }

    public void Configure(MainWindowViewModel vm, MapItemViewModel map)
    {
        _vm = vm;
        _map = map;
        CharacterPicker.ItemsSource = vm.NpcLibrary;
        PoiPicker.ItemsSource = vm.PointOfInterestLibrary;
        _map.Tokens.CollectionChanged += OnTokensCollectionChanged;
        RenderRows();
    }

    private void OnTokensCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderRows();
    }

    private void OnAddFreeformClick(object? sender, RoutedEventArgs e)
    {
        AddToken(TokenLinkKind.None, null);
    }

    private void OnAddCharacterTokenClick(object? sender, RoutedEventArgs e)
    {
        if (CharacterPicker.SelectedItem is NpcLibraryItemViewModel npc) AddToken(TokenLinkKind.Character, npc.Id);
    }

    private void OnAddPoiTokenClick(object? sender, RoutedEventArgs e)
    {
        if (PoiPicker.SelectedItem is PointOfInterestLibraryItemViewModel poi)
            AddToken(TokenLinkKind.PointOfInterest, poi.Id);
    }

    /// <summary>
    ///     New tokens default to the middle of the current floor's image (a sensible starting
    ///     point the GM immediately drags into place) - falls back to (0,0) if no floor is loaded
    ///     yet (nothing to center on).
    /// </summary>
    private void AddToken(TokenLinkKind linkKind, Guid? linkedId)
    {
        if (_map is null || _vm is null) return;

        var floorId = _map.Floors.FirstOrDefault()?.Id ?? Guid.Empty;
        _map.AddOrSelectToken(linkKind, linkedId, floorId, 0, 0, _map.RemoveToken,
            _ => _vm.NotifyMapTokensChanged(_map));
        TokensMutated?.Invoke();
    }

    private void RenderRows()
    {
        TokenList.Children.Clear();
        if (_map is null || _vm is null) return;

        EmptyStateLabel.IsVisible = _map.Tokens.Count == 0;
        foreach (var token in _map.Tokens) TokenList.Children.Add(BuildRow(token));
    }

    private Control BuildRow(MapTokenViewModel token)
    {
        var isSelected = ReferenceEquals(token, SelectedToken);
        var row = new Border
        {
            Classes = { "item" },
            Padding = new Avalonia.Thickness(6),
            BorderThickness = new Avalonia.Thickness(isSelected ? 2 : 0),
            BorderBrush = Avalonia.Media.Brushes.Orange
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var label = new TextBlock
        {
            Text = _vm!.ResolveTokenName(token), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);

        var gearButton = new Button { Classes = { "iconsquare" }, Content = "⚙", Width = 26, Height = 26 };
        var flyout = new Flyout { Content = BuildTokenSettingsContent(token), Placement = PlacementMode.Bottom };
        gearButton.Flyout = flyout;
        Grid.SetColumn(gearButton, 1);

        var deleteButton = new Button
        {
            Classes = { "iconsquare" }, Content = "✕", Width = 26, Height = 26,
            Command = token.DeleteCommand
        };
        Grid.SetColumn(deleteButton, 2);

        grid.Children.Add(label);
        grid.Children.Add(gearButton);
        grid.Children.Add(deleteButton);
        row.Child = grid;
        return row;
    }

    private Control BuildTokenSettingsContent(MapTokenViewModel token)
    {
        var panel = new StackPanel { Spacing = 8, Width = 260 };

        var linkKindCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<TokenLinkKind>(), SelectedItem = token.LinkKind,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(new TextBlock
            { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.LinkKindLabel") });
        panel.Children.Add(linkKindCombo);

        var freeformPanel = BuildFreeformSection(token);
        var characterPanel = BuildCharacterSection(token);
        var poiPanel = BuildPoiSection(token);
        panel.Children.Add(freeformPanel);
        panel.Children.Add(characterPanel);
        panel.Children.Add(poiPanel);

        void UpdateSectionVisibility(TokenLinkKind kind)
        {
            freeformPanel.IsVisible = kind == TokenLinkKind.None;
            characterPanel.IsVisible = kind == TokenLinkKind.Character;
            poiPanel.IsVisible = kind == TokenLinkKind.PointOfInterest;
        }

        UpdateSectionVisibility(token.LinkKind);
        linkKindCombo.SelectionChanged += (_, _) =>
        {
            if (linkKindCombo.SelectedItem is not TokenLinkKind kind) return;
            token.LinkKind = kind;
            if (kind != TokenLinkKind.Character) token.LinkedVariantId = null;
            if (kind == TokenLinkKind.None) token.LinkedId = null;
            UpdateSectionVisibility(kind);
            NotifyMutated();
        };

        var revealModeCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<TokenRevealMode>(), SelectedItem = token.RevealMode,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        revealModeCombo.SelectionChanged += (_, _) =>
        {
            if (revealModeCombo.SelectedItem is TokenRevealMode mode)
            {
                token.RevealMode = mode;
                NotifyMutated();
            }
        };
        panel.Children.Add(new TextBlock
            { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.RevealModeLabel") });
        panel.Children.Add(revealModeCombo);

        var nameVisibleCheck = new CheckBox
            { Content = LocalizationService.Get("MapEditor.PlayerVisibleNameLabel"), IsChecked = token.PlayerVisibleName };
        nameVisibleCheck.IsCheckedChanged += (_, _) =>
        {
            token.PlayerVisibleName = nameVisibleCheck.IsChecked == true;
            NotifyMutated();
        };
        var portraitVisibleCheck = new CheckBox
        {
            Content = LocalizationService.Get("MapEditor.PlayerVisiblePortraitLabel"),
            IsChecked = token.PlayerVisiblePortrait
        };
        portraitVisibleCheck.IsCheckedChanged += (_, _) =>
        {
            token.PlayerVisiblePortrait = portraitVisibleCheck.IsChecked == true;
            NotifyMutated();
        };
        var detailVisibleCheck = new CheckBox
        {
            Content = LocalizationService.Get("MapEditor.PlayerVisibleDetailLabel"), IsChecked = token.PlayerVisibleDetail
        };
        detailVisibleCheck.IsCheckedChanged += (_, _) =>
        {
            token.PlayerVisibleDetail = detailVisibleCheck.IsChecked == true;
            NotifyMutated();
        };
        panel.Children.Add(nameVisibleCheck);
        panel.Children.Add(portraitVisibleCheck);
        panel.Children.Add(detailVisibleCheck);

        var floorCombo = new ComboBox { ItemsSource = _map!.Floors, HorizontalAlignment = HorizontalAlignment.Stretch };
        floorCombo.SelectedItem = _map.Floors.FirstOrDefault(f => f.Id == token.FloorId);
        floorCombo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MapFloorItemViewModel>(
            (floor, _) => new TextBlock { Text = floor?.Name });
        floorCombo.SelectionChanged += (_, _) =>
        {
            if (floorCombo.SelectedItem is MapFloorItemViewModel floor)
            {
                token.FloorId = floor.Id;
                NotifyMutated();
            }
        };
        panel.Children.Add(new TextBlock
            { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.MoveToFloorLabel") });
        panel.Children.Add(floorCombo);

        return panel;

        void NotifyMutated()
        {
            _vm?.NotifyMapTokensChanged(_map!);
            TokensMutated?.Invoke();
            RenderRows();
        }
    }

    private StackPanel BuildFreeformSection(MapTokenViewModel token)
    {
        var panel = new StackPanel { Spacing = 6 };
        var nameBox = new TextBox
            { Text = token.Name, PlaceholderText = LocalizationService.Get("MapEditor.TokenNameWatermark") };
        nameBox.TextChanged += (_, _) =>
        {
            token.Name = nameBox.Text ?? string.Empty;
            _vm?.NotifyMapTokensChanged(_map!);
        };
        var descriptionBox = new TextBox
        {
            Text = token.Description, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 50
        };
        descriptionBox.TextChanged += (_, _) =>
        {
            token.Description = descriptionBox.Text ?? string.Empty;
            _vm?.NotifyMapTokensChanged(_map!);
        };
        panel.Children.Add(new TextBlock { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.TokenNameLabel") });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock
            { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.TokenDescriptionLabel") });
        panel.Children.Add(descriptionBox);
        return panel;
    }

    private StackPanel BuildCharacterSection(MapTokenViewModel token)
    {
        var panel = new StackPanel { Spacing = 6 };
        var npcCombo = new ComboBox
        {
            ItemsSource = _vm!.NpcLibrary, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = _vm.NpcLibrary.FirstOrDefault(n => n.Id == token.LinkedId)
        };
        npcCombo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<NpcLibraryItemViewModel>(
            (npc, _) => new TextBlock { Text = npc?.Name });

        var variantCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        variantCombo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<NpcVariantViewModel>(
            (v, _) => new TextBlock { Text = v?.Name });

        void RefreshVariants(NpcLibraryItemViewModel? npc)
        {
            variantCombo.ItemsSource = npc?.Variants;
            variantCombo.SelectedItem = npc?.Variants.FirstOrDefault(v => v.Id == token.LinkedVariantId)
                                         ?? npc?.DefaultVariant;
        }

        RefreshVariants(npcCombo.SelectedItem as NpcLibraryItemViewModel);

        npcCombo.SelectionChanged += (_, _) =>
        {
            var npc = npcCombo.SelectedItem as NpcLibraryItemViewModel;
            token.LinkedId = npc?.Id;
            RefreshVariants(npc);
            token.LinkedVariantId = (variantCombo.SelectedItem as NpcVariantViewModel)?.Id;
            _vm.NotifyMapTokensChanged(_map!);
            TokensMutated?.Invoke();
            RenderRows();
        };
        variantCombo.SelectionChanged += (_, _) =>
        {
            token.LinkedVariantId = (variantCombo.SelectedItem as NpcVariantViewModel)?.Id;
            _vm.NotifyMapTokensChanged(_map!);
        };

        panel.Children.Add(new TextBlock { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.CharacterLabel") });
        panel.Children.Add(npcCombo);
        panel.Children.Add(new TextBlock { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.VariantLabel") });
        panel.Children.Add(variantCombo);
        return panel;
    }

    private StackPanel BuildPoiSection(MapTokenViewModel token)
    {
        var panel = new StackPanel { Spacing = 6 };
        var poiCombo = new ComboBox
        {
            ItemsSource = _vm!.PointOfInterestLibrary, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = _vm.PointOfInterestLibrary.FirstOrDefault(p => p.Id == token.LinkedId)
        };
        poiCombo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PointOfInterestLibraryItemViewModel>(
            (poi, _) => new TextBlock { Text = poi?.Name });
        poiCombo.SelectionChanged += (_, _) =>
        {
            token.LinkedId = (poiCombo.SelectedItem as PointOfInterestLibraryItemViewModel)?.Id;
            _vm.NotifyMapTokensChanged(_map!);
            TokensMutated?.Invoke();
            RenderRows();
        };
        panel.Children.Add(new TextBlock
            { Classes = { "muted" }, Text = LocalizationService.Get("MapEditor.PointOfInterestLabel") });
        panel.Children.Add(poiCombo);
        return panel;
    }
}
