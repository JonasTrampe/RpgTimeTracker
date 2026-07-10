using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Tests;

public class FogMaskTests
{
    [Fact]
    public void CreateFullyHidden_has_every_cell_hidden()
    {
        var mask = FogMask.CreateFullyHidden(gridWidth: 10, gridHeight: 8, cellSizePx: 32);

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 10; x++)
            Assert.False(mask.IsRevealed(x, y));
    }

    [Fact]
    public void SetRevealed_toggles_only_the_targeted_cell()
    {
        var mask = FogMask.CreateFullyHidden(gridWidth: 5, gridHeight: 5, cellSizePx: 10);

        mask.SetRevealed(2, 3, true);

        Assert.True(mask.IsRevealed(2, 3));
        Assert.False(mask.IsRevealed(2, 2));
        Assert.False(mask.IsRevealed(3, 3));
    }

    [Fact]
    public void SetRevealed_can_re_hide_a_previously_revealed_cell()
    {
        var mask = FogMask.CreateFullyHidden(gridWidth: 5, gridHeight: 5, cellSizePx: 10);
        mask.SetRevealed(1, 1, true);

        mask.SetRevealed(1, 1, false);

        Assert.False(mask.IsRevealed(1, 1));
    }

    [Fact]
    public void Clone_is_independent_of_the_original()
    {
        var original = FogMask.CreateFullyHidden(gridWidth: 4, gridHeight: 4, cellSizePx: 16);
        original.SetRevealed(0, 0, true);

        var clone = original.Clone();
        clone.SetRevealed(1, 1, true);

        Assert.True(clone.IsRevealed(0, 0));
        Assert.True(clone.IsRevealed(1, 1));
        Assert.False(original.IsRevealed(1, 1));
    }

    [Fact]
    public void Reset_to_starting_is_a_clone_of_the_template_not_a_shared_reference()
    {
        // Models the "reset to starting" feature: CurrentFog = StartingFog.Clone().
        var startingTemplate = FogMask.CreateFullyHidden(gridWidth: 4, gridHeight: 4, cellSizePx: 16);
        startingTemplate.SetRevealed(0, 0, true);

        var currentFog = startingTemplate.Clone();
        currentFog.SetRevealed(2, 2, true);

        var resetFog = startingTemplate.Clone();

        Assert.True(resetFog.IsRevealed(0, 0));
        Assert.False(resetFog.IsRevealed(2, 2));
    }
}
