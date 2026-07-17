using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A Markdown-authored text handout the GM can push to players, shown in its own overlay
///     window - deliberately outside the image/video gallery and map flows (see
///     RpcMethods.HandoutShow). Kept as a single in-memory draft rather than a saved library
///     (no persistence across app restarts yet) - simplest useful version; a reusable library
///     of handouts is a natural follow-up if this sees regular use.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private string _handoutTitle = string.Empty;

    [ObservableProperty] private string _handoutMarkdown = string.Empty;

    /// <summary>Mirrors PointOfInterestLibraryItemViewModel.IsPlayerInfoPreviewMode - raw Markdown TextBox vs. rendered preview.</summary>
    [ObservableProperty] private bool _isHandoutPreviewMode;

    [ObservableProperty] private bool _isHandoutShown;

    public string HandoutPreviewToggleIcon => IsHandoutPreviewMode ? "✎" : "👁";

    partial void OnIsHandoutPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(HandoutPreviewToggleIcon));
    }

    [RelayCommand]
    private void ToggleHandoutPreview()
    {
        IsHandoutPreviewMode = !IsHandoutPreviewMode;
    }

    [RelayCommand]
    private async Task SendHandoutAsync()
    {
        await _playerServer.PublishHandoutShowAsync(HandoutTitle, HandoutMarkdown);
        IsHandoutShown = true;
    }

    [RelayCommand]
    private async Task RetractHandoutAsync()
    {
        await _playerServer.PublishHandoutHideAsync();
        IsHandoutShown = false;
    }
}
