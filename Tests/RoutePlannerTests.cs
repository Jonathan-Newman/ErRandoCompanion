using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Navigation;

namespace ErRandoCompanion.Core.Tests;

public sealed class RoutePlannerTests
{
    private static readonly MapBlock Limgrave = new(60, 42, 36, 0);
    private static readonly MapBlock Castle = new(10, 0, 0, 0);

    private static readonly Grace[] Graces =
    [
        new("Church of Elleh", 1, Limgrave, 0, 0, 60, 1000, 1000),
        new("Far grace", 2, Limgrave, 0, 0, 60, 5000, 5000),
        new("Castle grace", 3, Castle, 100, 100, 60, 2000, 2000),
        new("Shadow grace", 4, new MapBlock(61, 45, 40, 0), 0, 0, 61, 0, 0),
    ];

    private static MapPin Outside(double x, double z, int area = 60) =>
        new("t", "m", 0, 0, false) { Area = area, World = new WorldPoint(x, null, z) };

    [Fact]
    public void WalksWhenThatIsShorterThanWarping()
    {
        var here = new Guidance(GuideMode.Direct, 100, null, 0);
        var route = RoutePlanner.Plan(here, Outside(1050, 1000), Graces, new HashSet<uint> { 1 })!;
        Assert.Null(route.WarpTo);
        Assert.Equal(100, route.Walk);
    }

    [Fact]
    public void WarpsToAFoundGraceWhenThatIsShorter()
    {
        var far = new Guidance(GuideMode.Direct, 4000, null, 0);
        var route = RoutePlanner.Plan(far, Outside(1100, 1000), Graces, new HashSet<uint> { 1, 2 })!;
        Assert.Equal("Church of Elleh", route.WarpTo!.Name);
        Assert.Equal(100, route.Walk, 3);
        Assert.Equal(RoutePlanner.WarpCost + 100, route.Distance, 3);

        // A grace not found yet is never suggested.
        Assert.Null(RoutePlanner.Plan(far, Outside(1100, 1000), Graces, new HashSet<uint> { 2 })!.WarpTo);
    }

    [Fact]
    public void ReachesTheOtherRealmOnlyThroughAFoundGraceThere()
    {
        var otherRealm = new Guidance(GuideMode.OtherRealm, 0, null, 0);
        Assert.Null(RoutePlanner.Plan(otherRealm, Outside(30, 40, 61), Graces, new HashSet<uint> { 1 }));
        var route = RoutePlanner.Plan(otherRealm, Outside(30, 40, 61), Graces, new HashSet<uint> { 1, 4 })!;
        Assert.Equal("Shadow grace", route.WarpTo!.Name);
        Assert.Equal(50, route.Walk, 3);
    }

    [Fact]
    public void ADungeonCheckIsReachedThroughItsEntranceThenInside()
    {
        // The check projects 300 m from the castle's entrance, so that is the walk inside.
        var inside = new MapPin("t", "m", 0, 0, false)
        {
            Area = 60, MapId = "m10_00_00_00", Local = new WorldPoint(100, null, 400),
            World = new WorldPoint(2300, null, 2000), Entrance = new WorldPoint(2000, null, 2000),
        };
        var toEntrance = new Guidance(GuideMode.ToEntrance, 200, null, 0);
        var walk = RoutePlanner.Plan(toEntrance, inside, Graces, new HashSet<uint>())!;
        Assert.Equal(500, walk.Walk, 3);

        // A grace inside the same castle is measured inside it.
        var route = RoutePlanner.Plan(new Guidance(GuideMode.ToEntrance, 3000, null, 0), inside, Graces, new HashSet<uint> { 3 })!;
        Assert.Equal("Castle grace", route.WarpTo!.Name);
        Assert.Equal(300, route.Walk, 3);
    }
}
