using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using Serilog;

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
///     Icon selection based on the full Bootstrap Icons set (MIT, ~2000 icons). Path data lives in
///     Assets/BootstrapIcons/bootstrap-icons.json (generated from the official SVG sources - see
///     BOOTSTRAP_ICONS_NOTICE.txt in the same folder), loaded lazily at runtime instead of being
///     embedded in source, so the entire catalogue is available without hand-copying path strings.
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
    public const string IconMap = "Bootstrap: Map";
    public const string IconCompass = "Bootstrap: Compass";
    public const string IconFlag = "Bootstrap: Flag";
    public const string IconLock = "Bootstrap: Lock";
    public const string IconUnlock = "Bootstrap: Unlock";
    public const string IconPerson = "Bootstrap: Person";
    public const string IconHouse = "Bootstrap: House";
    public const string IconCloudMoon = "Bootstrap: Cloud Moon";
    public const string IconDroplet = "Bootstrap: Droplet";
    public const string IconTrophy = "Bootstrap: Trophy";
    public const string IconMusic = "Bootstrap: Music";
    public const string IconCamera = "Bootstrap: Camera";
    public const string IconEnvelope = "Bootstrap: Envelope";
    public const string IconInfo = "Bootstrap: Info";
    public const string IconQuestion = "Bootstrap: Question";
    public const string IconExclamation = "Bootstrap: Exclamation";
    public const string IconCheck = "Bootstrap: Check";
    public const string IconCross = "Bootstrap: Cross";
    public const string IconPlus = "Bootstrap: Plus";
    public const string IconVolume = "Bootstrap: Volume";
    public const string IconStopwatch = "Bootstrap: Stopwatch";
    public const string IconCup = "Bootstrap: Cup";
    public const string IconGlobe = "Bootstrap: Globe";
    public const string IconPin = "Bootstrap: Pin";
    public const string IconBuilding = "Bootstrap: Building";
    public const string IconDoor = "Bootstrap: Door";
    public const string IconPuzzle = "Bootstrap: Puzzle";
    public const string IconPalette = "Bootstrap: Palette";
    public const string IconMagic = "Bootstrap: Magic Wand";
    public const string IconSnow = "Bootstrap: Snow";
    public const string IconWind = "Bootstrap: Wind";
    public const string IconUmbrella = "Bootstrap: Umbrella";
    public const string IconThermometer = "Bootstrap: Thermometer";
    public const string IconBook = "Bootstrap: Book";
    public const string IconSpade = "Bootstrap: Spade";

    // Preserves the original picker ordering for the names above (stable UI ordering, independent
    // of whatever order the full catalogue happens to enumerate in).
    private static readonly string[] LegacyOrder =
    [
        IconTimer, IconAlarm, IconOnTime, IconHourglass, IconFire, IconShield, IconWarning, IconEye,
        IconStar, IconMoon, IconDice, IconHeart, IconGear, IconKey, IconCalendar, IconMap, IconCompass,
        IconFlag, IconLock, IconUnlock, IconPerson, IconHouse, IconCloudMoon, IconDroplet, IconTrophy,
        IconMusic, IconCamera, IconEnvelope, IconInfo, IconQuestion, IconExclamation, IconCheck,
        IconCross, IconPlus, IconVolume, IconStopwatch, IconCup, IconGlobe, IconPin, IconBuilding,
        IconDoor, IconPuzzle, IconPalette, IconMagic, IconSnow, IconWind, IconUmbrella, IconThermometer,
        IconBook, IconSpade
    ];

    // Maps the named constants above to their Bootstrap Icons kebab-case file name. Kept minimal and
    // separate from the full catalogue so old serialized Icon values (from timers/alarms saved
    // before the full icon set existed) keep resolving to the exact same glyph.
    private static readonly Dictionary<string, string> LegacyDisplayNameToKebab = new(StringComparer.Ordinal)
    {
        [IconTimer] = "clock",
        [IconAlarm] = "alarm-fill",
        [IconOnTime] = "lightning-charge-fill",
        [IconHourglass] = "hourglass-split",
        [IconFire] = "fire",
        [IconShield] = "shield-fill",
        [IconWarning] = "exclamation-triangle-fill",
        [IconEye] = "eye-fill",
        [IconStar] = "star-fill",
        [IconMoon] = "moon-fill",
        [IconDice] = "dice-3-fill",
        [IconHeart] = "heart-fill",
        [IconGear] = "gear-fill",
        [IconKey] = "key-fill",
        [IconCalendar] = "calendar",
        [IconMap] = "map-fill",
        [IconCompass] = "compass-fill",
        [IconFlag] = "flag-fill",
        [IconLock] = "lock-fill",
        [IconUnlock] = "unlock-fill",
        [IconPerson] = "person-fill",
        [IconHouse] = "house-fill",
        [IconCloudMoon] = "cloud-moon-fill",
        [IconDroplet] = "droplet-fill",
        [IconTrophy] = "trophy-fill",
        [IconMusic] = "music-note-beamed",
        [IconCamera] = "camera-fill",
        [IconEnvelope] = "envelope-fill",
        [IconInfo] = "info-circle-fill",
        [IconQuestion] = "question-circle-fill",
        [IconExclamation] = "exclamation-circle-fill",
        [IconCheck] = "check-circle-fill",
        [IconCross] = "x-circle-fill",
        [IconPlus] = "plus-circle-fill",
        [IconVolume] = "volume-up-fill",
        [IconStopwatch] = "stopwatch-fill",
        [IconCup] = "cup-fill",
        [IconGlobe] = "globe",
        [IconPin] = "geo-alt-fill",
        [IconBuilding] = "building-fill",
        [IconDoor] = "door-closed-fill",
        [IconPuzzle] = "puzzle-fill",
        [IconPalette] = "palette-fill",
        [IconMagic] = "magic",
        [IconSnow] = "snow2",
        [IconWind] = "wind",
        [IconUmbrella] = "umbrella-fill",
        [IconThermometer] = "thermometer-half",
        [IconBook] = "book-fill",
        [IconSpade] = "suit-spade-fill"
    };

    private static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);
    private static Dictionary<string, string>? _iconPaths;
    private static Dictionary<string, string>? _displayNameToKebab;
    private static ObservableCollection<string>? _iconOptions;

    public static ObservableCollection<string> IconOptions => _iconOptions ??= BuildIconOptions();

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
            var value when GetDisplayNameToKebab().ContainsKey(value) => value,
            _ => IconTimer
        };
    }

    public static Geometry IconGeometry(string? icon)
    {
        var key = NormalizeIcon(icon);
        if (GeometryCache.TryGetValue(key, out var cached)) return cached;

        var paths = GetIconPaths();
        var kebab = GetDisplayNameToKebab().TryGetValue(key, out var found) ? found : "clock";
        var data = paths.TryGetValue(kebab, out var pathData) ? pathData : paths.GetValueOrDefault("clock", "");

        var geometry = Geometry.Parse(data);
        GeometryCache[key] = geometry;
        return geometry;
    }

    private static ObservableCollection<string> BuildIconOptions()
    {
        var map = GetDisplayNameToKebab();
        var legacySet = new HashSet<string>(LegacyOrder, StringComparer.Ordinal);
        var rest = map.Keys.Where(name => !legacySet.Contains(name)).OrderBy(name => name, StringComparer.Ordinal);

        var result = new ObservableCollection<string>(LegacyOrder);
        foreach (var name in rest) result.Add(name);
        return result;
    }

    private static Dictionary<string, string> GetDisplayNameToKebab()
    {
        if (_displayNameToKebab is not null) return _displayNameToKebab;

        var map = new Dictionary<string, string>(LegacyDisplayNameToKebab, StringComparer.Ordinal);
        var claimedKebabs = new HashSet<string>(LegacyDisplayNameToKebab.Values, StringComparer.Ordinal);
        foreach (var kebab in GetIconPaths().Keys)
        {
            if (claimedKebabs.Contains(kebab)) continue;
            map[$"Bootstrap: {ToTitleCase(kebab)}"] = kebab;
        }

        _displayNameToKebab = map;
        return map;
    }

    private static string ToTitleCase(string kebab)
    {
        return string.Join(' ', kebab
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static Dictionary<string, string> GetIconPaths()
    {
        if (_iconPaths is not null) return _iconPaths;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "BootstrapIcons", "bootstrap-icons.json");
        try
        {
            var json = File.ReadAllText(path);
            _iconPaths = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                         ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bootstrap icon catalogue {Path} could not be loaded", path);
            _iconPaths = new Dictionary<string, string>();
        }

        return _iconPaths;
    }
}
