using System.IO;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Navigation;

/// <summary>
/// A Site of Grace. <see cref="X"/> and <see cref="Z"/> are where it is reached from the open world: the grace itself
/// outside, or its dungeon's way in. The same in every seed, so it says nothing about the randomization.
/// </summary>
/// <param name="Area">The open world it belongs to: 60 for the Lands Between, 61 for the Land of Shadow.</param>
public sealed record Grace(string Name, uint Flag, MapBlock Map, double LocalX, double LocalZ, int Area, double X, double Z)
{
    /// <summary>diste\Names\BonfireWarpParam.txt: "100002 Castleward Tunnel" lines.</summary>
    public static IReadOnlyDictionary<int, string> LoadNames(string randomizerDirectory)
    {
        var path = Path.Combine(randomizerDirectory, "diste", "Names", "BonfireWarpParam.txt");
        var names = new Dictionary<int, string>();
        if (!File.Exists(path))
            return names;
        foreach (var line in File.ReadLines(path))
        {
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line.AsSpan(0, space), out var id))
                names.TryAdd(id, line[(space + 1)..].Trim());
        }
        return names;
    }

    /// <summary>The graces that have a name and can be placed in an open world; Roundtable Hold, for one, cannot.</summary>
    public static IReadOnlyList<Grace> Build(IEnumerable<GraceRow> rows, IReadOnlyDictionary<int, string> names, MapLinks links)
    {
        var graces = new List<Grace>();
        foreach (var row in rows)
        {
            if (!names.TryGetValue(row.RowId, out var name) || name.Length == 0)
                continue;
            var map = new MapBlock(row.Tile.Area, row.Tile.X, row.Tile.Z, 0);
            if (links.ToOpenWorld(map, row.X, row.Y, row.Z) is not { } world)
                continue;
            var (x, z) = map.IsOpenWorld || world.Entrance is not { } entrance ? (world.X, world.Z) : (entrance.X, entrance.Z);
            graces.Add(new Grace(name, row.Flag, map, row.X, row.Z, world.Area, x, z));
        }
        return graces;
    }

    /// <summary>
    /// The found grace closest to a check, and how far it is from it in metres. Inside the check's own dungeon,
    /// distance is measured in the dungeon; anywhere else, between the ways in from the open world.
    /// </summary>
    public static (Grace Grace, double Distance)? Nearest(IEnumerable<Grace> graces, IReadOnlySet<uint> found, MapPin target)
    {
        var targetMap = MapBlock.Parse(target.MapId);
        var inDungeon = targetMap is { IsOpenWorld: false };
        var outside = inDungeon && target.Entrance is { } entrance ? entrance : target.World;

        (Grace, double)? best = null;
        foreach (var grace in graces)
        {
            if (!found.Contains(grace.Flag))
                continue;
            double distance;
            if (inDungeon && target.Local is { } local && SameDungeon(grace.Map, targetMap!.Value))
                distance = Math.Sqrt(Square(grace.LocalX - local.X) + Square(grace.LocalZ - local.Z));
            else if (outside is not null && grace.Area == target.Area)
                distance = Math.Sqrt(Square(grace.X - outside.X) + Square(grace.Z - outside.Z));
            else
                continue;
            if (best is null || distance < best.Value.Item2)
                best = (grace, distance);
        }
        return best;
    }

    private static bool SameDungeon(MapBlock a, MapBlock b) =>
        !a.IsOpenWorld && a.Area == b.Area && a.GridX == b.GridX && a.GridZ == b.GridZ;

    private static double Square(double v) => v * v;
}

/// <summary>Which graces this character has found. The flags are read a few at a time, as they only ever turn on.</summary>
public sealed class GraceTracker(IReadOnlyList<Grace> graces)
{
    private readonly HashSet<uint> _found = [];
    private readonly uint[] _all = graces.Select(g => g.Flag).Distinct().ToArray();

    public IReadOnlyList<Grace> Graces => graces;
    public IReadOnlySet<uint> Found => _found;

    /// <summary>The grace flags not seen on yet.</summary>
    public IEnumerable<uint> Pending => _all.Where(f => !_found.Contains(f));

    /// <summary>Takes in a poll's flags; returns whether a grace was found.</summary>
    public bool Update(IReadOnlySet<uint> flagsOn)
    {
        var changed = false;
        foreach (var flag in _all)
        {
            if (flagsOn.Contains(flag))
                changed |= _found.Add(flag);
        }
        return changed;
    }

    /// <summary>For another character: its graces are read afresh.</summary>
    public void Reset() => _found.Clear();
}
