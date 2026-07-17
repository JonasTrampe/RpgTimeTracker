using System;
using Avalonia.Media;

namespace RpgTimeTracker.Shared.Services.Visuals;

/// <summary>
///     Deterministically derives a display color and short label from a player's persistent
///     ClientId (see ClientSettingsService.GetOrCreateClientId), so every participant (all
///     connected players and the GM) independently computes the identical color/label for a given
///     painter without the wire protocol having to carry either - just the raw ClientId already
///     sent with every map.annotationBroadcast (see RpcParams.MapAnnotationBroadcastParams).
/// </summary>
public static class PainterTagHelper
{
    /// <summary>
    ///     Bright, mutually distinct colors readable against most map art - deliberately not
    ///     including Gold, which stays reserved for the GM's own ping ripple (see
    ///     MapDisplayView.OnPingReceived) so a stroke is never confused with a ping.
    /// </summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x39, 0x35), // red
        Color.FromRgb(0x1E, 0x88, 0xE5), // blue
        Color.FromRgb(0x43, 0xA0, 0x47), // green
        Color.FromRgb(0xFB, 0x8C, 0x00), // orange
        Color.FromRgb(0x8E, 0x24, 0xAA), // purple
        Color.FromRgb(0x00, 0xAC, 0xC1), // cyan
        Color.FromRgb(0xD8, 0x1B, 0x60), // pink
        Color.FromRgb(0x6D, 0x4C, 0x41) // brown
    ];

    /// <summary>
    ///     Prefix marking a ClientId string as the GM's own sentinel (see MainWindowViewModel.
    ///     BroadcastGmAnnotationAsync) rather than a real player's persistent id - carries the GM's
    ///     own picked line color inline so the wire format (MapAnnotationBroadcastParams.ClientId)
    ///     doesn't need a dedicated color field just for this one case.
    /// </summary>
    private const string GmSentinelPrefix = "gm:";

    /// <summary>Builds the ClientId sentinel for a GM-drawn Temporary line with the given hex color.</summary>
    public static string GmSentinelFor(string colorHex) => GmSentinelPrefix + colorHex;

    /// <summary>
    ///     True (with the parsed color) if clientId is the GM's sentinel (see GmSentinelFor) or the
    ///     legacy empty-string GM marker (defaults to Gold) - false for any real player's ClientId.
    /// </summary>
    public static bool TryGetGmColor(string clientId, out Color color)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            color = Colors.Gold;
            return true;
        }

        if (clientId.StartsWith(GmSentinelPrefix, StringComparison.Ordinal) &&
            Color.TryParse(clientId[GmSentinelPrefix.Length..], out color))
            return true;

        color = default;
        return false;
    }

    public static Color ColorFor(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return Palette[0];

        var hash = unchecked((uint)clientId.GetHashCode());
        return Palette[hash % (uint)Palette.Length];
    }

    public static IBrush BrushFor(string clientId)
    {
        return new SolidColorBrush(ColorFor(clientId));
    }

    /// <summary>Short, stable tag derived from the ClientId - e.g. "A3F9" - not a real name (none exists, see ClientId's doc comment).</summary>
    public static string ShortTagFor(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return "????";

        var hash = unchecked((uint)clientId.GetHashCode());
        return hash.ToString("X4");
    }
}
