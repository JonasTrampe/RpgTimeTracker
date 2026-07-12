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
    /// <summary>Nearest-neighbor resample: maps each new-grid cell's top-left corner back to
    ///     old-grid coordinates (via the ratio of new to old cell size) so existing revealed/hidden
    ///     areas stay aligned when CellSizePx changes.</summary>
    public static FogMask Rescale(FogMask source, int newCellSizePx, int newGridWidth, int newGridHeight)
    {
        var result = FogMask.CreateFullyHidden(newGridWidth, newGridHeight, newCellSizePx);
        for (var y = 0; y < newGridHeight; y++)
        for (var x = 0; x < newGridWidth; x++)
        {
            var oldX = x * newCellSizePx / source.CellSizePx;
            var oldY = y * newCellSizePx / source.CellSizePx;
            if (oldX >= source.GridWidth || oldY >= source.GridHeight) continue;
            if (source.IsRevealed(oldX, oldY)) result.SetRevealed(x, y, true);
        }

        return result;
    }
}
