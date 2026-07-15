using System;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.ViewModels;
using Xunit;

namespace RpgTimeTracker.Tests;

public class PointOfInterestLibraryItemViewModelTests
{
    private static PointOfInterestLibraryItemViewModel CreatePoi(string name = "Chest",
        Action<PointOfInterestLibraryItemViewModel>? onDeleteRequested = null)
    {
        return new PointOfInterestLibraryItemViewModel(Guid.NewGuid(), name,
            onDeleteRequested ?? (_ => { }), null);
    }

    private static MediaLibraryItemViewModel CreateMediaItem()
    {
        return new MediaLibraryItemViewModel(Guid.NewGuid(), "Icon", "does-not-exist.mp4",
            MediaKind.Video, "video/mp4", false, _ => { }, _ => { });
    }

    [Fact]
    public void SettingIconImage_ClearsIconGlyph()
    {
        var poi = CreatePoi();
        poi.IconGlyph = "Bootstrap: Map";

        poi.IconImage = CreateMediaItem();

        Assert.Null(poi.IconGlyph);
        Assert.NotNull(poi.IconImage);
    }

    [Fact]
    public void SettingIconGlyph_ClearsIconImage()
    {
        var poi = CreatePoi();
        poi.IconImage = CreateMediaItem();

        poi.IconGlyph = "Bootstrap: Map";

        Assert.Null(poi.IconImage);
        Assert.Equal("Bootstrap: Map", poi.IconGlyph);
    }

    [Theory]
    [InlineData("Ancient Chest", "AC")]
    [InlineData("Signpost", "S")]
    [InlineData("", "")]
    public void ResolvedInitials_DerivedFromName(string name, string expected)
    {
        var poi = CreatePoi(name);

        Assert.Equal(expected, poi.ResolvedInitials);
    }

    [Fact]
    public void Delete_InvokesRequestedCallback()
    {
        PointOfInterestLibraryItemViewModel? deleted = null;
        var poi = CreatePoi(onDeleteRequested: p => deleted = p);

        poi.DeleteCommand.Execute(null);

        Assert.Same(poi, deleted);
    }

    [Fact]
    public void BeginBulkLoad_SuppressesChangeNotifications()
    {
        var changeCount = 0;
        var poi = new PointOfInterestLibraryItemViewModel(Guid.NewGuid(), "Chest", _ => { },
            _ => changeCount++);

        poi.BeginBulkLoad();
        poi.Name = "Renamed";
        poi.Description = "Loot inside";
        poi.EndBulkLoad();

        Assert.Equal(0, changeCount);

        poi.Name = "Renamed again";
        Assert.Equal(1, changeCount);
    }
}
