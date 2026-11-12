using System.Globalization;

namespace ErRandoCompanion.GameReader;

/// <summary>
/// A character loaded in the world, as the kill counter needs it. <paramref name="Handle"/> tells the same character
/// apart across polls; <paramref name="NpcParam"/> is its kind and drop table (NpcParam row); <paramref name="Model"/>
/// its model number (4300 for c4300), which names the kind.
/// </summary>
public readonly record struct LoadedEnemy(ulong Handle, int NpcParam, int Model, int Hp, int MaxHp, byte Team);

/// <summary>
/// Where WorldChrMan keeps its characters, and where a character keeps what the kill counter reads. The layout is the
/// one fromsoftware-rs documents (Vincent Swarte; MIT/Apache, non-AI training licences): WorldChrMan's array of 196
/// ChrSet pointers sits just before the main player pointer; a ChrSet has its capacity at +0x10 and its entries
/// (0x10 bytes, the ChrIns first) at +0x18; a ChrIns has its handle at +0x8, NpcParam id at +0x60, model number at
/// +0x64, type at +0x68, team at +0x6C and its modules at +0x190, whose first is the data module with HP at +0x138
/// and a pointer back to its ChrIns at +0x8.
/// </summary>
public sealed record EnemyOffsets(
    int ChrSets = 0x1DED8,
    int ChrSetCount = 196,
    int SetCapacity = 0x10,
    int SetEntries = 0x18,
    int EntrySize = 0x10,
    int Handle = 0x8,
    int NpcParam = 0x60,
    int Model = 0x64,
    int Type = 0x68,
    int Team = 0x6C,
    int Modules = 0x190,
    int DataOwner = 0x8,
    int Hp = 0x138)
{
    /// <summary>Characters of this type are enemies and NPCs (ChrType::Npc); players, ghosts and phantoms are not.</summary>
    public const int NpcType = 5;

    /// <summary>The defaults, with any hex values from Data/signatures.json's "enemies" section in their place.</summary>
    public static EnemyOffsets From(IReadOnlyDictionary<string, string>? text)
    {
        var o = new EnemyOffsets();
        if (text is null)
            return o;
        int Hex(string key, int fallback) =>
            text.TryGetValue(key, out var value) && int.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        return new EnemyOffsets(Hex("chrSets", o.ChrSets), Hex("chrSetCount", o.ChrSetCount), Hex("setCapacity", o.SetCapacity),
            Hex("setEntries", o.SetEntries), Hex("entrySize", o.EntrySize), Hex("handle", o.Handle), Hex("npcParam", o.NpcParam),
            Hex("model", o.Model), Hex("type", o.Type), Hex("team", o.Team), Hex("modules", o.Modules), Hex("dataOwner", o.DataOwner),
            Hex("hp", o.Hp));
    }
}

/// <summary>
/// Lists the enemies loaded in the world, with their HP, by walking WorldChrMan's character sets. Only reads; every
/// pointer is checked, and a character whose data module does not point back to it is skipped, so a wrong offset
/// gives an empty list rather than nonsense.
/// </summary>
public sealed class EnemyReader(IMemoryReader memory, long worldChrManPointerAddress, int playerInstanceOffset, EnemyOffsets offsets)
{
    private const int MaxCapacity = 10_000;
    private const int MaxEnemies = 4_000;
    private const int ChrHeader = 0x198;
    private readonly byte[] _eight = new byte[8];

    /// <summary>The enemies loaded now, or null when the character manager cannot be read (loading, or wrong offsets).</summary>
    public IReadOnlyList<LoadedEnemy>? Read()
    {
        if (!TryReadPointer(worldChrManPointerAddress, out var manager))
            return null;
        TryReadPointer(manager + playerInstanceOffset, out var player);
        var sets = new byte[offsets.ChrSetCount * 8];
        if (!memory.TryRead(manager + offsets.ChrSets, sets, sets.Length))
            return null;

        var enemies = new List<LoadedEnemy>();
        var seen = new HashSet<long>();
        var header = new byte[ChrHeader];
        var data = new byte[offsets.Hp + 8];
        for (var s = 0; s < offsets.ChrSetCount && enemies.Count < MaxEnemies; s++)
        {
            var set = BitConverter.ToInt64(sets, s * 8);
            if (set < 0x10000)
                continue;
            var setHeader = new byte[offsets.SetEntries + 8];
            if (!memory.TryRead(set, setHeader, setHeader.Length))
                continue;
            var capacity = BitConverter.ToInt32(setHeader, offsets.SetCapacity);
            var entries = BitConverter.ToInt64(setHeader, offsets.SetEntries);
            if (capacity is <= 0 or > MaxCapacity || entries < 0x10000)
                continue;
            var table = new byte[capacity * offsets.EntrySize];
            if (!memory.TryRead(entries, table, table.Length))
                continue;
            for (var e = 0; e < capacity; e++)
            {
                var chr = BitConverter.ToInt64(table, e * offsets.EntrySize);
                if (chr < 0x10000 || chr == player || !seen.Add(chr) || !memory.TryRead(chr, header, header.Length))
                    continue;
                if (BitConverter.ToInt32(header, offsets.Type) != EnemyOffsets.NpcType)
                    continue;
                var modules = BitConverter.ToInt64(header, offsets.Modules);
                if (modules < 0x10000 || !TryReadPointer(modules, out var dataModule)
                    || !memory.TryRead(dataModule, data, data.Length) || BitConverter.ToInt64(data, offsets.DataOwner) != chr)
                    continue;
                var maxHp = BitConverter.ToInt32(data, offsets.Hp + 4);
                if (maxHp <= 0)
                    continue;
                enemies.Add(new LoadedEnemy(BitConverter.ToUInt64(header, offsets.Handle), BitConverter.ToInt32(header, offsets.NpcParam),
                    BitConverter.ToInt32(header, offsets.Model), BitConverter.ToInt32(data, offsets.Hp), maxHp, header[offsets.Team]));
            }
        }
        return enemies;
    }

    private bool TryReadPointer(long address, out long value)
    {
        value = 0;
        if (!memory.TryRead(address, _eight, 8))
            return false;
        value = BitConverter.ToInt64(_eight, 0);
        return value >= 0x10000;
    }
}
