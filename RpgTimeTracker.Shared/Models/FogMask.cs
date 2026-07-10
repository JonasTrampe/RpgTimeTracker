using System;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     A grid of revealed/hidden cells over a map floor image. One bit per cell
///     (row-major), so a fine-grained grid (e.g. 10px cells on a 3000px-wide map,
///     300 cells across) still fits in a few KB - see FogMaskSerializer for the
///     at-rest binary format used by both the Map Library's starting-fog file and
///     the save file's per-floor "current fog" entries.
/// </summary>
public sealed class FogMask
{
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>Cell size in map-image pixels. Configurable per floor - a fine grid (down to
    ///     ~5-10px) gives detailed reveal shapes, a coarse one keeps the mask tiny.</summary>
    public int CellSizePx { get; set; } = 32;

    /// <summary>1 bit per cell, row-major, packed 8 cells per byte. Length = ceil(GridWidth * GridHeight / 8).</summary>
    public byte[] RevealedBits { get; set; } = [];

    public bool IsRevealed(int x, int y)
    {
        var index = CellIndex(x, y);
        return (RevealedBits[index / 8] & (1 << (index % 8))) != 0;
    }

    public void SetRevealed(int x, int y, bool revealed)
    {
        var index = CellIndex(x, y);
        var byteIndex = index / 8;
        var bit = 1 << (index % 8);
        if (revealed) RevealedBits[byteIndex] |= (byte)bit;
        else RevealedBits[byteIndex] &= (byte)~bit;
    }

    public FogMask Clone()
    {
        return new FogMask
        {
            GridWidth = GridWidth,
            GridHeight = GridHeight,
            CellSizePx = CellSizePx,
            RevealedBits = (byte[])RevealedBits.Clone()
        };
    }

    public static FogMask CreateFullyHidden(int gridWidth, int gridHeight, int cellSizePx)
    {
        var byteCount = (gridWidth * gridHeight + 7) / 8;
        return new FogMask
        {
            GridWidth = gridWidth,
            GridHeight = gridHeight,
            CellSizePx = cellSizePx,
            RevealedBits = new byte[byteCount]
        };
    }

    private int CellIndex(int x, int y)
    {
        if (x < 0 || x >= GridWidth || y < 0 || y >= GridHeight)
            throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x},{y}) is outside the {GridWidth}x{GridHeight} grid.");

        return y * GridWidth + x;
    }
}
