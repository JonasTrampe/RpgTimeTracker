using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     One slot in a Map's initiative order (#70) - a Character (optionally a specific Variant,
///     for Health/Status/portrait lookup) or a freeform name with no library link at all (e.g. a
///     quick unnamed mook). The same Character can appear more than once for a participant with
///     multiple turns per round - there's no separate "turns per round" field, the GM just adds
///     them again. Reuses TokenLinkKind (#31) restricted to Character/None; a Point of Interest
///     can't take a turn.
/// </summary>
public sealed partial class InitiativeEntryViewModel : ObservableObject
{
    private readonly Action<InitiativeEntryViewModel> _onDeleteRequested;
    private readonly Action<InitiativeEntryViewModel>? _onChanged;

    [ObservableProperty] private string _freeformName;
    [ObservableProperty] private TokenLinkKind _linkKind;
    [ObservableProperty] private Guid? _linkedId;
    [ObservableProperty] private Guid? _linkedVariantId;

    public InitiativeEntryViewModel(
        Guid id,
        TokenLinkKind linkKind,
        Guid? linkedId,
        Guid? linkedVariantId,
        string freeformName,
        Action<InitiativeEntryViewModel> onDeleteRequested,
        Action<InitiativeEntryViewModel>? onChanged)
    {
        Id = id;
        _linkKind = linkKind;
        _linkedId = linkedId;
        _linkedVariantId = linkedVariantId;
        _freeformName = freeformName;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public Guid Id { get; }

    partial void OnLinkKindChanged(TokenLinkKind value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnLinkedIdChanged(Guid? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnLinkedVariantIdChanged(Guid? value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnFreeformNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }
}
