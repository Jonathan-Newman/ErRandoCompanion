using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Navigation;

/// <summary>A position in one of the two open worlds. Y is height, and is null when it is not known.</summary>
/// <param name="Entrance">For a position inside a dungeon, the open world point the game links that dungeon to.</param>
public sealed record OpenWorldPosition(int Area, double X, double? Y, double Z, (double X, double? Y, double Z)? Entrance = null);

/// <summary>
/// Converts positions in any map to open world coordinates, the same way tools/build_map_data.py
/// placed every check: open world tiles are 256 metres square, and dungeons are walked through the
/// game's own table of linked points until an open world tile is reached.
/// </summary>
public sealed class MapLinks
{
    private const double Tile = 256;
    private readonly Dictionary<MapTile, Dictionary<MapTile, Link>> _links = [];

    public MapLinks(IEnumerable<MapLinkRow> rows)
    {
        foreach (var row in rows)
        {
            Add(row.From, (row.FromX, row.FromY, row.FromZ), row.To, (row.ToX, row.ToY, row.ToZ));
            Add(row.To, (row.ToX, row.ToY, row.ToZ), row.From, (row.FromX, row.FromY, row.FromZ));
        }
    }

    /// <summary>Returns null for a map that nothing links to the open world, such as Roundtable Hold.</summary>
    public OpenWorldPosition? ToOpenWorld(MapBlock map, double x, double? y, double z)
    {
        if (map.IsOpenWorld)
        {
            // Levels of 10 and up are alternate versions of a normal tile, not larger ones.
            var level = map.Level >= 10 ? 0 : map.Level;
            return new OpenWorldPosition(map.Area, TileCentre(map.GridX, level) + x, y, TileCentre(map.GridZ, level) + z);
        }

        // Breadth first, so the result matches the build script's, which took the first route it found.
        var start = new MapTile(map.Area, map.GridX, map.GridZ);
        var queue = new Queue<(MapTile Tile, double X, double? Y, double Z, IReadOnlyList<(float X, float Y, float Z)> Points)>();
        queue.Enqueue((start, x, y, z, []));
        var seen = new HashSet<MapTile> { start };
        while (queue.Count > 0)
        {
            var (tile, cx, cy, cz, points) = queue.Dequeue();
            if (tile.Area is MapBlock.LandsBetween or MapBlock.LandOfShadow)
            {
                var wx = tile.X * Tile + cx;
                var wz = tile.Z * Tile + cz;
                var entrance = points
                    .Select(p => (X: tile.X * Tile + p.X, Y: (double?)p.Y, Z: tile.Z * Tile + p.Z))
                    .MinBy(p => (p.X - wx) * (p.X - wx) + (p.Z - wz) * (p.Z - wz));
                return new OpenWorldPosition(tile.Area, wx, cy, wz, entrance);
            }

            foreach (var (next, link) in _links.GetValueOrDefault(tile) ?? [])
            {
                if (seen.Add(next))
                    queue.Enqueue((next, cx + link.Dx, cy + link.Dy, cz + link.Dz, link.Points));
            }
        }
        return null;
    }

    private static double TileCentre(int index, int level)
    {
        var scale = 1 << level;
        return (index * scale + (scale - 1) / 2.0) * Tile;
    }

    // The first row between two tiles sets the offset, as in the build script; every row adds a shared point.
    private void Add(MapTile from, (float X, float Y, float Z) fromPoint, MapTile to, (float X, float Y, float Z) toPoint)
    {
        if (!_links.TryGetValue(from, out var targets))
            _links[from] = targets = [];
        if (targets.TryGetValue(to, out var link))
            link.Points.Add(toPoint);
        else
            targets[to] = new Link(toPoint.X - fromPoint.X, toPoint.Y - fromPoint.Y, toPoint.Z - fromPoint.Z, [toPoint]);
    }

    private sealed record Link(double Dx, double Dy, double Dz, List<(float X, float Y, float Z)> Points);
}
