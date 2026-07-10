using System.Text;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class SaveFileArchiveTests
{
    [Fact]
    public void Round_trip_preserves_the_state_json_exactly()
    {
        const string json = """{"CurrentGameTime":"2026-01-01T08:00:00","Timers":[]}""";

        var wrapped = SaveFileArchive.Wrap(json);
        var unwrapped = SaveFileArchive.Unwrap(wrapped);

        Assert.Equal(json, unwrapped);
    }

    [Fact]
    public void Wrapped_bytes_are_a_real_zip_containing_the_expected_entry_name()
    {
        var wrapped = SaveFileArchive.Wrap("{}");

        using var stream = new MemoryStream(wrapped);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry(SaveFileArchive.StateEntryName));
    }

    [Fact]
    public void Unwrap_falls_back_to_treating_non_zip_bytes_as_the_state_json_directly()
    {
        // Models the upgrade path from an old plain-JSON save, written before the
        // .rtt-save zip format existed - it's just UTF-8 text, not a zip at all.
        const string oldPlainJsonSave = """{"CurrentGameTime":"2025-06-01T12:00:00"}""";
        var oldSaveBytes = Encoding.UTF8.GetBytes(oldPlainJsonSave);

        var result = SaveFileArchive.Unwrap(oldSaveBytes);

        Assert.Equal(oldPlainJsonSave, result);
    }

    [Fact]
    public void Unwrap_falls_back_when_given_a_valid_zip_missing_the_expected_entry()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("something-else.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("not the save state");
        }

        var bytes = stream.ToArray();
        var result = SaveFileArchive.Unwrap(bytes);

        // Falls back to raw bytes-as-text, since this isn't a shape we understand -
        // downstream JSON parsing is expected to fail gracefully on this, not Unwrap itself.
        Assert.Equal(Encoding.UTF8.GetString(bytes), result);
    }
}
