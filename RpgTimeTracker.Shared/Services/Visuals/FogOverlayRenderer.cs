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

    public static WriteableBitmap BuildOverlayBitmap(FogMask fog, Color hiddenColor)
    {
        var width = fog.GridWidth;
        var height = fog.GridHeight;
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
                if (fog.IsRevealed(x, y)) continue;

                var offset = rowOffset + x * 4;
                buffer[offset] = hiddenColor.B;
                buffer[offset + 1] = hiddenColor.G;
                buffer[offset + 2] = hiddenColor.R;
                buffer[offset + 3] = hiddenColor.A;
            }
        }

        Marshal.Copy(buffer, 0, frameBuffer.Address, buffer.Length);
        return bitmap;
    }
}
