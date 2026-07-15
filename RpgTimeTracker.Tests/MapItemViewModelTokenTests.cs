using System;
using RpgTimeTracker.Models;
using RpgTimeTracker.ViewModels;
using Xunit;

namespace RpgTimeTracker.Tests;

public class MapItemViewModelTokenTests
{
    private static MapItemViewModel CreateMap()
    {
        return new MapItemViewModel(Guid.NewGuid(), "Test Map", 32, _ => { }, null);
    }

    [Fact]
    public void AddOrSelectToken_FreeformTokens_AreNeverDeduplicated()
    {
        var map = CreateMap();
        var floorId = Guid.NewGuid();

        map.AddOrSelectToken(TokenLinkKind.None, null, floorId, 0, 0, _ => { }, null);
        map.AddOrSelectToken(TokenLinkKind.None, null, floorId, 10, 10, _ => { }, null);

        Assert.Equal(2, map.Tokens.Count);
    }

    [Fact]
    public void AddOrSelectToken_SameCharacterLink_MovesExistingTokenInstead()
    {
        var map = CreateMap();
        var characterId = Guid.NewGuid();
        var firstFloor = Guid.NewGuid();
        var secondFloor = Guid.NewGuid();

        var first = map.AddOrSelectToken(TokenLinkKind.Character, characterId, firstFloor, 0, 0, _ => { }, null);
        var second = map.AddOrSelectToken(TokenLinkKind.Character, characterId, secondFloor, 50, 60, _ => { }, null);

        Assert.Single(map.Tokens);
        Assert.Same(first, second);
        Assert.Equal(secondFloor, first.FloorId);
        Assert.Equal(50, first.X);
        Assert.Equal(60, first.Y);
    }

    [Fact]
    public void AddOrSelectToken_DifferentLinkedIds_CreateSeparateTokens()
    {
        var map = CreateMap();
        var floorId = Guid.NewGuid();

        map.AddOrSelectToken(TokenLinkKind.Character, Guid.NewGuid(), floorId, 0, 0, _ => { }, null);
        map.AddOrSelectToken(TokenLinkKind.Character, Guid.NewGuid(), floorId, 0, 0, _ => { }, null);

        Assert.Equal(2, map.Tokens.Count);
    }

    [Fact]
    public void AddOrSelectToken_SameIdButDifferentLinkKind_CreateSeparateTokens()
    {
        var map = CreateMap();
        var sharedId = Guid.NewGuid();
        var floorId = Guid.NewGuid();

        map.AddOrSelectToken(TokenLinkKind.Character, sharedId, floorId, 0, 0, _ => { }, null);
        map.AddOrSelectToken(TokenLinkKind.PointOfInterest, sharedId, floorId, 0, 0, _ => { }, null);

        Assert.Equal(2, map.Tokens.Count);
    }

    [Fact]
    public void RemoveToken_RemovesFromCollection()
    {
        var map = CreateMap();
        var token = map.AddOrSelectToken(TokenLinkKind.None, null, Guid.NewGuid(), 0, 0, _ => { }, null);

        map.RemoveToken(token);

        Assert.Empty(map.Tokens);
    }
}
