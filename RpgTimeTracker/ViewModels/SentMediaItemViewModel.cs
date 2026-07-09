using System;
using System.ComponentModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     An image/video sent to players that stays in the session's own gallery (not immediately
///     discarded by the next send) - the GM and each player can independently
///     navigate forward/backward through this list (see design-decisions.md). "Highlight" jumps
///     for all participants (including the GM themselves) to this item; "Remove" takes it out of the gallery.
///     Event-trigger media are deliberately NEVER part of this list (see MainWindowViewModel.SendTriggerMediaAsync).
/// </summary>
public partial class SentMediaItemViewModel : ObservableObject, IDisposable
{
    private readonly Action<SentMediaItemViewModel> _onHighlightRequested;
    private readonly Action<SentMediaItemViewModel> _onRetractRequested;
    private readonly MediaLibraryItemViewModel? _sourceItem;

    [ObservableProperty] private string _name;
    [ObservableProperty] private Bitmap? _thumbnail;

    public SentMediaItemViewModel(
        string mediaId,
        string name,
        MediaKind kind,
        string localPath,
        string mimeType,
        bool isOwnedCache,
        Action<SentMediaItemViewModel> onHighlightRequested,
        Action<SentMediaItemViewModel> onRetractRequested,
        MediaLibraryItemViewModel? sourceItem = null)
    {
        MediaId = mediaId;
        _name = name;
        Kind = kind;
        LocalPath = localPath;
        MimeType = mimeType;
        IsOwnedCache = isOwnedCache;
        _onHighlightRequested = onHighlightRequested;
        _onRetractRequested = onRetractRequested;
        _sourceItem = sourceItem;
        if (_sourceItem is not null)
            _sourceItem.PropertyChanged += OnSourceItemPropertyChanged;

        if (kind == MediaKind.Image) LoadThumbnail();
    }

    public string MediaId { get; }
    public MediaKind Kind { get; }
    public string LocalPath { get; }
    public string MimeType { get; }

    /// <summary>
    ///     Whether LocalPath is a one-off ad-hoc cache copy (to be deleted
    ///     when removed from the gallery) or a persistent library file (remains independent of the gallery).
    /// </summary>
    public bool IsOwnedCache { get; }

    public string KindLabel => Kind == MediaKind.Video ? "Video" : "Bild";
    public string FallbackIcon => "\U0001F3AC";
    public bool HasThumbnail => Thumbnail is not null;

    public void Dispose()
    {
        if (_sourceItem is not null)
            _sourceItem.PropertyChanged -= OnSourceItemPropertyChanged;
    }

    private void LoadThumbnail()
    {
        try
        {
            using var stream = File.OpenRead(LocalPath);
            Thumbnail = Bitmap.DecodeToWidth(stream, 96);
        }
        catch
        {
            Thumbnail = null;
        }
    }

    partial void OnThumbnailChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }

    [RelayCommand]
    private void Highlight()
    {
        _onHighlightRequested(this);
    }

    [RelayCommand]
    private void Retract()
    {
        _onRetractRequested(this);
    }

    private void OnSourceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_sourceItem is null) return;

        if (e.PropertyName == nameof(MediaLibraryItemViewModel.Name))
            Name = _sourceItem.Name;
    }
}