using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Resamples a FogMask onto a new grid when a floor's CellSizePx changes, so existing
///     revealed/hidden cells stay roughly aligned instead of being silently corrupted (grid
///     dimensions are derived from CellSizePx - changing it without rescaling would misalign
///     every cell).
/// </summary>
public static class FogMaskRescaler
{
    /// <summary>
    ///     Nearest-neighbor resample: maps each new-grid cell's CENTER (not corner) back to
    ///     old-grid coordinates via the ratio of new to old cell size, then rounds to the nearest
    ///     old cell. Sampling from the corner instead of the center introduces a systematic
    ///     directional bias - every resample nudges the revealed/hidden boundary toward one edge,
    ///     which compounds into visible drift after repeated CellSizePx changes. Center-sampling
    ///     keeps the resample unbiased regardless of the new/old cell-size ratio.
    /// </summary>
    public static FogMask Rescale(FogMask source, int newCellSizePx, int newGridWidth, int newGridHeight)
    {
        var result = FogMask.CreateFullyHidden(newGridWidth, newGridHeight, newCellSizePx);
        for (var y = 0; y < newGridHeight; y++)
        for (var x = 0; x < newGridWidth; x++)
        {
            var oldX = (int)((x + 0.5) * newCellSizePx / source.CellSizePx);
            var oldY = (int)((y + 0.5) * newCellSizePx / source.CellSizePx);
            if (oldX < 0 || oldY < 0 || oldX >= source.GridWidth || oldY >= source.GridHeight) continue;
            if (source.IsRevealed(oldX, oldY)) result.SetRevealed(x, y, true);
        }

        return result;
    }
}