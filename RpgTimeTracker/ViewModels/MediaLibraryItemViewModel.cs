using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Ein vorausgewähltes Bild/Video in der SL-Medienbibliothek. Doppelklick zeigt es sofort
///     bei SL und allen verbundenen Spielern an (siehe MainWindowViewModel.ShowMediaLibraryItem).
/// </summary>
public partial class MediaLibraryItemViewModel : LibraryItemViewModelBase<MediaLibraryItemViewModel>
{
    private readonly Action<MediaLibraryItemViewModel> _onShowRequested;

    /// <summary>
    ///     Ob ein gesendetes Video am Ende automatisch von vorn beginnen soll, statt beim Ende dem Host gemeldet und
    ///     geschlossen zu werden.
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

    public string KindLabel => Kind == MediaKind.Video ? "Video" : "Bild";
    public bool IsVideo => Kind == MediaKind.Video;

    /// <summary>Nur Video kann am Ende automatisch von vorn beginnen statt den Host zu benachrichtigen.</summary>
    public bool SupportsLoop => Kind == MediaKind.Video;

    /// <summary>Platzhalter-Icon für Videos ohne Thumbnail.</summary>
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