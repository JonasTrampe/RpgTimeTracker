using System;
using System.ComponentModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services.Localization;

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
        SourceItem = sourceItem;
        if (SourceItem is not null)
            SourceItem.PropertyChanged += OnSourceItemPropertyChanged;

        if (kind == MediaKind.Image) LoadThumbnail();
    }

    public string MediaId { get; }
    public MediaKind Kind { get; }
    public string LocalPath { get; }
    public string MimeType { get; }

    /// <summary>
    ///     The Media Library item this gallery entry was sent from, if any (ad-hoc sends
    ///     have none) - lets MainWindowViewModel find and retract every gallery entry that
    ///     originated from a given library item (see StopShowingImage), the same "stop" gesture
    ///     Maps/Playlists already have.
    /// </summary>
    public MediaLibraryItemViewModel? SourceItem { get; }

    /// <summary>
    ///     Whether LocalPath is a one-off ad-hoc cache copy (to be deleted
    ///     when removed from the gallery) or a persistent library file (remains independent of the gallery).
    /// </summary>
    public bool IsOwnedCache { get; }

    public string KindLabel => Kind == MediaKind.Video
        ? LocalizationService.Get("Calendar.MediaKind.Video")
        : LocalizationService.Get("Calendar.MediaKind.Image");

    public string FallbackIcon => "\U0001F3AC";
    public bool HasThumbnail => Thumbnail is not null;

    public void Dispose()
    {
        if (SourceItem is not null)
            SourceItem.PropertyChanged -= OnSourceItemPropertyChanged;
    }

    /// <summary>
    ///     Called by MainWindowViewModel on LocalizationService.LanguageChanged (see
    ///     LibraryItemViewModelBase.RefreshLocalizedText for the same pattern).
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
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
        if (SourceItem is null) return;

        if (e.PropertyName == nameof(MediaLibraryItemViewModel.Name))
            Name = SourceItem.Name;
    }
}