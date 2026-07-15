using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Services.Visuals;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Editable wrapper around a Tag - see Tag's doc comment for why tags are a flat,
///     campaign-wide list rather than Shared-vs-SessionLocal like the libraries they tag.
/// </summary>
public partial class TagViewModel : ObservableObject
{
    private readonly Action<TagViewModel> _onDeleteRequested;
    private readonly Action<TagViewModel>? _onChanged;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _colorHex;

    public TagViewModel(Guid id, string name, string colorHex, Action<TagViewModel> onDeleteRequested,
        Action<TagViewModel>? onChanged = null)
    {
        Id = id;
        _name = name;
        _colorHex = colorHex;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public Guid Id { get; }

    public IBrush? ColorBrush => VisualItemHelper.TryBrush(ColorHex);

    partial void OnNameChanged(string value)
    {
        _onChanged?.Invoke(this);
    }

    partial void OnColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(ColorBrush));
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested(this);
    }

    public Tag ToModel() => new() { Id = Id, Name = Name, ColorHex = ColorHex };

    public static TagViewModel FromModel(Tag tag, Action<TagViewModel> onDeleteRequested,
        Action<TagViewModel>? onChanged = null) =>
        new(tag.Id, tag.Name, tag.ColorHex, onDeleteRequested, onChanged);
}
