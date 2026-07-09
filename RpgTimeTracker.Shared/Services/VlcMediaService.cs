using System;
using System.Globalization;
using LibVLCSharp.Shared;
using Serilog;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Verwaltet die eine LibVLC-Instanz des Prozesses. Unter Windows wird die native
///     Bibliothek über das NuGet-Paket VideoLAN.LibVLC.Windows mitgeliefert; unter Linux muss
///     VLC systemweit installiert sein (z.B. "sudo apt install vlc" / "sudo dnf install vlc").
/// </summary>
public static class VlcMediaService
{
    private static LibVLC? _libVlc;
    private static bool _initializeFailed;

    public static string? LastError { get; private set; }

    public static bool TryGetLibVlc(out LibVLC? libVlc)
    {
        if (_libVlc is not null)
        {
            libVlc = _libVlc;
            return true;
        }

        if (_initializeFailed)
        {
            libVlc = null;
            return false;
        }

        try
        {
            Core.Initialize();
            _libVlc = new LibVLC(false);
            libVlc = _libVlc;
            Log.Information("LibVLC erfolgreich initialisiert");
            return true;
        }
        catch (Exception ex)
        {
            _initializeFailed = true;
            LastError = OperatingSystem.IsLinux()
                ? "VLC-Bibliothek nicht gefunden. Bitte VLC installieren (z.B. 'sudo apt install vlc' unter Ubuntu/Debian, 'sudo dnf install vlc' unter Fedora) und die App neu starten."
                : $"VLC-Bibliothek konnte nicht geladen werden: {ex.Message}";
            Log.Error(ex, "LibVLC-Initialisierung fehlgeschlagen");
            libVlc = null;
            return false;
        }
    }

    /// <summary>
    ///     Schneidet einen Sound über die LibVLC-Optionen start-time/stop-time zurecht (Sekunden,
    ///     invariant formatiert - VLC parst diese Optionen wie eine Kommandozeile, ein per Kultur
    ///     abweichendes Dezimaltrennzeichen wie "," würde den Wert stillschweigend verwerfen).
    ///     0 bedeutet an der jeweiligen Stelle "kein Trim".
    /// </summary>
    public static void ApplySoundTrim(Media media, long trimStartMs, long trimEndMs)
    {
        if (trimStartMs > 0)
            media.AddOption($":start-time={(trimStartMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}");

        if (trimEndMs > 0)
            media.AddOption($":stop-time={(trimEndMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}");
    }
}