using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Navigation;

/// <summary>
/// The best way to a check: walking from where the player is, or warping to a found Site of Grace and walking from
/// there. A target inside a dungeon is reached through its entrance, then on inside. Each walk is measured in straight
/// lines between those points, so it is an estimate that knows about warping and dungeon entrances, not the terrain.
/// </summary>
/// <param name="Distance">The whole route's length in metres, with warping counted as <see cref="RoutePlanner.WarpCost"/>.</param>
/// <param name="WarpTo">The grace to warp to first, or null to walk from here.</param>
/// <param name="Walk">How far there is to walk: from here, or from the grace.</param>
public sealed record Route(double Distance, Grace? WarpTo, double Walk);

public static class RoutePlanner
{
    /// <summary>What a warp is worth in metres: the menus and the loading screen. Below this, walking wins.</summary>
    public const double WarpCost = 250;

    /// <summary>A height difference worth mentioning, in metres.</summary>
    public const double NotableRise = 15;

    /// <param name="fromHere">The way from the player, as the guide works it out; may be other realm or unknown.</param>
    /// <param name="graces">Every grace, and which the player has found; only found graces can be warped to.</param>
    /// <returns>The shortest route, or null when there is none (no position, and no found grace in the target's realm).</returns>
    public static Route? Plan(Guidance fromHere, MapPin target, IEnumerable<Grace> graces, IReadOnlySet<uint> found)
    {
        Route? best = null;
        var targetMap = MapBlock.Parse(target.MapId);
        var inDungeon = targetMap is { IsOpenWorld: false } && target.Entrance is not null;
        // Inside a dungeon, from its entrance to the check: the links between maps are only moves, so the gap between the
        // check's place on the open world and its entrance is the gap inside.
        var inside = inDungeon && target.World is { } w && target.Entrance is { } e ? Flat(w.X - e.X, w.Z - e.Z) : 0;

        if (fromHere.Mode is GuideMode.SameMap or GuideMode.Direct)
            best = new Route(fromHere.Distance, null, fromHere.Distance);
        else if (fromHere.Mode == GuideMode.ToEntrance)
            best = new Route(fromHere.Distance + inside, null, fromHere.Distance + inside);

        foreach (var grace in graces)
        {
            if (!found.Contains(grace.Flag))
                continue;
            double walk;
            if (inDungeon && target.Local is { } local && SameDungeon(grace.Map, targetMap!.Value))
                walk = Flat(grace.LocalX - local.X, grace.LocalZ - local.Z);
            else if (grace.Area != target.Area)
                continue;
            else if (inDungeon)
                walk = Flat(grace.X - target.Entrance!.X, grace.Z - target.Entrance.Z) + inside;
            else if (target.World is { } world)
                walk = Flat(grace.X - world.X, grace.Z - world.Z);
            else
                continue;
            var total = WarpCost + walk;
            if (best is null || total < best.Distance)
                best = new Route(total, grace, walk);
        }
        return best;
    }

    private static bool SameDungeon(MapBlock a, MapBlock b) =>
        !a.IsOpenWorld && a.Area == b.Area && a.GridX == b.GridX && a.GridZ == b.GridZ;

    private static double Flat(double dx, double dz) => Math.Sqrt(dx * dx + dz * dz);
}
