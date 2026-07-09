using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RpgTimeTracker.Services;

public static class SoundService
{
    public const string None = "Kein Ton";
    public const string Pling = "Pling";
    public const string Bell = "Glocke";
    public const string Digital = "Digital";

    /// <summary>
    ///     Aktuell aus der Sound-Bibliothek registrierte Namen -&gt; Pfad (siehe SyncLibrarySounds).
    ///     Ersetzt die frühere, separat gepflegte "Custom Sounds"-Liste aus den Einstellungen.
    /// </summary>
    private static readonly Dictionary<string, string> LibrarySoundPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kurze Pause zwischen zwei Wiederholungen desselben Sounds, damit sie hörbar getrennt bleiben.</summary>
    private static readonly TimeSpan RepeatGap = TimeSpan.FromMilliseconds(300);

    public static ObservableCollection<string> SoundOptions { get; } =
    [
        Pling,
        Bell,
        Digital,
        None
    ];

    /// <summary>
    ///     Ersetzt alle bisher aus der Sound-Bibliothek registrierten Einträge in SoundOptions durch die
    ///     aktuell übergebenen (Built-ins Pling/Glocke/Digital/Kein Ton bleiben davon unberührt). Wird
    ///     beim Start (nach dem Laden der Bibliothek) sowie bei jeder Änderung der Bibliothek
    ///     (hinzufügen/entfernen/umbenennen) aufgerufen, siehe MainWindowViewModel.SyncSoundServiceLibrary.
    /// </summary>
    public static void SyncLibrarySounds(IEnumerable<(string Name, string Path)> sounds)
    {
        foreach (var name in LibrarySoundPaths.Keys.ToList()) SoundOptions.Remove(name);
        LibrarySoundPaths.Clear();

        foreach (var (name, path) in sounds)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;

            LibrarySoundPaths[name] = path;
            if (!SoundOptions.Contains(name)) SoundOptions.Insert(Math.Max(0, SoundOptions.Count - 1), name);
        }
    }

    /// <summary>
    ///     Ob ein gewählter Sound-Name auf einen Sound-Bibliothek-Eintrag verweist (statt auf
    ///     einen Built-in) - maßgeblich dafür, ob ein auslösendes Element den Sound an Spieler sendet
    ///     (siehe MainWindowViewModel.PlaySound(Guid,...)) statt ihn nur lokal beim SL abzuspielen.
    /// </summary>
    public static bool TryGetLibrarySoundPath(string? name, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return LibrarySoundPaths.TryGetValue(name, out path!);
    }

    /// <summary>
    ///     repeatCount &lt;= 0 bedeutet endlose Wiederholung - läuft, bis cancellationToken abgebrochen
    ///     wird (siehe MainWindowViewModel.StopInfiniteSoundLoop, aufgerufen beim Reset des
    ///     auslösenden Timers/Weckers/Intervalls). Für repeatCount &gt;= 1 ist der Token optional.
    /// </summary>
    public static void Play(string? soundName, int repeatCount = 1, CancellationToken cancellationToken = default)
    {
        if (string.Equals(soundName, None, StringComparison.OrdinalIgnoreCase)) return;

        var path = ResolvePath(soundName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            TryFallbackBeep();
            return;
        }

        var infinite = repeatCount <= 0;
        var count = infinite ? 0 : repeatCount;

        Task.Run(() =>
        {
            for (var i = 0; infinite || i < count; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;

                PlayPlatformSound(path);

                if (cancellationToken.IsCancellationRequested) return;
                if (infinite || i < count - 1)
                    try
                    {
                        Task.Delay(RepeatGap, cancellationToken).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
            }
        }, cancellationToken);
    }

    private static void PlayPlatformSound(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    using var player = new SoundPlayer(path);
                    player.PlaySync();
                    return;
                }

                if (TryRunPlayer("ffplay", $"-nodisp -autoexit -loglevel quiet {Quote(path)}")) return;
                TryFallbackBeep();
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                if (TryRunPlayer("afplay", Quote(path)))
                    return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (TryRunPlayer("paplay", Quote(path))) return;
                if (TryRunPlayer("pw-play", Quote(path))) return;
                if (TryRunPlayer("aplay", Quote(path))) return;
                if (TryRunPlayer("ffplay", $"-nodisp -autoexit -loglevel quiet {Quote(path)}")) return;
            }

            TryFallbackBeep();
        }
        catch
        {
            TryFallbackBeep();
        }
    }

    private static string ResolvePath(string? soundName)
    {
        if (!string.IsNullOrWhiteSpace(soundName) &&
            LibrarySoundPaths.TryGetValue(soundName, out var customPath))
            return customPath;

        var fileName = soundName switch
        {
            Bell => "bell.wav",
            Digital => "digital.wav",
            _ => "pling.wav"
        };

        return Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", fileName);
    }

    // Wartet bewusst auf das Prozessende (statt fire-and-forget): läuft ohnehin auf einem
    // Task.Run-Hintergrundthread, und nur so spielen mehrere Wiederholungen (SoundRepeatCount)
    // nacheinander statt einander überlappend ab.
    private static bool TryRunPlayer(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (process is null) return false;

            process.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string path)
    {
        return $"\"{path.Replace("\"", "\\\"")}\"";
    }

    private static void TryFallbackBeep()
    {
        // Absichtlich kein Console.Beep: Console.Beep ist nicht plattformübergreifend
        // und erzeugt unter Linux/macOS CA1416-Warnungen. Sound ist Komfortfunktion;
        // wenn kein Player verfügbar ist, wird der Fehler still ignoriert.
    }
}