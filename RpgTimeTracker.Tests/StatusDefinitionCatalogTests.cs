using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services.Theming;

namespace RpgTimeTracker.Tests;

public class StatusDefinitionCatalogTests
{
    [Fact]
    public void GetActiveStatuses_falls_back_to_defaults_when_theme_defines_none()
    {
        var theme = new ThemeDefinitionDto { Statuses = null };

        var statuses = StatusDefinitionCatalog.GetActiveStatuses(theme);

        Assert.NotEmpty(statuses);
        Assert.Contains(statuses, s => s.SkipsInitiativeTurn);
    }

    [Fact]
    public void GetActiveStatuses_falls_back_to_defaults_when_theme_list_is_empty()
    {
        var theme = new ThemeDefinitionDto { Statuses = [] };

        var statuses = StatusDefinitionCatalog.GetActiveStatuses(theme);

        Assert.NotEmpty(statuses);
    }

    [Fact]
    public void GetActiveStatuses_uses_theme_defined_list_when_present()
    {
        var theme = new ThemeDefinitionDto
        {
            Statuses =
            [
                new StatusDefinitionDto { Id = "stunned", Name = "Stunned", SkipsInitiativeTurn = false },
                new StatusDefinitionDto { Id = "flatlined", Name = "Flatlined", SkipsInitiativeTurn = true }
            ]
        };

        var statuses = StatusDefinitionCatalog.GetActiveStatuses(theme);

        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, s => s.Id == "flatlined" && s.SkipsInitiativeTurn);
    }

    [Fact]
    public void Resolve_returns_null_for_null_or_empty_id()
    {
        Assert.Null(StatusDefinitionCatalog.Resolve(null, null));
        Assert.Null(StatusDefinitionCatalog.Resolve(null, ""));
    }

    [Fact]
    public void Resolve_finds_matching_status_by_id()
    {
        var theme = new ThemeDefinitionDto
        {
            Statuses = [new StatusDefinitionDto { Id = "wounded", Name = "Wounded" }]
        };

        var resolved = StatusDefinitionCatalog.Resolve(theme, "wounded");

        Assert.NotNull(resolved);
        Assert.Equal("Wounded", resolved!.Name);
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_id()
    {
        var theme = new ThemeDefinitionDto
        {
            Statuses = [new StatusDefinitionDto { Id = "wounded", Name = "Wounded" }]
        };

        Assert.Null(StatusDefinitionCatalog.Resolve(theme, "nonexistent"));
    }
}
