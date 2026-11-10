using System.Globalization;

namespace ErRandoCompanion.Core.Navigation;

/// <summary>
/// One of the game's map files, such as m60_42_36_00: an area, a grid square, and a level.
/// The game packs it into one number with the area in the highest byte.
/// </summary>
public readonly record struct MapBlock(byte Area, byte GridX, byte GridZ, byte Level)
{
    public const int LandsBetween = 60;
    public const int LandOfShadow = 61;

    /// <summary>Siofra, Ainsel, Nokron, Deeproot and the rest: separate maps that sit beneath the open world.</summary>
    public const int Underground = 12;

    /// <summary>The Lands Between or the Land of Shadow, rather than a dungeon or other separate map.</summary>
    public bool IsOpenWorld => Area is LandsBetween or LandOfShadow;

    public static MapBlock FromPacked(uint packed) =>
        new((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

    /// <summary>Reads a name like m60_42_36_00, returning null for anything else.</summary>
    public static MapBlock? Parse(string? name)
    {
        var parts = name is { Length: 12 } && name[0] == 'm' ? name[1..].Split('_') : null;
        if (parts is not { Length: 4 })
            return null;

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return new MapBlock(bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    public override string ToString() => $"m{Area:00}_{GridX:00}_{GridZ:00}_{Level:00}";
}
