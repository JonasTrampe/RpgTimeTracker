using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RpgTimeTracker.Shared.Models;
using Serilog;

namespace RpgTimeTracker.Shared.Services;

/// <summary>
///     Loads CalendarDefinitions from JSON files - bundled predefined calendars (adapted from
///     Foundry VTT Simple Calendar's predefined-calendar format) plus GM-authored custom calendars,
///     following the same bundled-vs-custom split as ThemeDefinitionLoader. Unlike themes, a
///     calendar is a single JSON file (no accompanying images), so this loader is considerably
///     simpler.
/// </summary>
public static class CalendarDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Calendars authored/imported by the GM themselves - one JSON file per calendar.</summary>
    public static string CustomCalendarsDirectory =>
        Path.Combine(GetUserConfigDirectory(), "RpgTimeTracker", "Calendars");

    /// <summary>Calendars shipped with the app.</summary>
    public static string BundledCalendarsDirectory => Path.Combine(AppContext.BaseDirectory, "PredefinedCalendars");

    private static string GetUserConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) return appData;

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig)) return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : Path.Combine(home, ".config");
    }

    /// <summary>Bundled calendars first, then the GM's own - with the same Name, the GM's own wins (later in the list).</summary>
    public static List<LoadedCalendar> LoadAll()
    {
        var result = new List<LoadedCalendar>();
        CollectFrom(BundledCalendarsDirectory, true, result);
        CollectFrom(CustomCalendarsDirectory, false, result);
        return result;
    }

    private static void CollectFrom(string rootDir, bool isBundled, List<LoadedCalendar> result)
    {
        if (!Directory.Exists(rootDir)) return;

        foreach (var jsonPath in Directory.GetFiles(rootDir, "*.json"))
        {
            var loaded = TryLoad(jsonPath, isBundled);
            if (loaded is not null) result.Add(loaded.Value);
        }
    }

    /// <summary>Loads and validates a single calendar JSON file - used both by LoadAll's directory
    ///     scan and by the GM's "import calendar" file picker (so an invalid file is rejected with
    ///     the same rules either way).</summary>
    public static LoadedCalendar? TryLoad(string jsonPath, bool isBundled)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            var def = JsonSerializer.Deserialize<CalendarDefinition>(json, JsonOptions);
            if (def is null || string.IsNullOrWhiteSpace(def.Name))
            {
                Log.Warning("Calendar JSON without a valid Name skipped: {Path}", jsonPath);
                return null;
            }

            if (def.Months.Count == 0)
            {
                Log.Warning("Calendar JSON {Name} has no months, skipped: {Path}", def.Name, jsonPath);
                return null;
            }

            return new LoadedCalendar(def, jsonPath, isBundled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Calendar JSON could not be loaded: {Path}", jsonPath);
            return null;
        }
    }

    /// <summary>Resolves a stored/network-received calendar name against LoadAll() - case-insensitive.
    ///     Returns null if nothing matches (caller decides the fallback, e.g. Gregorian).</summary>
    public static LoadedCalendar? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (var loaded in LoadAll())
            if (string.Equals(loaded.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                return loaded;

        return null;
    }

    public readonly record struct LoadedCalendar(CalendarDefinition Definition, string FilePath, bool IsBundled);
}
