using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

public class FogMaskSerializerTests
{
    [Fact]
    public void Round_trip_preserves_dimensions_and_cell_size()
    {
        var mask = FogMask.CreateFullyHidden(12, 9, 8);

        var bytes = FogMaskSerializer.Serialize(mask);
        var restored = FogMaskSerializer.Deserialize(bytes);

        Assert.Equal(mask.GridWidth, restored.GridWidth);
        Assert.Equal(mask.GridHeight, restored.GridHeight);
        Assert.Equal(mask.CellSizePx, restored.CellSizePx);
    }

    [Fact]
    public void Round_trip_preserves_every_revealed_cell()
    {
        var mask = FogMask.CreateFullyHidden(20, 15, 10);
        mask.SetRevealed(0, 0, true);
        mask.SetRevealed(19, 14, true);
        mask.SetRevealed(5, 7, true);

        var restored = FogMaskSerializer.Deserialize(FogMaskSerializer.Serialize(mask));

        for (var y = 0; y < 15; y++)
        for (var x = 0; x < 20; x++)
            Assert.Equal(mask.IsRevealed(x, y), restored.IsRevealed(x, y));
    }

    [Fact]
    public void A_large_uniform_mask_compresses_to_far_less_than_its_raw_bit_size()
    {
        // Fog masks are mostly large uniform blocks - this is the whole reason a
        // GZip-compressed bitset was chosen over a plain per-cell format.
        var mask = FogMask.CreateFullyHidden(300, 300, 10);

        var bytes = FogMaskSerializer.Serialize(mask);

        var rawBitsetBytes = (300 * 300 + 7) / 8;
        Assert.True(bytes.Length < rawBitsetBytes / 4);
    }

    [Fact]
    public void Deserialize_rejects_an_unsupported_format_version()
    {
        var mask = FogMask.CreateFullyHidden(2, 2, 10);
        var bytes = FogMaskSerializer.Serialize(mask);
        bytes[0] = 99; // corrupt the version byte

        Assert.Throws<InvalidDataException>(() => FogMaskSerializer.Deserialize(bytes));
    }
}