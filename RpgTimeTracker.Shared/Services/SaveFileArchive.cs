using System.IO;
using System.IO.Compression;
using System.Text;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Wraps/unwraps the app's ".rtt-save" container: a zip archive holding "state.json"
///     (the AppStateDto JSON, unchanged) as one entry, with room for future binary
///     entries alongside it (e.g. fog-of-war map data) that don't belong in JSON.
///     A distinct extension - rather than keeping ".json" now that the content is a
///     zip - was chosen specifically so a save file isn't mistaken for arbitrary JSON
///     data. See docs/design-decisions.md "Save file: a .rtt-save zip container".
/// </summary>
public static class SaveFileArchive
{
    public const string StateEntryName = "state.json";

    public static byte[] Wrap(string stateJson)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(StateEntryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, Encoding.UTF8);
            writer.Write(stateJson);
        }

        return stream.ToArray();
    }

    /// <summary>
    ///     Extracts state.json from a .rtt-save zip. Falls back to treating the bytes as
    ///     the state JSON directly if they aren't a valid zip at all (or are a zip
    ///     missing the expected entry) - this is the entire upgrade path from before the
    ///     .rtt-save format existed: an old plain-JSON save just passes through unchanged.
    /// </summary>
    public static string Unwrap(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(StateEntryName)
                        ?? throw new InvalidDataException($"Zip is missing the {StateEntryName} entry.");

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            return Encoding.UTF8.GetString(data);
        }
    }
}
