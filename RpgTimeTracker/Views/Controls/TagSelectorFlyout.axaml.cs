using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Reusable "assign tags to this item" flyout content, embedded in each library item's
///     settings Flyout (Media/Sound/Music/Map/NPC). Bridges the campaign-wide <see cref="AllTags"/>
///     list against a single item's <see cref="AssignedTagIds"/> collection via a per-open
///     <see cref="TagOptionViewModel"/> list, so toggling a CheckBox directly Adds/Removes the
///     tag's Id in the bound item - no two-way Guid-collection binding needed.
/// </summary>
public partial class TagSelectorFlyout : UserControl
{
    public static readonly StyledProperty<ObservableCollection<TagViewModel>?> AllTagsProperty =
        AvaloniaProperty.Register<TagSelectorFlyout, ObservableCollection<TagViewModel>?>(nameof(AllTags));

    public static readonly StyledProperty<ObservableCollection<System.Guid>?> AssignedTagIdsProperty =
        AvaloniaProperty.Register<TagSelectorFlyout, ObservableCollection<System.Guid>?>(nameof(AssignedTagIds));

    /// <summary>Creates a new campaign-wide tag by name (see MainWindowViewModel.CreateTag) - lets
    ///     the GM define a tag right here instead of having to go to Settings first. The new tag
    ///     shows up unassigned in Options (via AllTags' CollectionChanged) once created; the GM
    ///     still has to check its box to assign it to this item.</summary>
    public static readonly StyledProperty<ICommand?> CreateTagCommandProperty =
        AvaloniaProperty.Register<TagSelectorFlyout, ICommand?>(nameof(CreateTagCommand));

    public static readonly DirectProperty<TagSelectorFlyout, ObservableCollection<TagOptionViewModel>> OptionsProperty =
        AvaloniaProperty.RegisterDirect<TagSelectorFlyout, ObservableCollection<TagOptionViewModel>>(
            nameof(Options), o => o.Options);

    public static readonly DirectProperty<TagSelectorFlyout, bool> HasNoTagsProperty =
        AvaloniaProperty.RegisterDirect<TagSelectorFlyout, bool>(nameof(HasNoTags), o => o.HasNoTags);

    private readonly ObservableCollection<TagOptionViewModel> _options = [];
    private bool _hasNoTags = true;

    public TagSelectorFlyout()
    {
        InitializeComponent();
        AllTagsProperty.Changed.AddClassHandler<TagSelectorFlyout>((c, _) => c.RebuildOptions());
        AssignedTagIdsProperty.Changed.AddClassHandler<TagSelectorFlyout>((c, _) => c.RebuildOptions());
    }

    public ObservableCollection<TagViewModel>? AllTags
    {
        get => GetValue(AllTagsProperty);
        set => SetValue(AllTagsProperty, value);
    }

    public ObservableCollection<System.Guid>? AssignedTagIds
    {
        get => GetValue(AssignedTagIdsProperty);
        set => SetValue(AssignedTagIdsProperty, value);
    }

    public ICommand? CreateTagCommand
    {
        get => GetValue(CreateTagCommandProperty);
        set => SetValue(CreateTagCommandProperty, value);
    }

    public ObservableCollection<TagOptionViewModel> Options => _options;

    public bool HasNoTags
    {
        get => _hasNoTags;
        private set => SetAndRaise(HasNoTagsProperty, ref _hasNoTags, value);
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RebuildOptions();
        if (AllTags is INotifyCollectionChanged incc) incc.CollectionChanged += OnAllTagsCollectionChanged;
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (AllTags is INotifyCollectionChanged incc) incc.CollectionChanged -= OnAllTagsCollectionChanged;
    }

    private void OnAllTagsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOptions();

    private void RebuildOptions()
    {
        _options.Clear();
        var allTags = AllTags;
        var assignedTagIds = AssignedTagIds;
        if (allTags is null || assignedTagIds is null) return;

        foreach (var tag in allTags)
            _options.Add(new TagOptionViewModel(tag, assignedTagIds.Contains(tag.Id), (tagId, isAssigned) =>
            {
                if (isAssigned) { if (!assignedTagIds.Contains(tagId)) assignedTagIds.Add(tagId); }
                else assignedTagIds.Remove(tagId);
            }));

        HasNoTags = _options.Count == 0;
    }

    private void OnCreateTagClick(object? sender, RoutedEventArgs e) => SubmitNewTag();

    private void OnNewTagNameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) SubmitNewTag();
    }

    private void SubmitNewTag()
    {
        var textBox = this.FindControl<TextBox>("NewTagNameBox");
        var name = textBox?.Text;
        if (string.IsNullOrWhiteSpace(name) || CreateTagCommand is null) return;

        CreateTagCommand.Execute(name);
        if (textBox is not null) textBox.Text = string.Empty;
    }
}
