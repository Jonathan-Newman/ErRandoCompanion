using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Navigation;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

public class NavigationTests
{
    // A catacomb, m30_00_00_00, whose doorway at (10, 0, 20) is the open world point (100, 50, 200) in tile m60_40_36.
    // A second row gives a second doorway at (-90, 0, 120), the same offset seen from another point.
    private static readonly MapLinks Links = new(
    [
        new MapLinkRow(new MapTile(30, 0, 0), 10, 0, 20, new MapTile(60, 40, 36), 100, 50, 200),
        new MapLinkRow(new MapTile(30, 0, 0), -90, 0, 120, new MapTile(60, 40, 36), 0, 50, 300),
        new MapLinkRow(new MapTile(12, 1, 0), 0, 0, 0, new MapTile(60, 40, 36), 0, -500, 0),
        new MapLinkRow(new MapTile(12, 2, 0), 0, 0, 0, new MapTile(60, 40, 36), 1000, -500, 0),
    ]);

    private static readonly MapBlock Catacomb = new(30, 0, 0, 0);
    private static readonly MapBlock Overworld = new(60, 40, 36, 0);

    [Fact]
    public void OpenWorld_AddsTheTileCorner()
    {
        var position = Links.ToOpenWorld(Overworld, 5, 7, -3)!;
        Assert.Equal((60, 40 * 256 + 5.0, 7.0, 36 * 256 - 3.0), (position.Area, position.X, position.Y, position.Z));
        Assert.Null(position.Entrance);
    }

    [Fact]
    public void OpenWorld_LargerTilesAreCentredAndAlternatesAreNormalSize()
    {
        // A level 1 tile spans two normal tiles, so its centre is half a normal tile past the first one's.
        Assert.Equal(20 * 2 * 256 + 128.0, Links.ToOpenWorld(new MapBlock(60, 20, 18, 1), 0, 0, 0)!.X);
        Assert.Equal(44 * 256.0, Links.ToOpenWorld(new MapBlock(61, 44, 45, 10), 0, 0, 0)!.X);
    }

    [Fact]
    public void Dungeon_IsCarriedToTheOpenWorld_WithItsNearestDoorway()
    {
        var position = Links.ToOpenWorld(Catacomb, 30, 1, 40)!;

        // Offset from the first row: +90 across, +50 up, +180 along.
        Assert.Equal((60, 40 * 256 + 120.0, 51.0, 36 * 256 + 220.0), (position.Area, position.X, position.Y, position.Z));
        Assert.Equal((40 * 256 + 100.0, 50.0, 36 * 256 + 200.0), position.Entrance);
    }

    [Fact]
    public void UnlinkedMap_HasNoOpenWorldPosition()
    {
        Assert.Null(Links.ToOpenWorld(new MapBlock(11, 10, 0, 0), 0, 0, 0));
    }

    private static MapPin Pin(string mapId, WorldPoint world, WorldPoint? local = null, WorldPoint? entrance = null, int area = 60) =>
        new("target", "base", 0, 0, false) { Area = area, MapId = mapId, World = world, Local = local, Entrance = entrance };

    [Fact]
    public void InTheTargetsDungeon_GoesStraightToIt()
    {
        var target = Pin("m30_00_00_00", new WorldPoint(0, 0, 0), local: new WorldPoint(50, 4, 60), entrance: new WorldPoint(9, 9, 9));
        var guide = Guide.For(Catacomb, 20, 1, 30, target, Links);

        Assert.Equal(GuideMode.SameMap, guide.Mode);
        Assert.Equal((30.0, 3.0, 30.0), (guide.Dx, guide.Dy, guide.Dz));
    }

    [Fact]
    public void InTheOpenWorld_GoesToADungeonsEntrance()
    {
        var target = Pin("m30_00_00_00", new WorldPoint(10360, 51, 9450), entrance: new WorldPoint(10340, 50, 9416));
        var guide = Guide.For(Overworld, 0, 50, 0, target, Links);

        Assert.Equal(GuideMode.ToEntrance, guide.Mode);
        Assert.Equal((10340.0 - 40 * 256, 0.0, 9416.0 - 36 * 256), (guide.Dx, guide.Dy, guide.Dz));
        Assert.Equal(Math.Sqrt(100 * 100 + 200 * 200), guide.Distance, 6);
    }

    [Fact]
    public void FromAnotherDungeon_StillGoesToTheEntrance()
    {
        var target = Pin("m30_01_00_00", new WorldPoint(10500, 0, 9500), entrance: new WorldPoint(10400, 60, 9400));
        Assert.Equal(GuideMode.ToEntrance, Guide.For(Catacomb, 0, 0, 0, target, Links).Mode);
    }

    [Fact]
    public void Underground_GoesStraightAcrossConnectedRivers()
    {
        var target = Pin("m12_02_00_00", new WorldPoint(40 * 256 + 1000, -500, 36 * 256), entrance: new WorldPoint(0, 0, 0));
        var guide = Guide.For(new MapBlock(12, 1, 0, 0), 0, 0, 0, target, Links);

        Assert.Equal(GuideMode.Direct, guide.Mode);
        Assert.Equal((1000.0, 0.0, 0.0), (guide.Dx, guide.Dy, guide.Dz));
    }

    [Fact]
    public void OpenWorldTarget_WithUnknownHeight_GivesNoHeightDifference()
    {
        var target = Pin("m60_41_36_00", new WorldPoint(41 * 256, null, 36 * 256));
        var guide = Guide.For(Overworld, 0, 10, 0, target, Links);

        Assert.Equal(GuideMode.Direct, guide.Mode);
        Assert.Equal(256.0, guide.Dx);
        Assert.Null(guide.Dy);
    }

    [Fact]
    public void OtherRealm_AndUnknownPlaces_GiveNoDirection()
    {
        var shadow = Pin("m61_45_41_00", new WorldPoint(11520, 0, 10496), area: 61);
        Assert.Equal(GuideMode.OtherRealm, Guide.For(Overworld, 0, 0, 0, shadow, Links).Mode);
        Assert.Equal(GuideMode.Unknown, Guide.For(new MapBlock(11, 10, 0, 0), 0, 0, 0, shadow, Links).Mode);
    }
}
