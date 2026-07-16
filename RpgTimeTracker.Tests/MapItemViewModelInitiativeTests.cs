using System;
using RpgTimeTracker.Models;
using RpgTimeTracker.ViewModels;
using Xunit;

namespace RpgTimeTracker.Tests;

/// <summary>
///     Covers MapItemViewModel's initiative-order bookkeeping (#70) in isolation - the actual
///     turn-advance/skip/IsCurrentTurn logic lives in MainWindowViewModel.AdvanceInitiative, which
///     isn't unit-testable without constructing that class's real (parameterless but heavily
///     side-effecting: real settings file I/O, a real TCP listener) constructor - not something
///     any other test in this project does either. What's tested here is the part that IS pure:
///     keeping InitiativeCurrentIndex pointing at the same logical entry across a reorder/removal.
/// </summary>
public class MapItemViewModelInitiativeTests
{
    private static MapItemViewModel CreateMap()
    {
        return new MapItemViewModel(Guid.NewGuid(), "Test Map", 8, _ => { }, null);
    }

    private static InitiativeEntryViewModel AddEntry(MapItemViewModel map, string name)
    {
        return map.AddInitiativeEntry(TokenLinkKind.None, null, null, name, map.RemoveInitiativeEntry, null);
    }

    [Fact]
    public void AddInitiativeEntry_AppendsInOrder()
    {
        var map = CreateMap();
        AddEntry(map, "A");
        AddEntry(map, "B");

        Assert.Equal(2, map.InitiativeEntries.Count);
        Assert.Equal("A", map.InitiativeEntries[0].FreeformName);
        Assert.Equal("B", map.InitiativeEntries[1].FreeformName);
    }

    [Fact]
    public void MoveInitiativeEntryTo_KeepsCurrentIndexOnSameLogicalEntry_WhenAnEarlierEntryMovesPastIt()
    {
        var map = CreateMap();
        var a = AddEntry(map, "A");
        AddEntry(map, "B");
        AddEntry(map, "C");
        map.InitiativeCurrentIndex = 1; // "B" is current

        map.MoveInitiativeEntryTo(a, 2); // drag "A" (index 0) past "B"/"C"

        Assert.Equal("B", map.InitiativeEntries[map.InitiativeCurrentIndex].FreeformName);
    }

    [Fact]
    public void MoveInitiativeEntryTo_KeepsCurrentIndexOnSameLogicalEntry_WhenTheCurrentEntryItselfMoves()
    {
        var map = CreateMap();
        AddEntry(map, "A");
        var b = AddEntry(map, "B");
        AddEntry(map, "C");
        map.InitiativeCurrentIndex = 1; // "B" is current

        map.MoveInitiativeEntryTo(b, 0);

        Assert.Equal(0, map.InitiativeCurrentIndex);
        Assert.Equal("B", map.InitiativeEntries[map.InitiativeCurrentIndex].FreeformName);
    }

    [Fact]
    public void RemoveInitiativeEntry_BeforeCurrentIndex_ShiftsPointerBackToSameLogicalEntry()
    {
        var map = CreateMap();
        var a = AddEntry(map, "A");
        AddEntry(map, "B");
        AddEntry(map, "C");
        map.InitiativeCurrentIndex = 2; // "C" is current

        map.RemoveInitiativeEntry(a);

        Assert.Equal("C", map.InitiativeEntries[map.InitiativeCurrentIndex].FreeformName);
    }

    [Fact]
    public void RemoveInitiativeEntry_LastEntryWhileCurrent_ClampsIndexBackIntoRange()
    {
        var map = CreateMap();
        AddEntry(map, "A");
        var b = AddEntry(map, "B");
        map.InitiativeCurrentIndex = 1; // "B" (the last entry) is current

        map.RemoveInitiativeEntry(b);

        Assert.Equal(0, map.InitiativeCurrentIndex);
        Assert.Single(map.InitiativeEntries);
    }
}
