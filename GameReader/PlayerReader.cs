using ErRandoCompanion.Core.Navigation;

namespace ErRandoCompanion.GameReader;

/// <summary>Where the player stands, in the coordinates of the map they are in.</summary>
public sealed record PlayerPosition(MapBlock Map, float X, float Y, float Z);

/// <summary>
/// Reads the player's map and position. The pointer path and offsets come from SoulSplitter
/// (GPL-3.0, https://github.com/FrankvdStam/SoulSplitter), which lists them per game version.
/// </summary>
public sealed class PlayerReader(IMemoryReader memory, long worldChrManPointerAddress, PlayerOffsets offsets)
{
    // Far beyond any map's size, so a larger value means the offsets do not fit this game version.
    private const float MaxCoordinate = 30000f;
    private readonly byte[] _buffer = new byte[12];

    /// <summary>
    /// Returns null while there is no player, such as on the title screen or during loading, and
    /// whenever what is read does not look like a position, so a wrong offset is never trusted.
    /// </summary>
    public PlayerPosition? Read()
    {
        if (!TryReadLong(worldChrManPointerAddress, out var manager) || manager == 0)
            return null;
        if (!TryReadLong(manager + offsets.PlayerInstance, out var player) || player == 0)
            return null;
        if (!memory.TryRead(player + offsets.MapId, _buffer, sizeof(uint)))
            return null;
        var map = MapBlock.FromPacked(BitConverter.ToUInt32(_buffer, 0));

        if (!memory.TryRead(player + offsets.Position, _buffer, 12))
            return null;
        var x = BitConverter.ToSingle(_buffer, 0);
        var y = BitConverter.ToSingle(_buffer, 4);
        var z = BitConverter.ToSingle(_buffer, 8);

        return IsPlausible(map, x, y, z) ? new PlayerPosition(map, x, y, z) : null;
    }

    private static bool IsPlausible(MapBlock map, float x, float y, float z) =>
        map.Area is >= 10 and < 100 && map.GridX < 100 && map.GridZ < 100 && map.Level < 100
        && float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
        && Math.Abs(x) < MaxCoordinate && Math.Abs(y) < MaxCoordinate && Math.Abs(z) < MaxCoordinate;

    private bool TryReadLong(long address, out long value)
    {
        var ok = memory.TryRead(address, _buffer, sizeof(long));
        value = BitConverter.ToInt64(_buffer, 0);
        return ok;
    }
}
