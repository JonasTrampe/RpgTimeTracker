using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RpgTimeTracker.ViewModels;

/// <summary>
///     Gemeinsame Basis für MediaLibraryItemViewModel und SoundLibraryItemViewModel: beide sind
///     ein benanntes, lokal gespeichertes Dateisystem-Element mit Umbenennen/Löschen/Änderungs-
///     benachrichtigung - alles darüber hinaus (Thumbnail/Kind/Loop bei Medien; Lautstärke/
///     Wiederholungen/Trim bei Sounds) bleibt bewusst in den jeweiligen abgeleiteten Klassen, da es
///     sich inhaltlich zu stark unterscheidet, um sich sinnvoll zu teilen.
///     CRTP (TSelf = die konkrete abgeleitete Klasse) statt eines nicht-generischen
///     Action&lt;LibraryItemViewModelBase&gt;, damit die bestehenden, konkret typisierten
///     onDeleteRequested/onChanged-Callback-Methoden in MainWindowViewModel (z.B.
///     RemoveMediaLibraryItem(MediaLibraryItemViewModel)) unverändert per Method-Group weiterhin
///     als Konstruktor-Argument passen.
/// </summary>
public abstract partial class LibraryItemViewModelBase<TSelf> : ObservableObject
    where TSelf : LibraryItemViewModelBase<TSelf>
{
    private readonly Action<TSelf>? _onChanged;
    private readonly Action<TSelf> _onDeleteRequested;

    [ObservableProperty] private string _name;

    protected LibraryItemViewModelBase(
        string name,
        string localPath,
        string mimeType,
        Action<TSelf> onDeleteRequested,
        Action<TSelf>? onChanged)
    {
        _name = name;
        LocalPath = localPath;
        MimeType = mimeType;
        _onDeleteRequested = onDeleteRequested;
        _onChanged = onChanged;
    }

    public string LocalPath { get; }
    public string MimeType { get; }

    partial void OnNameChanged(string value)
    {
        NotifyChanged();
    }

    /// <summary>
    ///     Für abgeleitete Klassen: eigene änderungsauslösende Properties (z.B. Loop,
    ///     Volume, Trim) rufen das ebenfalls über diese Methode auf, da _onChanged hier gekapselt bleibt.
    /// </summary>
    protected void NotifyChanged()
    {
        _onChanged?.Invoke((TSelf)this);
    }

    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested((TSelf)this);
    }
}