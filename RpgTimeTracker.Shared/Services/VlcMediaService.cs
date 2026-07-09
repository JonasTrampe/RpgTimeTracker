using System;
using System.Globalization;
using LibVLCSharp.Shared;
using Serilog;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Manages the single LibVLC instance of the process. On Windows the native
///     library is bundled via the NuGet package VideoLAN.LibVLC.Windows; on Linux
///     VLC must be installed system-wide (e.g. "sudo apt install vlc" / "sudo dnf install vlc").
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
            Log.Information("LibVLC successfully initialized");
            return true;
        }
        catch (Exception ex)
        {
            _initializeFailed = true;
            LastError = OperatingSystem.IsLinux()
                ? "VLC-Bibliothek nicht gefunden. Bitte VLC installieren (z.B. 'sudo apt install vlc' unter Ubuntu/Debian, 'sudo dnf install vlc' unter Fedora) und die App neu starten."
                : $"VLC-Bibliothek konnte nicht geladen werden: {ex.Message}";
            Log.Error(ex, "LibVLC initialization failed");
            libVlc = null;
            return false;
        }
    }

    /// <summary>
    ///     Trims a sound using the LibVLC start-time/stop-time options (seconds,
    ///     formatted invariantly - VLC parses these options like a command line, a culture-
    ///     specific decimal separator like "," would silently discard the value).
    ///     0 means "no trim" at the respective position.
    /// </summary>
    public static void ApplySoundTrim(Media media, long trimStartMs, long trimEndMs)
    {
        if (trimStartMs > 0)
            media.AddOption($":start-time={(trimStartMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}");

        if (trimEndMs > 0)
            media.AddOption($":stop-time={(trimEndMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}");
    }
}