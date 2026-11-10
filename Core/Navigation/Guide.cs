using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Navigation;

public enum GuideMode
{
    /// <summary>The player is in the target's own dungeon, so the way is straight to it.</summary>
    SameMap,

    /// <summary>Straight to the target in the open world, or between connected parts of the same kind of map.</summary>
    Direct,

    /// <summary>The target is inside a dungeon the player is not in, so the way is to that dungeon's entrance.</summary>
    ToEntrance,

    /// <summary>The target is in the other realm, the Lands Between or the Land of Shadow, so no direction applies.</summary>
    OtherRealm,

    /// <summary>The player's map or the target's position is not known well enough to point anywhere.</summary>
    Unknown,
}

/// <summary>
/// Which way to go. The offset is in metres from the player, along the game's axes: x and z across the ground, y up.
/// Only a translation separates one map's coordinates from another's, so the offset holds in whichever map the player is in.
/// </summary>
/// <param name="Dy">Null when the target's height is not known.</param>
public sealed record Guidance(GuideMode Mode, double Dx, double? Dy, double Dz)
{
    public static readonly Guidance None = new(GuideMode.Unknown, 0, null, 0);

    /// <summary>How far away the target is across the ground.</summary>
    public double Distance => Math.Sqrt(Dx * Dx + Dz * Dz);
}

public static class Guide
{
    public static Guidance For(MapBlock playerMap, double x, double y, double z, MapPin target, MapLinks links)
    {
        var targetMap = MapBlock.Parse(target.MapId);

        // In the target's own dungeon, its coordinates and the player's are the same map's.
        if (targetMap is { IsOpenWorld: false } && targetMap == playerMap && target.Local is { } local)
            return Offset(GuideMode.SameMap, (local.X, local.Y, local.Z), (x, y, z));

        var player = links.ToOpenWorld(playerMap, x, y, z);
        if (player is null || target.World is not { } world)
            return Guidance.None;
        if (player.Area != target.Area)
            return new Guidance(GuideMode.OtherRealm, 0, null, 0);

        // The underground rivers are one connected space laid out under the open world, so go straight there.
        // Any other dungeon, even another catacomb sharing the same area number, is reached through its entrance.
        var bothUnderground = targetMap?.Area == MapBlock.Underground && playerMap.Area == MapBlock.Underground;
        var direct = targetMap is null || targetMap.Value.IsOpenWorld || bothUnderground;
        if (direct || target.Entrance is not { } entrance)
            return Offset(GuideMode.Direct, (world.X, world.Y, world.Z), (player.X, player.Y, player.Z));
        return Offset(GuideMode.ToEntrance, (entrance.X, entrance.Y, entrance.Z), (player.X, player.Y, player.Z));
    }

    private static Guidance Offset(GuideMode mode, (double X, double? Y, double Z) to, (double X, double? Y, double Z) from) =>
        new(mode, to.X - from.X, to.Y - from.Y, to.Z - from.Z);
}
