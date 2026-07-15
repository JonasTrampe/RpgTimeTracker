using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Ingest helper that names a stored file by its content hash instead of a fresh random Guid,
///     so adding/importing the exact same file twice into the same library directory doesn't
///     store it twice. Deliberately scoped per-directory: the same file added to two different
///     directories (e.g. the Shared Library vs. a Session's own folder - see SessionService) is
///     still stored once per directory, not deduped across them - each pool is independent.
/// </summary>
public static class ContentAddressedStorage
{
    /// <summary>
    ///     Copies sourcePath into destinationDirectory (created if missing), returning the
    ///     path to the resulting file - a pre-existing file with the same content is reused
    ///     as-is rather than copied again.
    /// </summary>
    public static async Task<string> StoreFileAsync(string sourcePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var hash = await ComputeHashAsync(sourcePath);
        var destinationPath = Path.Combine(destinationDirectory, $"{hash}{Path.GetExtension(sourcePath)}");
        if (!File.Exists(destinationPath))
        {
            await using var source = File.OpenRead(sourcePath);
            await using var target = File.Create(destinationPath);
            await source.CopyToAsync(target);
        }

        return destinationPath;
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}