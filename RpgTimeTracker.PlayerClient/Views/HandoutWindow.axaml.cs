using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RpgTimeTracker.PlayerClient.Views;

/// <summary>
///     Shows a GM-pushed Markdown handout (see RpcMethods.HandoutShow) - lazily created and
///     Show()/Hide()'d by ClientMainWindow, never recreated, same convention as MediaWindow.
/// </summary>
public partial class HandoutWindow : Window
{
    public HandoutWindow()
    {
        InitializeComponent();
    }

    // Local-only close, mirrors MediaWindow.OnCloseClick - the host's handout state is
    // untouched, so it reopens unchanged the next time handout.show is sent.
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Hide();
    }
}
