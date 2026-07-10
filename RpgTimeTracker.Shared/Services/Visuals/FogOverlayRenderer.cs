using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Shared.Services.Visuals;

/// <summary>
///     Renders a FogMask as a tiny bitmap - one pixel per grid cell. Stretched over the floor
///     image in the same panel cell (Stretch="Uniform"), it lines up exactly since the grid
///     dimensions are derived directly from the image's pixel size - far cheaper than issuing a
///     DrawRectangle per cell every frame, and any fog change just rebuilds this small bitmap.
/// </summary>
public static class FogOverlayRenderer
{
    /// <summary>Used by the SL Map Editor Window: a semi-transparent overlay so the GM can still
    ///     reference the underlying map while editing, unlike the player-facing view.</summary>
    public static readonly Color EditorHiddenColor = Color.FromArgb(140, 0, 0, 0);

    /// <summary>Used by the PlayerClient/Host local preview as the v1 default tint.</summary>
    public static readonly Color PlayerHiddenColor = Color.FromArgb(255, 12, 12, 12);

    /// <summary>Combines a GM-configured color hex + opacity percent (see issue #22) into the
    ///     alpha-baked Color the tint layer expects - shared by the Host (Settings tab) and
    ///     the PlayerClient (session.snapshot/map.renderStyleChanged).</summary>
    public static Color BuildHiddenColor(string colorHex, int opacityPercent)
    {
        var baseColor = Color.TryParse(colorHex, out var parsed) ? parsed : Color.FromRgb(12, 12, 12);
        var alpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255);
        return new Color(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>
    ///     Simple flat-colored overlay, no blur - used by the SL Map Editor Window's own
    ///     reference view, which doesn't need the player-facing blur/mask-cutout treatment (see
    ///     BuildMaskBitmap): the GM just needs a quick visual reminder of what's currently hidden.
    /// </summary>
    public static WriteableBitmap BuildColoredOverlayBitmap(FogMask fog, Color hiddenColor)
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

    /// <summary>
    ///     A plain opacity-mask shape - opaque white for hidden cells, fully transparent for
    ///     revealed ones, no color baked in. Used as an Avalonia OpacityMask brush to cut out
    ///     which part of a blurred copy of the map image (and a separate color-tint layer) is
    ///     visible - see MapDisplayViewModel/MapDisplayView. Blurring a flat-colored fog blob
    ///     directly used to be next to invisible once stretched across a large map (a one-pixel-
    ///     per-cell bitmap has nothing for a blur kernel to smooth across); blurring the actual
    ///     map image instead, and only using this mask to decide *where* that blur shows through,
    ///     both looks better and sidesteps that problem entirely. Cell edges stay crisp by
    ///     design - the blur is visible in the *content*, not the cell boundary.
    /// </summary>
    public static WriteableBitmap BuildMaskBitmap(FogMask fog)
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
                buffer[offset] = 255;
                buffer[offset + 1] = 255;
                buffer[offset + 2] = 255;
                buffer[offset + 3] = 255;
            }
        }

        Marshal.Copy(buffer, 0, frameBuffer.Address, buffer.Length);
        return bitmap;
    }
}
