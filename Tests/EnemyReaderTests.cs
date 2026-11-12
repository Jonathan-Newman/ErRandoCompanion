using ErRandoCompanion.GameReader;

namespace ErRandoCompanion.Tests;

public class EnemyReaderTests
{
    /// <summary>Memory made of blocks at fixed addresses; anything else is unreadable.</summary>
    private sealed class FakeMemory : IMemoryReader
    {
        private readonly List<(long Start, byte[] Bytes)> _blocks = [];

        public byte[] Block(long start, int size)
        {
            var bytes = new byte[size];
            _blocks.Add((start, bytes));
            return bytes;
        }

        public bool TryRead(long address, byte[] buffer, int count)
        {
            foreach (var (start, bytes) in _blocks)
            {
                if (address >= start && address + count <= start + bytes.Length)
                {
                    Array.Copy(bytes, address - start, buffer, 0, count);
                    return true;
                }
            }
            return false;
        }
    }

    private static void Long(byte[] block, int at, long value) => BitConverter.GetBytes(value).CopyTo(block, at);
    private static void Int(byte[] block, int at, int value) => BitConverter.GetBytes(value).CopyTo(block, at);

    [Fact]
    public void ReadsEnemiesFromTheCharacterSets()
    {
        var o = new EnemyOffsets();
        var memory = new FakeMemory();
        const long pointer = 0x100000, manager = 0x200000, set = 0x400000, entries = 0x500000;
        Long(memory.Block(pointer, 8), 0, manager);
        var man = memory.Block(manager, 0x1F000);
        Long(man, 0x1E508, 0x600000); // the main player
        Long(man, o.ChrSets + 3 * 8, set);
        var setBlock = memory.Block(set, 0x40);
        Int(setBlock, o.SetCapacity, 4);
        Long(setBlock, o.SetEntries, entries);
        var table = memory.Block(entries, 4 * o.EntrySize);

        // An Imp at half HP, a dead Imp, the player, and one whose data does not point back to it (a wrong layout).
        long[] chrs = [0x600000, 0x610000, 0x620000, 0x630000];
        for (var i = 0; i < chrs.Length; i++)
        {
            Long(table, i * o.EntrySize, chrs[i]);
            var chr = memory.Block(chrs[i], 0x200);
            Long(chr, o.Handle, 1000 + i);
            Int(chr, o.NpcParam, 43000);
            Int(chr, o.Model, 3080);
            Int(chr, o.Type, i == 0 ? 0 : EnemyOffsets.NpcType);
            Long(chr, o.Modules, chrs[i] + 0x1000);
            var modules = memory.Block(chrs[i] + 0x1000, 8);
            Long(modules, 0, chrs[i] + 0x2000);
            var data = memory.Block(chrs[i] + 0x2000, 0x200);
            Long(data, o.DataOwner, i == 3 ? 0x999999 : chrs[i]);
            Int(data, o.Hp, i == 2 ? 0 : 50);
            Int(data, o.Hp + 4, 100);
        }
        // The player (0x600000) is skipped as the main player; the last as its data belongs to someone else.
        var reader = new EnemyReader(memory, pointer, 0x1E508, o);
        var enemies = reader.Read();

        Assert.NotNull(enemies);
        Assert.Equal([new LoadedEnemy(1001, 43000, 3080, 50, 100, 0), new LoadedEnemy(1002, 43000, 3080, 0, 100, 0)], enemies);
    }

    [Fact]
    public void GivesNothingWhenTheManagerIsMissing()
    {
        Assert.Null(new EnemyReader(new FakeMemory(), 0x100000, 0x1E508, new EnemyOffsets()).Read());
    }
}
