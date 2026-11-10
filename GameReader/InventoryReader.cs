namespace ErRandoCompanion.GameReader;

/// <summary>
/// Everything the player holds, as item id (with its kind in the top four bits: 0 weapon, 1 armour, 2 talisman,
/// 4 goods, 8 ash of war) to quantity; and the same for the storage chest at sites of grace.
/// </summary>
public sealed record InventorySnapshot(IReadOnlyDictionary<uint, int> Held, IReadOnlyDictionary<uint, int> Stored);

/// <summary>
/// Reads the player's inventory and storage chest. The layouts come from fromsoftware-rs (Vincent Swarte; MIT/Apache,
/// non-AI-training licences). The inventory's place inside the player's data is found by its shape rather than a fixed
/// offset, and checked before it is trusted, so a game update that moves it makes this find nothing instead of reading
/// something else.
///
/// GameDataMan (a global) + 0x8 is the main player's PlayerGameData. Inside it, an inventory's item lists read:
///   u32 total capacity, u32 normal capacity, ptr normal list, u32 normal length,
///   u32 key capacity,   ptr key list,    u32 key length
/// and each list entry is 0x18 bytes: u32 handle, u32 item id, u32 quantity, u32 order, ... (an empty entry has id -1).
/// The storage chest has the same layout, behind a pointer in the player's data.
/// </summary>
public sealed class InventoryReader(IMemoryReader memory, long gameDataManAddress)
{
    private const int PlayerDataSearch = 0x1400;
    private const int EntrySize = 0x18;
    private const int MaxEntries = 5000;

    private long _player;
    private long _held;
    private long _stored;

    /// <summary>What the player holds now, or null while no character is loaded or the inventory cannot be found.</summary>
    public InventorySnapshot? Read()
    {
        if (!TryReadLong(gameDataManAddress, out var manager) || manager == 0 || !TryReadLong(manager + 0x8, out var player) || player == 0)
            return null;

        // Found once per character, as loading another save makes a new player data object.
        if (player != _player)
        {
            _player = player;
            (_held, _stored) = Locate(player);
        }
        if (_held == 0)
            return null;
        var held = ReadLists(_held);
        if (held is null)
        {
            _player = 0; // look again next time
            return null;
        }
        var stored = _stored != 0 ? ReadLists(_stored) : null;
        return new InventorySnapshot(held, stored ?? new Dictionary<uint, int>());
    }

    /// <summary>Where the item lists are: inline in the player's data (held), and behind a pointer (storage).</summary>
    private (long Held, long Stored) Locate(long player)
    {
        var bytes = new byte[PlayerDataSearch];
        if (!memory.TryRead(player, bytes, bytes.Length))
            return (0, 0);

        long held = 0;
        for (var offset = 0; offset + 0x28 <= bytes.Length; offset += 4)
        {
            if (LooksLikeLists(bytes, offset) && ReadLists(player + offset) is { Count: > 0 })
            {
                held = player + offset;
                break;
            }
        }

        // The storage chest is a separate object the player's data points to; its lists follow its vtable.
        long stored = 0;
        for (var offset = 0; offset + 8 <= bytes.Length && stored == 0; offset += 8)
        {
            var pointer = BitConverter.ToInt64(bytes, offset);
            if (pointer == 0 || pointer % 8 != 0 || pointer == player)
                continue;
            var candidate = new byte[0x28];
            if (memory.TryRead(pointer + 8, candidate, candidate.Length) && LooksLikeLists(candidate, 0) && pointer + 8 != held
                && ReadLists(pointer + 8) is not null)
            {
                stored = pointer + 8;
            }
        }
        return (held, stored);
    }

    /// <summary>The list header's numbers make sense: capacities in range, lengths within them, list pointers set.</summary>
    private static bool LooksLikeLists(byte[] bytes, int at)
    {
        var total = BitConverter.ToUInt32(bytes, at);
        var normalCapacity = BitConverter.ToUInt32(bytes, at + 4);
        var normalList = BitConverter.ToInt64(bytes, at + 8);
        var normalLength = BitConverter.ToUInt32(bytes, at + 0x10);
        var keyCapacity = BitConverter.ToUInt32(bytes, at + 0x14);
        var keyList = BitConverter.ToInt64(bytes, at + 0x18);
        var keyLength = BitConverter.ToUInt32(bytes, at + 0x20);
        return normalCapacity is >= 100 and <= MaxEntries && keyCapacity is >= 10 and <= 1000
            && total >= normalCapacity && total <= MaxEntries * 2
            && normalLength <= normalCapacity && keyLength <= keyCapacity
            && normalList > 0x10000 && normalList % 4 == 0 && keyList > 0x10000 && keyList % 4 == 0;
    }

    /// <summary>Both lists' items and quantities, or null if they cannot be read or hold something that is not an item.</summary>
    private Dictionary<uint, int>? ReadLists(long lists)
    {
        var header = new byte[0x28];
        if (!memory.TryRead(lists, header, header.Length) || !LooksLikeLists(header, 0))
            return null;
        var items = new Dictionary<uint, int>();
        foreach (var (listAt, lengthAt) in new[] { (8, 0x10), (0x18, 0x20) })
        {
            var list = BitConverter.ToInt64(header, listAt);
            var length = (int)BitConverter.ToUInt32(header, lengthAt);
            if (length == 0)
                continue;
            var entries = new byte[length * EntrySize];
            if (!memory.TryRead(list, entries, entries.Length))
                return null;
            for (var i = 0; i < length; i++)
            {
                var id = BitConverter.ToUInt32(entries, i * EntrySize + 4);
                var quantity = BitConverter.ToInt32(entries, i * EntrySize + 8);
                if (id == uint.MaxValue || id == 0 || quantity <= 0)
                    continue;
                if ((id >> 28) is not (0 or 1 or 2 or 4 or 8) || quantity > 100000)
                    return null; // not an item list after all
                items[id] = items.GetValueOrDefault(id) + quantity;
            }
        }
        return items;
    }

    private bool TryReadLong(long address, out long value)
    {
        var buffer = new byte[8];
        var ok = memory.TryRead(address, buffer, 8);
        value = BitConverter.ToInt64(buffer, 0);
        return ok;
    }
}
