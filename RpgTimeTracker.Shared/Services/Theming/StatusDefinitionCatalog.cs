using System.Collections.Generic;
using System.Linq;
using RpgTimeTracker.Shared.Models.Theming;

namespace RpgTimeTracker.Shared.Services.Theming;

/// <summary>
///     Resolves the GM-configurable Status vocabulary for the active Theme (see
///     ThemeDefinitionDto.Statuses), falling back to a small built-in default list for any Theme
///     that doesn't define its own - so the feature isn't broken for every existing bundled Design
///     out of the box. See design-decisions.md's "Status is GM-configurable data tied to the
///     active Theme" entry for why this isn't a fixed enum.
/// </summary>
public static class StatusDefinitionCatalog
{
    private static readonly IReadOnlyList<StatusDefinitionDto> DefaultStatuses =
    [
        new() { Id = "healthy", Name = "Healthy", TintColorHex = "#FFFFFFFF", SkipsInitiativeTurn = false },
        new() { Id = "down", Name = "Down", TintColorHex = "#FFFFA500", SkipsInitiativeTurn = false },
        new() { Id = "dead", Name = "Dead", TintColorHex = "#FF808080", SkipsInitiativeTurn = true }
    ];

    public static IReadOnlyList<StatusDefinitionDto> GetActiveStatuses(ThemeDefinitionDto? activeTheme)
    {
        return activeTheme?.Statuses is { Count: > 0 } statuses ? statuses : DefaultStatuses;
    }

    public static StatusDefinitionDto? Resolve(ThemeDefinitionDto? activeTheme, string? statusId)
    {
        if (string.IsNullOrEmpty(statusId)) return null;

        return GetActiveStatuses(activeTheme).FirstOrDefault(s => s.Id == statusId);
    }
}
