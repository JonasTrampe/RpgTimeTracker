using System;
using RpgTimeTracker.Models;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.ViewModels;
using Xunit;

namespace RpgTimeTracker.Tests;

public class MapTokenViewModelTests
{
    private static MapTokenViewModel CreateToken(TokenRevealMode revealMode,
        TokenLinkKind linkKind = TokenLinkKind.None, Guid? linkedId = null)
    {
        var token = new MapTokenViewModel(Guid.NewGuid(), Guid.NewGuid(), 0, 0, linkKind, linkedId,
            _ => { }, null)
        {
            RevealMode = revealMode
        };
        return token;
    }

    [Fact]
    public void AlwaysVisible_IsVisibleRegardlessOfCellState()
    {
        var token = CreateToken(TokenRevealMode.AlwaysVisible);

        Assert.True(token.IsVisibleToPlayers(cellRevealed: false));
        Assert.True(token.IsVisibleToPlayers(cellRevealed: true));
    }

    [Fact]
    public void GmOnly_IsNeverVisibleRegardlessOfCellState()
    {
        var token = CreateToken(TokenRevealMode.GmOnly);

        Assert.False(token.IsVisibleToPlayers(cellRevealed: false));
        Assert.False(token.IsVisibleToPlayers(cellRevealed: true));
    }

    [Fact]
    public void HiddenUntilRevealed_FollowsCellRevealState()
    {
        var token = CreateToken(TokenRevealMode.HiddenUntilRevealed);

        Assert.False(token.IsVisibleToPlayers(cellRevealed: false));
        Assert.True(token.IsVisibleToPlayers(cellRevealed: true));
    }

    [Fact]
    public void SettingIconImage_ClearsIconGlyph()
    {
        var token = CreateToken(TokenRevealMode.AlwaysVisible);
        token.IconGlyph = "Bootstrap: Map";

        token.IconImage = new MediaLibraryItemViewModel(Guid.NewGuid(), "Icon", "does-not-exist.mp4",
            MediaKind.Video, "video/mp4", false, _ => { }, _ => { });

        Assert.Null(token.IconGlyph);
        Assert.NotNull(token.IconImage);
    }

    [Fact]
    public void SettingIconGlyph_ClearsIconImage()
    {
        var token = CreateToken(TokenRevealMode.AlwaysVisible);
        token.IconImage = new MediaLibraryItemViewModel(Guid.NewGuid(), "Icon", "does-not-exist.mp4",
            MediaKind.Video, "video/mp4", false, _ => { }, _ => { });

        token.IconGlyph = "Bootstrap: Map";

        Assert.Null(token.IconImage);
        Assert.Equal("Bootstrap: Map", token.IconGlyph);
    }

    [Fact]
    public void Delete_InvokesRequestedCallback()
    {
        MapTokenViewModel? deleted = null;
        var token = new MapTokenViewModel(Guid.NewGuid(), Guid.NewGuid(), 0, 0, TokenLinkKind.None, null,
            t => deleted = t, null);

        token.DeleteCommand.Execute(null);

        Assert.Same(token, deleted);
    }
}
