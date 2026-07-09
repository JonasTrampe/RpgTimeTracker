using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     A preselected image/video in the GM media library. Double-click shows it immediately
///     to the GM and all connected players (see MainWindowViewModel.ShowMediaLibraryItem).
/// </summary>
public partial class MediaLibraryItemViewModel : LibraryItemViewModelBase<MediaLibraryItemViewModel>
{
    private readonly Action<MediaLibraryItemViewModel> _onShowRequested;

    /// <summary>
    ///     Whether a sent video should automatically restart from the beginning at the end, instead of
    ///     being reported to the host and closed at the end.
    /// </summary>
    [ObservableProperty] private bool _loop;

    [ObservableProperty] private Bitmap? _thumbnail;

    public MediaLibraryItemViewModel(
        string name,
        string localPath,
        MediaKind kind,
        string mimeType,
        bool loop,
        Action<MediaLibraryItemViewModel> onDeleteRequested,
        Action<MediaLibraryItemViewModel> onShowRequested,
        Action<MediaLibraryItemViewModel>? onChanged = null)
        : base(name, localPath, mimeType, onDeleteRequested, onChanged)
    {
        Kind = kind;
        _loop = loop;
        _onShowRequested = onShowRequested;

        if (kind == MediaKind.Image) LoadThumbnail();
    }

    public MediaKind Kind { get; }

    public string KindLabel => Kind == MediaKind.Video
        ? LocalizationService.Get("Calendar.MediaKind.Video")
        : LocalizationService.Get("Calendar.MediaKind.Image");
    public bool IsVideo => Kind == MediaKind.Video;

    /// <summary>Only video can automatically restart from the beginning instead of notifying the host.</summary>
    public bool SupportsLoop => Kind == MediaKind.Video;

    /// <summary>Placeholder icon for videos without a thumbnail.</summary>
    public string FallbackIcon => "\U0001F3AC";

    public bool HasThumbnail => Thumbnail is not null;

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

    partial void OnLoopChanged(bool value)
    {
        NotifyChanged();
    }

    [RelayCommand]
    private void Show()
    {
        _onShowRequested(this);
    }
}