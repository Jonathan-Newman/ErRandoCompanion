using System.IO;
using System.Text.Json;
using ErRandoCompanion.Core.Json;

namespace ErRandoCompanion.Core.Models;

public sealed record MapLabel(string Name, double U, double V);

/// <summary>A point in metres. Y is height, and is null when only the spot on the map is known.</summary>
public sealed record WorldPoint(double X, double? Y, double Z);

public sealed record MapInfo(string Id, string Name, string Image, int Width, int Height, IReadOnlyList<MapLabel> Labels)
{
    /// <summary>The open world area this sheet draws: 60 for the Lands Between, 61 for the Land of Shadow.</summary>
    public int Area { get; init; }

    /// <summary>World x at the image's left edge, and world z at its top edge.</summary>
    public double WorldLeft { get; init; }
    public double WorldTop { get; init; }

    /// <summary>How many metres the image spans across and down.</summary>
    public double WorldWidth { get; init; }
    public double WorldHeight { get; init; }

    /// <summary>Where a world position falls on this sheet, as fractions of its width and height.</summary>
    public (double U, double V) ToUv(double x, double z) =>
        ((x - WorldLeft) / WorldWidth, (WorldTop - z) / WorldHeight);
}

/// <summary>Where a location sits on a map, as fractions of the image's width and height.</summary>
public sealed record MapPin(string Id, string Map, double U, double V, bool Underground)
{
    /// <summary>The open world area the check belongs to.</summary>
    public int Area { get; init; }

    /// <summary>
    /// The check in open world coordinates. Inside a dungeon this is where the game projects it onto
    /// the open world, which is not a place you can walk to; use <see cref="Entrance"/> to get there.
    /// </summary>
    public WorldPoint? World { get; init; }

    /// <summary>The map file the check is in, such as m10_00_00_00 for Stormveil Castle.</summary>
    public string? MapId { get; init; }

    /// <summary>The check in its own map's coordinates, which is what the game uses while you are in that map.</summary>
    public WorldPoint? Local { get; init; }

    /// <summary>For checks inside dungeons, the open world point the game links that dungeon to, usually its way in.</summary>
    public WorldPoint? Entrance { get; init; }
}

/// <summary>Everything the Map tab draws, from Data/map_data.json. It holds no seed-specific information.</summary>
public sealed record MapData(IReadOnlyList<MapInfo> Maps, IReadOnlyList<MapPin> Pins)
{
    /// <exception cref="IOException">The file is missing or unreadable.</exception>
    /// <exception cref="JsonException">The file is damaged.</exception>
    public static MapData Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), DataJsonContext.Default.MapData)
        ?? throw new JsonException($"{path} is empty.");
}
