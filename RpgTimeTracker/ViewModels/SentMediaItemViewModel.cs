using System;
using System.ComponentModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Ein an Spieler gesendetes Bild/Video, das in der session-eigenen Galerie bleibt (nicht sofort
///     durch das nächste Senden verworfen) - SL und jeder Spieler können unabhängig voneinander
///     vor/zurück durch diese Liste navigieren (siehe design-decisions.md). "Hervorheben" springt bei
///     allen Beteiligten (auch dem SL selbst) auf dieses Element; "Entfernen" nimmt es aus der Galerie.
///     Event-Trigger-Medien sind bewusst NIE Teil dieser Liste (siehe MainWindowViewModel.SendTriggerMediaAsync).
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
    ///     Ob LocalPath eine Einweg-Ad-hoc-Cache-Kopie ist (beim Entfernen aus der Galerie zu
    ///     löschen) oder eine persistente Bibliotheksdatei (bleibt unabhängig von der Galerie bestehen).
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