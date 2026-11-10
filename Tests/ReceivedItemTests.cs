using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.GameReader;

namespace ErRandoCompanion.Core.Tests;

public sealed class ReceivedItemTests
{
    private const uint Uchigatana = 0x00000000 | 9000000;   // a weapon
    private const uint GoldenRune = 0x40000000 | 2900;      // goods
    private const uint Talisman = 0x20000000 | 1000;        // a talisman

    [Fact]
    public void FindsTheInventoryAndStorageChestByTheirShape()
    {
        var memory = new Memory();
        const long dataMan = 0x1000, manager = 0x2000, player = 0x3000, held = player + 0x5B8, chest = 0x8000;
        const long normalList = 0x1100000, keyList = 0x1200000, chestNormal = 0x1300000, chestKey = 0x1400000;
        memory.Long(dataMan, manager);
        memory.Long(manager + 0x8, player);
        memory.Fill(player, 0x1400);
        Lists(memory, held, normalList, keyList, [(Uchigatana, 1), (GoldenRune, 5)], [(Talisman, 1)]);
        memory.Long(player + 0x900, chest);                      // the storage chest, behind a pointer
        memory.Fill(chest, 0x30);
        Lists(memory, chest + 8, chestNormal, chestKey, [(GoldenRune, 20)], []);

        var inventory = new InventoryReader(memory, dataMan).Read();
        Assert.NotNull(inventory);
        Assert.Equal(1, inventory.Held[Uchigatana]);
        Assert.Equal(5, inventory.Held[GoldenRune]);
        Assert.Equal(1, inventory.Held[Talisman]);
        Assert.Equal(20, inventory.Stored[GoldenRune]);
    }

    [Fact]
    public void FindsNothingRatherThanReadingSomethingElse()
    {
        var memory = new Memory();
        memory.Long(0x1000, 0x2000);
        memory.Long(0x2008, 0x3000);
        memory.Fill(0x3000, 0x1400);
        // Numbers shaped like a list header, but the entries are not items.
        Lists(memory, 0x3100, 0x1100000, 0x1200000, [(0x7000_0001u, 1)], []);
        Assert.Null(new InventoryReader(memory, 0x1000).Read());
    }

    [Fact]
    public void CountsGainsButNotItemsTakenFromTheChest()
    {
        var received = new ReceivedItems();
        Dictionary<uint, int> Held(int runes, int swords) => new() { [GoldenRune] = runes, [Uchigatana] = swords };
        Dictionary<uint, int> Stored(int runes) => new() { [GoldenRune] = runes };

        Assert.Empty(received.Compare(Held(5, 0), Stored(20)));            // the first look is only a starting point
        Assert.Equal([(Uchigatana, 1)], received.Compare(Held(5, 1), Stored(20)));
        Assert.Empty(received.Compare(Held(15, 1), Stored(10)));           // ten runes out of the chest
        Assert.Equal([(GoldenRune, 2)], received.Compare(Held(17, 1), Stored(10)));
        Assert.Empty(received.Compare(Held(10, 1), Stored(10)));           // using items is not receiving them

        received.Reset();
        Assert.Empty(received.Compare(Held(99, 9), Stored(0)));            // a fresh start after leaving the world
    }

    [Fact]
    public void LogsReceivedItemsThatNoPickupExplains()
    {
        var state = new TrackerState { PlayedSeconds = 60 };
        var names = new Dictionary<(int, int), string> { [(2, 9000000)] = "Uchigatana", [(1, 2900)] = "Golden Rune [2]" };
        var now = new DateTime(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc);
        var explained = PickupLog.Given([new FoundItem { Items = ["Golden Rune [2] x3"] }]);

        var added = PickupLog.RecordReceived(state, [(Uchigatana, 1), (GoldenRune, 4)], names, explained, now, "Limgrave - Agheel Lake");

        Assert.Equal(2, added);
        Assert.Equal(["Uchigatana"], state.Found[0].Items);
        Assert.Equal(["Golden Rune [2]"], state.Found[1].Items);   // four arrived, three came from the logged pickup
        Assert.All(state.Found, f => Assert.Equal(FoundItem.DropOrOther, f.Source));
        Assert.Equal("Received in Limgrave - Agheel Lake", state.Found[0].Where);
        Assert.Equal("Weapons", state.Found[0].Kinds[0]);
    }

    private static void Lists(Memory memory, long at, long normal, long key, (uint Id, int Count)[] items, (uint Id, int Count)[] keys)
    {
        memory.Int(at, 3000);
        memory.Int(at + 4, 2688);
        memory.Long(at + 8, normal);
        memory.Int(at + 0x10, items.Length);
        memory.Int(at + 0x14, 384);
        memory.Long(at + 0x18, key);
        memory.Int(at + 0x20, keys.Length);
        memory.Fill(at + 0x24, 4);
        Entries(memory, normal, items);
        Entries(memory, key, keys);
    }

    private static void Entries(Memory memory, long at, (uint Id, int Count)[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            memory.Fill(at + i * 0x18, 0x18);
            memory.Int(at + i * 0x18 + 4, unchecked((int)items[i].Id));
            memory.Int(at + i * 0x18 + 8, items[i].Count);
        }
    }

    private sealed class Memory : IMemoryReader
    {
        private readonly Dictionary<long, byte> _bytes = [];

        public void Fill(long at, int count)
        {
            for (var i = 0; i < count; i++)
                _bytes.TryAdd(at + i, 0);
        }

        public void Int(long at, int value) => Write(at, BitConverter.GetBytes(value));
        public void Long(long at, long value) => Write(at, BitConverter.GetBytes(value));

        public bool TryRead(long address, byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!_bytes.TryGetValue(address + i, out buffer[i]))
                    return false;
            }
            return true;
        }

        private void Write(long at, byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
                _bytes[at + i] = bytes[i];
        }
    }
}
