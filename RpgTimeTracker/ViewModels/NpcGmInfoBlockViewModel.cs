using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One GM-named, ordered markdown section on an NPC (see NpcLibraryItemViewModel.GmInfoBlocks) -
///     shared across all of that NPC's states, since these are private GM reference notes
///     (motivation, secrets, stat block) that don't change with mood.
/// </summary>
public partial class NpcGmInfoBlockViewModel : ObservableObject
{
    private readonly Action<NpcGmInfoBlockViewModel> _onDeleteRequested;
    private readonly Action<NpcGmInfoBlockViewModel>? _onChanged;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _markdownBody;

    /// <summary>Purely local UI state (not persisted) - whether this block currently shows its
    ///     rendered Markdown preview instead of the plain-text editor. See MainWindow.axaml's
    ///     per-field preview toggle button. Starts true (preview) whenever markdownBody already
    ///     has content to render, and false (edit) for a genuinely empty/new block - previewing
    ///     nothing would just look broken, and there'd be no obvious way to start typing.</summary>
    [ObservableProperty] private bool _isPreviewMode;

    public string PreviewToggleIcon => IsPreviewMode ? "✎" : "👁";

    public NpcGmInfoBlockViewModel(
        string title,
        string markdownBody,
        Action<NpcGmInfoBlockViewModel> onDeleteRequested,
        Action<NpcGmInfoBlockViewModel>? onChanged)
    {
        _title = title;
        _markdownBody = markdownBody;
        _isPreviewMode = !string.IsNullOrWhiteSpace(markdownBody);
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    partial void OnTitleChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnMarkdownBodyChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnIsPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewToggleIcon));
    }

    [RelayCommand]
    private void TogglePreview()
    {
        IsPreviewMode = !IsPreviewMode;
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}
