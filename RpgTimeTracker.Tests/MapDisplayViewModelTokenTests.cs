using System;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.ViewModels;
using Xunit;

namespace RpgTimeTracker.Tests;

public class MapDisplayViewModelTokenTests
{
    private static MapDisplayFloor CreateFloor(Guid floorId, string name = "Floor")
    {
        return new MapDisplayFloor { FloorId = floorId, Name = name };
    }

    [Fact]
    public void VisibleTokens_OnlyShowsTokensOnCurrentFloor()
    {
        var display = new MapDisplayViewModel();
        var floorA = Guid.NewGuid();
        var floorB = Guid.NewGuid();
        display.ShowMap("Test Map", [CreateFloor(floorA), CreateFloor(floorB)]);

        display.UpsertToken(new MapTokenSnapshotDto { Id = Guid.NewGuid(), FloorId = floorA, Name = "On A" });
        display.UpsertToken(new MapTokenSnapshotDto { Id = Guid.NewGuid(), FloorId = floorB, Name = "On B" });

        Assert.Single(display.VisibleTokens);
        Assert.Equal("On A", display.VisibleTokens[0].Name);
    }

    [Fact]
    public void NavigatingFloors_UpdatesVisibleTokens()
    {
        var display = new MapDisplayViewModel();
        var floorA = Guid.NewGuid();
        var floorB = Guid.NewGuid();
        display.ShowMap("Test Map", [CreateFloor(floorA), CreateFloor(floorB)]);
        display.UpsertToken(new MapTokenSnapshotDto { Id = Guid.NewGuid(), FloorId = floorB, Name = "On B" });

        Assert.Empty(display.VisibleTokens);

        display.NextFloorCommand.Execute(null);

        Assert.Single(display.VisibleTokens);
        Assert.Equal("On B", display.VisibleTokens[0].Name);
    }

    [Fact]
    public void UpsertToken_SameId_ReplacesExistingEntry()
    {
        var display = new MapDisplayViewModel();
        var floorId = Guid.NewGuid();
        display.ShowMap("Test Map", [CreateFloor(floorId)]);
        var tokenId = Guid.NewGuid();

        display.UpsertToken(new MapTokenSnapshotDto { Id = tokenId, FloorId = floorId, Name = "First" });
        display.UpsertToken(new MapTokenSnapshotDto { Id = tokenId, FloorId = floorId, Name = "Second" });

        Assert.Single(display.VisibleTokens);
        Assert.Equal("Second", display.VisibleTokens[0].Name);
    }

    [Fact]
    public void RemoveToken_RemovesFromVisibleTokens()
    {
        var display = new MapDisplayViewModel();
        var floorId = Guid.NewGuid();
        display.ShowMap("Test Map", [CreateFloor(floorId)]);
        var tokenId = Guid.NewGuid();
        display.UpsertToken(new MapTokenSnapshotDto { Id = tokenId, FloorId = floorId, Name = "Token" });

        display.RemoveToken(tokenId);

        Assert.Empty(display.VisibleTokens);
    }

    [Fact]
    public void HideMap_ClearsTokens()
    {
        var display = new MapDisplayViewModel();
        var floorId = Guid.NewGuid();
        display.ShowMap("Test Map", [CreateFloor(floorId)]);
        display.UpsertToken(new MapTokenSnapshotDto { Id = Guid.NewGuid(), FloorId = floorId, Name = "Token" });

        display.HideMap();

        Assert.Empty(display.VisibleTokens);
    }
}
