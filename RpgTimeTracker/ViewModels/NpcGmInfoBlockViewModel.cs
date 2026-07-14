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

    public NpcGmInfoBlockViewModel(
        string title,
        string markdownBody,
        Action<NpcGmInfoBlockViewModel> onDeleteRequested,
        Action<NpcGmInfoBlockViewModel>? onChanged)
    {
        _title = title;
        _markdownBody = markdownBody;
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

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}
