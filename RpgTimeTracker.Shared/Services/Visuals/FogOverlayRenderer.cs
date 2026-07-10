using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services.Visuals;

/// <summary>
///     Renders a FogMask as a tiny bitmap - one pixel per grid cell, one solid color for hidden
///     cells and fully transparent for revealed ones. Stretched (Stretch="Uniform", nearest-
///     neighbor) over the floor image in the same panel cell, it lines up exactly since the grid
///     dimensions are derived directly from the image's pixel size - far cheaper than issuing a
///     DrawRectangle per cell every frame, and any fog change just rebuilds this small bitmap.
/// </summary>
public static class FogOverlayRenderer
{
    /// <summary>Used by the SL Map Editor Window: a semi-transparent overlay so the GM can still
    ///     reference the underlying map while editing, unlike the player-facing solid block.</summary>
    public static readonly Color EditorHiddenColor = Color.FromArgb(140, 0, 0, 0);

    /// <summary>
    ///     Used by the PlayerClient: fully opaque, so hidden cells truly can't be seen through.
    ///     Configurable opacity/color/blur is a later milestone (issue #22) - this is the fixed
    ///     v1 default.
    /// </summary>
    public static readonly Color PlayerHiddenColor = Color.FromArgb(255, 12, 12, 12);

    /// <summary>Combines a GM-configured color hex + opacity percent (see issue #22) into the
    ///     alpha-baked Color BuildOverlayBitmap expects - shared by the Host (Settings tab) and
    ///     the PlayerClient (session.snapshot/map.renderStyleChanged).</summary>
    public static Color BuildHiddenColor(string colorHex, int opacityPercent)
    {
        var baseColor = Color.TryParse(colorHex, out var parsed) ? parsed : Color.FromRgb(12, 12, 12);
        var alpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255);
        return new Color(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>
    ///     A one-pixel-per-cell overlay would make a blur radius specified in cells essentially
    ///     invisible once stretched across a map that's hundreds of screen pixels wide (each
    ///     "step" between cells is still a single source pixel, however large the blur kernel).
    ///     When blur is requested, the overlay is instead rendered at this many sub-pixels per
    ///     cell so a blur kernel actually has neighboring samples to smooth across - the bitmap
    ///     is still tiny (a few hundred pixels at most for a typical map) compared to the final
    ///     on-screen size.
    /// </summary>
    private const int BlurSupersample = 4;

    /// <param name="blurRadiusCells">
    ///     Softening radius in grid cells (0 = crisp per-cell edges, the v1 default look). Baked
    ///     directly into the overlay's alpha values via a repeated box blur (approximates
    ///     Gaussian) rather than relying on a declarative render effect, so the result is
    ///     predictable regardless of how large the map ends up being stretched on screen.
    /// </param>
    public static WriteableBitmap BuildOverlayBitmap(FogMask fog, Color hiddenColor, double blurRadiusCells = 0)
    {
        var supersample = blurRadiusCells > 0 ? BlurSupersample : 1;
        var width = fog.GridWidth * supersample;
        var height = fog.GridHeight * supersample;

        var alpha = new double[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            alpha[y, x] = fog.IsRevealed(x / supersample, y / supersample) ? 0.0 : 1.0;

        if (blurRadiusCells > 0)
        {
            var radius = Math.Max(1, (int)Math.Round(blurRadiusCells * supersample));
            for (var pass = 0; pass < 3; pass++)
            {
                alpha = BoxBlurHorizontal(alpha, width, height, radius);
                alpha = BoxBlurVertical(alpha, width, height, radius);
            }
        }

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var frameBuffer = bitmap.Lock();
        var stride = frameBuffer.RowBytes;
        var buffer = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var a = alpha[y, x];
                if (a <= 0) continue;

                var offset = rowOffset + x * 4;
                buffer[offset] = hiddenColor.B;
                buffer[offset + 1] = hiddenColor.G;
                buffer[offset + 2] = hiddenColor.R;
                buffer[offset + 3] = (byte)Math.Clamp(a * hiddenColor.A, 0, 255);
            }
        }

        Marshal.Copy(buffer, 0, frameBuffer.Address, buffer.Length);
        return bitmap;
    }

    private static double[,] BoxBlurHorizontal(double[,] src, int width, int height, int radius)
    {
        var dst = new double[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            var count = 0;
            for (var dx = -radius; dx <= radius; dx++)
            {
                var sx = x + dx;
                if (sx < 0 || sx >= width) continue;
                sum += src[y, sx];
                count++;
            }

            dst[y, x] = sum / count;
        }

        return dst;
    }

    private static double[,] BoxBlurVertical(double[,] src, int width, int height, int radius)
    {
        var dst = new double[height, width];
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            double sum = 0;
            var count = 0;
            for (var dy = -radius; dy <= radius; dy++)
            {
                var sy = y + dy;
                if (sy < 0 || sy >= height) continue;
                sum += src[sy, x];
                count++;
            }

            dst[y, x] = sum / count;
        }

        return dst;
    }
}
