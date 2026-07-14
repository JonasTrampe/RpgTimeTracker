using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace RpgTimeTracker.Views.Controls;

/// <summary>Icon-button pair ("move to Shared" / "move to this Session") shared by the Media,
///     Sound, and Music library tiles - see MainWindowViewModel.MoveLibraryItemToScope's doc
///     comment for the ViewModel-side counterpart this binds to. Map and Characters use a
///     MenuItem pair instead (their per-item actions already live inside a MenuFlyout), so
///     those two stay inline in MainWindow.axaml rather than reusing this control.</summary>
public partial class MoveToScopeButtons : UserControl
{
    public static readonly StyledProperty<bool> IsSessionLocalProperty =
        AvaloniaProperty.Register<MoveToScopeButtons, bool>(nameof(IsSessionLocal));

    public static readonly StyledProperty<bool> IsSessionOpenProperty =
        AvaloniaProperty.Register<MoveToScopeButtons, bool>(nameof(IsSessionOpen));

    public static readonly StyledProperty<ICommand?> MoveToSharedCommandProperty =
        AvaloniaProperty.Register<MoveToScopeButtons, ICommand?>(nameof(MoveToSharedCommand));

    public static readonly StyledProperty<ICommand?> MoveToSessionCommandProperty =
        AvaloniaProperty.Register<MoveToScopeButtons, ICommand?>(nameof(MoveToSessionCommand));

    public static readonly StyledProperty<object?> ItemProperty =
        AvaloniaProperty.Register<MoveToScopeButtons, object?>(nameof(Item));

    public MoveToScopeButtons()
    {
        InitializeComponent();
    }

    public bool IsSessionLocal
    {
        get => GetValue(IsSessionLocalProperty);
        set => SetValue(IsSessionLocalProperty, value);
    }

    public bool IsSessionOpen
    {
        get => GetValue(IsSessionOpenProperty);
        set => SetValue(IsSessionOpenProperty, value);
    }

    public ICommand? MoveToSharedCommand
    {
        get => GetValue(MoveToSharedCommandProperty);
        set => SetValue(MoveToSharedCommandProperty, value);
    }

    public ICommand? MoveToSessionCommand
    {
        get => GetValue(MoveToSessionCommandProperty);
        set => SetValue(MoveToSessionCommandProperty, value);
    }

    public object? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
