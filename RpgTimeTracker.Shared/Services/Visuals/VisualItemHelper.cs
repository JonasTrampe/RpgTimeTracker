using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;

namespace RpgTimeTracker.Shared.Services.Visuals;

public sealed class BootstrapIconChoice
{
    public BootstrapIconChoice(string name)
    {
        Name = name;
        Geometry = VisualItemHelper.IconGeometry(name);
    }

    public string Name { get; }
    public Geometry Geometry { get; }
}

/// <summary>
///     Icon selection based on Bootstrap Icons (MIT).
///     The path data is taken from the official Bootstrap Icons SVGs.
/// </summary>
public static class VisualItemHelper
{
    public const string IconTimer = "Bootstrap: Clock";
    public const string IconAlarm = "Bootstrap: Alarm";
    public const string IconOnTime = "Bootstrap: Lightning";
    public const string IconHourglass = "Bootstrap: Hourglass";
    public const string IconFire = "Bootstrap: Fire";
    public const string IconShield = "Bootstrap: Shield";
    public const string IconWarning = "Bootstrap: Warning";
    public const string IconEye = "Bootstrap: Eye";
    public const string IconStar = "Bootstrap: Star";
    public const string IconMoon = "Bootstrap: Moon";
    public const string IconDice = "Bootstrap: Dice";
    public const string IconHeart = "Bootstrap: Heart";
    public const string IconGear = "Bootstrap: Gear";
    public const string IconKey = "Bootstrap: Key";
    public const string IconCalendar = "Bootstrap: Calendar";

    private static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);

    public static ObservableCollection<string> IconOptions { get; } = new()
    {
        IconTimer,
        IconAlarm,
        IconOnTime,
        IconHourglass,
        IconFire,
        IconShield,
        IconWarning,
        IconEye,
        IconStar,
        IconMoon,
        IconDice,
        IconHeart,
        IconGear,
        IconKey,
        IconCalendar
    };

    public static IReadOnlyList<string> AllIconNames => IconOptions;

    public static IEnumerable<BootstrapIconChoice> SearchIconChoices(string? searchText)
    {
        var query = searchText?.Trim();
        var matches = string.IsNullOrWhiteSpace(query)
            ? IconOptions
            : IconOptions.Where(icon => icon.Contains(query, StringComparison.OrdinalIgnoreCase));

        return matches.Select(icon => new BootstrapIconChoice(icon));
    }

    public static IBrush? TryBrush(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex)) return null;

        try
        {
            return Brush.Parse(colorHex.Trim());
        }
        catch
        {
            return null;
        }
    }

    public static bool BlinkFrame()
    {
        return DateTime.Now.Millisecond < 500;
    }

    public static string NormalizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return IconTimer;

        return icon.Trim() switch
        {
            "⏳" => IconTimer,
            "⏰" => IconAlarm,
            "⚡" => IconOnTime,
            "🔥" => IconFire,
            "🛡" => IconShield,
            "🚨" => IconWarning,
            "👁" => IconEye,
            "⭐" => IconStar,
            "🌙" => IconMoon,
            "🎲" => IconDice,
            "❤" => IconHeart,
            "⚙" => IconGear,
            "🔑" => IconKey,
            "📅" => IconCalendar,
            var value when IconOptions.Contains(value) => value,
            _ => IconTimer
        };
    }

    public static Geometry IconGeometry(string? icon)
    {
        var key = NormalizeIcon(icon);
        if (GeometryCache.TryGetValue(key, out var cached)) return cached;

        var data = key switch
        {
            IconAlarm =>
                "M8.5 5.5a.5.5 0 0 0-1 0v3.362l-1.429 2.38a.5.5 0 1 0 .858.515l1.5-2.5A.5.5 0 0 0 8.5 9z " +
                "M6.5 0a.5.5 0 0 0 0 1H7v1.07a7.001 7.001 0 0 0-3.273 12.474l-.602.602a.5.5 0 0 0 .707.708l.746-.746A6.97 6.97 0 0 0 8 16a6.97 6.97 0 0 0 3.422-.892l.746.746a.5.5 0 0 0 .707-.708l-.601-.602A7.001 7.001 0 0 0 9 2.07V1h.5a.5.5 0 0 0 0-1zm1.038 3.018a6 6 0 0 1 .924 0 6 6 0 1 1-.924 0",
            IconOnTime =>
                "M11.251.068a.5.5 0 0 1 .227.58L9.677 6.5H13a.5.5 0 0 1 .364.843l-8 8.5a.5.5 0 0 1-.842-.49L6.323 9.5H3a.5.5 0 0 1-.364-.843l8-8.5a.5.5 0 0 1 .615-.09z",
            IconHourglass =>
                "M2.5 15a.5.5 0 1 1 0-1h1v-1a4.5 4.5 0 0 1 2.557-4.06c.29-.139.443-.377.443-.59v-.7c0-.213-.154-.451-.443-.59A4.5 4.5 0 0 1 3.5 3V2h-1a.5.5 0 0 1 0-1h11a.5.5 0 0 1 0 1h-1v1a4.5 4.5 0 0 1-2.557 4.06c-.29.139-.443.377-.443.59v.7c0 .213.154.451.443.59A4.5 4.5 0 0 1 12.5 13v1h1a.5.5 0 0 1 0 1z",
            IconFire =>
                "M8 16c3.314 0 6-2 6-5.5 0-1.5-.5-4-2.5-6 .25 1.5-1.25 2-1.25 2C11 4 9 .5 6 0c.357 2 .5 4-2 6-1.25 1-2 2.729-2 4.5C2 14 4.686 16 8 16",
            IconShield =>
                "M5.338 1.59a61 61 0 0 0-2.837.856.48.48 0 0 0-.328.39c-.554 4.157.726 7.19 2.253 9.188a10.7 10.7 0 0 0 2.287 2.233c.346.244.652.42.893.533q.18.085.293.118a1 1 0 0 0 .101.025 1 1 0 0 0 .1-.025q.114-.034.294-.118c.24-.113.547-.29.893-.533a10.7 10.7 0 0 0 2.287-2.233c1.527-1.997 2.807-5.031 2.253-9.188a.48.48 0 0 0-.328-.39c-.651-.213-1.75-.56-2.837-.855C9.552 1.29 8.531 1.067 8 1.067c-.53 0-1.552.223-2.662.524z",
            IconWarning =>
                "M8.982 1.566a1.13 1.13 0 0 0-1.96 0L.165 13.233c-.457.778.091 1.767.98 1.767h13.713c.889 0 1.438-.99.98-1.767zM8 5c.535 0 .954.462.9.995l-.35 3.507a.552.552 0 0 1-1.1 0L7.1 5.995A.905.905 0 0 1 8 5m.002 6a1 1 0 1 1 0 2 1 1 0 0 1 0-2",
            IconEye =>
                "M16 8s-3-5.5-8-5.5S0 8 0 8s3 5.5 8 5.5S16 8 16 8M1.173 8a13 13 0 0 1 1.66-2.043C4.12 4.668 5.88 3.5 8 3.5s3.879 1.168 5.168 2.457A13 13 0 0 1 14.828 8q-.086.13-.195.288c-.335.48-.83 1.12-1.465 1.755C11.879 11.332 10.119 12.5 8 12.5s-3.879-1.168-5.168-2.457A13 13 0 0 1 1.172 8z M8 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5",
            IconStar =>
                "M2.866 14.85c-.078.444.36.791.746.593L8 13.187l4.389 2.256c.386.198.824-.149.746-.592l-.83-4.73 3.522-3.356c.329-.314.158-.888-.283-.95l-4.898-.696L8.465.792a.513.513 0 0 0-.93 0L5.354 5.12l-4.898.696c-.441.062-.612.636-.283.95l3.522 3.356z",
            IconMoon =>
                "M6 .278a.77.77 0 0 1 .08.858 7.2 7.2 0 0 0-.878 3.46c0 4.021 3.278 7.277 7.318 7.277.527 0 1.04-.055 1.533-.16a.79.79 0 0 1 .81.316.73.73 0 0 1-.031.893A8.35 8.35 0 0 1 8.344 16C3.734 16 0 12.286 0 7.71 0 4.266 2.114 1.312 5.124.06A.75.75 0 0 1 6 .278",
            IconDice =>
                "M13 1a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V3a2 2 0 0 1 2-2z M3 2a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V3a1 1 0 0 0-1-1z M5.5 4a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3m5 5a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3",
            IconHeart =>
                "m8 2.748-.717-.737C5.6.281 2.514.878 1.4 3.053c-.523 1.023-.641 2.5.314 4.385.92 1.815 2.834 3.989 6.286 6.357 3.452-2.368 5.365-4.542 6.286-6.357.955-1.886.838-3.362.314-4.385C13.486.878 10.4.28 8.717 2.01z",
            IconGear =>
                "M8 4.754a3.246 3.246 0 1 0 0 6.492 3.246 3.246 0 0 0 0-6.492M5.754 8a2.246 2.246 0 1 1 4.492 0 2.246 2.246 0 0 1-4.492 0",
            IconKey =>
                "M0 8a4 4 0 0 1 7.465-2H14a.5.5 0 0 1 .354.146l1.5 1.5a.5.5 0 0 1 0 .708l-1.5 1.5A.5.5 0 0 1 14 10h-1v1.5a.5.5 0 0 1-.5.5h-2a.5.5 0 0 1-.5-.5V10H7.465A4 4 0 0 1 0 8",
            IconCalendar =>
                "M3.5 0a.5.5 0 0 1 .5.5V1h8V.5a.5.5 0 0 1 1 0V1h1a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2V3a2 2 0 0 1 2-2h1V.5a.5.5 0 0 1 .5-.5M1 4v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V4z",
            _ =>
                "M8 3.5a.5.5 0 0 0-1 0V9a.5.5 0 0 0 .252.434l3.5 2a.5.5 0 0 0 .496-.868L8 8.71z M8 16A8 8 0 1 0 8 0a8 8 0 0 0 0 16m7-8A7 7 0 1 1 1 8a7 7 0 0 1 14 0"
        };

        var geometry = Geometry.Parse(data);
        GeometryCache[key] = geometry;
        return geometry;
    }
}