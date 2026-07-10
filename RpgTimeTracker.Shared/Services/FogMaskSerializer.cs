using System;
using System.IO;
using System.IO.Compression;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Compact binary format for FogMask, used for at-rest storage (the Map
///     Library's starting-fog file, and the save file's per-floor current-fog zip
///     entries) - deliberately not JSON, since a fog mask is a bitmap, not
///     structured data, and JSON/base64 would bloat it for no benefit. BCL-only
///     (System.IO.Compression), no new dependency. Fog masks compress extremely
///     well (large uniform blocks), so this stays tiny even for a fine grid.
/// </summary>
public static class FogMaskSerializer
{
    private const byte FormatVersion = 1;

    public static byte[] Serialize(FogMask mask)
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(mask.GridWidth);
            writer.Write(mask.GridHeight);
            writer.Write(mask.CellSizePx);

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(mask.RevealedBits, 0, mask.RevealedBits.Length);

            var compressedBytes = compressed.ToArray();
            writer.Write(compressedBytes.Length);
            writer.Write(compressedBytes);
        }

        return output.ToArray();
    }

    public static FogMask Deserialize(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var reader = new BinaryReader(input);

        var version = reader.ReadByte();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported FogMask format version {version} (expected {FormatVersion}).");

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var cellSizePx = reader.ReadInt32();
        var compressedLength = reader.ReadInt32();
        var compressedBytes = reader.ReadBytes(compressedLength);

        using var compressed = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        gzip.CopyTo(decompressed);

        return new FogMask
        {
            GridWidth = width,
            GridHeight = height,
            CellSizePx = cellSizePx,
            RevealedBits = decompressed.ToArray()
        };
    }
}
