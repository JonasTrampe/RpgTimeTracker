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
    ///     Names currently registered from the sound library -&gt; path (see SyncLibrarySounds).
    ///     Replaces the former, separately maintained "custom sounds" list from settings.
    /// </summary>
    private static readonly Dictionary<string, string> LibrarySoundPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Short pause between two repetitions of the same sound, so they remain audibly separated.</summary>
    private static readonly TimeSpan RepeatGap = TimeSpan.FromMilliseconds(300);

    public static ObservableCollection<string> SoundOptions { get; } =
    [
        Pling,
        Bell,
        Digital,
        None
    ];

    /// <summary>
    ///     Replaces all entries previously registered from the sound library in SoundOptions with the
    ///     currently passed ones (built-ins Pling/Bell/Digital/No Sound remain unaffected). Called
    ///     on startup (after loading the library) as well as on every change to the library
    ///     (add/remove/rename), see MainWindowViewModel.SyncSoundServiceLibrary.
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
    ///     Whether a chosen sound name refers to a sound library entry (rather than
    ///     a built-in) - determines whether a triggering item sends the sound to players
    ///     (see MainWindowViewModel.PlaySound(Guid,...)) instead of only playing it locally on the GM side.
    /// </summary>
    public static bool TryGetLibrarySoundPath(string? name, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return LibrarySoundPaths.TryGetValue(name, out path!);
    }

    /// <summary>
    ///     repeatCount &lt;= 0 means infinite repetition - runs until cancellationToken is cancelled
    ///     (see MainWindowViewModel.StopInfiniteSoundLoop, called when the triggering
    ///     timer/alarm/interval is reset). For repeatCount &gt;= 1 the token is optional.
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

    // Deliberately waits for the process to end (instead of fire-and-forget): it runs on a
    // Task.Run background thread anyway, and this is the only way multiple repetitions (SoundRepeatCount)
    // play one after another instead of overlapping.
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
        // Deliberately no Console.Beep: Console.Beep is not cross-platform
        // and produces CA1416 warnings on Linux/macOS. Sound is a convenience feature;
        // if no player is available, the error is silently ignored.
    }
}