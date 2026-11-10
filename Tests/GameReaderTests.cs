using ErRandoCompanion.GameReader;

namespace ErRandoCompanion.Core.Tests;

public class GameReaderTests
{
    [Fact]
    public void Find_HonoursWildcards()
    {
        byte[] memory = [0x00, 0x11, 0xAA, 0x33, 0x44];
        Assert.Equal(1, SignatureScanner.Find(memory, "11 ? 33"));
        Assert.Equal(-1, SignatureScanner.Find(memory, "11 ? 99"));
        Assert.Equal(-1, SignatureScanner.Find(memory, "44 ? ?"));
    }

    [Fact]
    public void ResolveAddress_FollowsTheRelativeOperand()
    {
        // At index 2: a 3-byte opcode, then a relative address of 0x10 ending the instruction at +7.
        byte[] memory = [0, 0, 0x4C, 0x8B, 0x25, 0x10, 0, 0, 0, 0xFF];
        var address = SignatureScanner.ResolveAddress(memory, 0x1000, new Signature("4c 8b 25 ? ? ? ? ff", 3, 7));
        Assert.Equal(0x1000 + 2 + 7 + 0x10, address);
    }

    [Fact]
    public void ResolveAddress_ReturnsNullWithoutAMatch()
    {
        Assert.Null(SignatureScanner.ResolveAddress(new byte[8], 0, new Signature("4c 8b 25 ? ? ? ?", 3, 7)));
    }

    /// <summary>
    /// A miniature of the game's flag manager: one tree node for category 5 and a bit block.
    /// With a divisor of 1000, flag 5012 is category 5, bit 12: byte 1, mask 0x08.
    /// </summary>
    private static FakeMemory BuildManager(int blockKind = 3, bool blockIsZero = false)
    {
        const long pointer = 0x100, manager = 0x200, head = 0x300, node = 0x400, block = 0x600;
        var memory = new FakeMemory();
        memory.WriteLong(pointer, manager);
        memory.WriteInt(manager + 0x1C, 1000);
        memory.WriteLong(manager + 0x38, head);

        // Nodes are whole objects in the game, and the reader takes each in one read, so fill them in.
        for (var i = 0; i < 0x38; i++)
        {
            memory.WriteByte(head + i, 0);
            memory.WriteByte(node + i, 0);
        }

        // As in a C++ std::map, the head points at the root, and every empty child points back at the head.
        memory.WriteLong(head + 0x8, node);
        memory.WriteByte(head + 0x19, 1);
        memory.WriteByte(node + 0x19, 0);
        memory.WriteInt(node + 0x20, 5);
        memory.WriteLong(node + 0x0, head);
        memory.WriteLong(node + 0x10, head);
        memory.WriteInt(node + 0x28, blockKind);
        memory.WriteLong(node + 0x30, blockIsZero ? 0 : block);
        memory.WriteByte(block + 1, 0x08);
        return memory;
    }

    [Theory]
    [InlineData(5012u, true)]   // The set bit.
    [InlineData(5013u, false)]  // Its neighbour.
    [InlineData(4012u, false)]  // A lower category than the tree holds.
    [InlineData(6012u, false)]  // A higher category than the tree holds.
    public void ReadFlag_FindsTheRightBit(uint flag, bool expected)
    {
        var reader = new EventFlagReader(BuildManager(), 0x100);
        Assert.Equal(expected, reader.ReadFlag(flag));
    }

    [Fact]
    public void ReadFlags_GivesTheSameAnswersAsReadingOneAtATime()
    {
        var memory = BuildManager();
        // The whole block is read at once, so all of it must be there: 1000 flags is 125 bytes.
        for (var i = 0; i < 125; i++)
        {
            if (i != 1)
                memory.WriteByte(0x600 + i, 0);
        }
        var reader = new EventFlagReader(memory, 0x100);
        uint[] flags = [5012, 5013, 5999, 4012, 6012];

        var on = reader.ReadFlags(flags);
        Assert.NotNull(on);
        Assert.Equal([5012u], on);
        foreach (var flag in flags)
            Assert.Equal(reader.ReadFlag(flag), on.Contains(flag));

        Assert.Null(new EventFlagReader(new FakeMemory(), 0x100).ReadFlags(flags));
    }

    [Fact]
    public void ReadFlag_KnowsAnUnallocatedBlockIsOff()
    {
        Assert.False(new EventFlagReader(BuildManager(blockKind: 2), 0x100).ReadFlag(5012));
        Assert.False(new EventFlagReader(BuildManager(blockIsZero: true), 0x100).ReadFlag(5012));
    }

    [Fact]
    public void ReadFlag_IsUnknownUntilTheGameIsReady()
    {
        var noManager = new FakeMemory();
        noManager.WriteLong(0x100, 0);
        Assert.Null(new EventFlagReader(noManager, 0x100).ReadFlag(5012));

        var noDivisor = new FakeMemory();
        noDivisor.WriteLong(0x100, 0x200);
        noDivisor.WriteInt(0x200 + 0x1C, 0);
        Assert.Null(new EventFlagReader(noDivisor, 0x100).ReadFlag(5012));

        Assert.Null(new EventFlagReader(new FakeMemory(), 0x100).ReadFlag(5012));
    }

    private static readonly PlayerOffsets Offsets = new(0x1E508, 0x6D0, 0x6D4);

    /// <summary>The pointer path to the player: a global pointer to the character manager, which points to the player.</summary>
    private static FakeMemory BuildPlayer(uint map, float x, float y, float z, long player = 0x5000)
    {
        const long pointer = 0x100, manager = 0x200;
        var memory = new FakeMemory();
        memory.WriteLong(pointer, manager);
        memory.WriteLong(manager + Offsets.PlayerInstance, player);
        memory.WriteInt(player + Offsets.MapId, unchecked((int)map));
        memory.WriteFloat(player + Offsets.Position, x);
        memory.WriteFloat(player + Offsets.Position + 4, y);
        memory.WriteFloat(player + Offsets.Position + 8, z);
        return memory;
    }

    [Fact]
    public void ReadPlayer_GivesTheMapAndPosition()
    {
        // m60_42_36_00 is packed with the area in the top byte: 0x3C2A2400.
        var position = new PlayerReader(BuildPlayer(0x3C2A2400, 12.5f, 230f, -40f), 0x100, Offsets).Read();

        Assert.NotNull(position);
        Assert.Equal("m60_42_36_00", position.Map.ToString());
        Assert.True(position.Map.IsOpenWorld);
        Assert.Equal((12.5f, 230f, -40f), (position.X, position.Y, position.Z));
    }

    [Fact]
    public void ReadPlayer_IsNullWithoutAPlayer()
    {
        var titleScreen = new FakeMemory();
        titleScreen.WriteLong(0x100, 0x200);
        titleScreen.WriteLong(0x200 + Offsets.PlayerInstance, 0);
        Assert.Null(new PlayerReader(titleScreen, 0x100, Offsets).Read());
        Assert.Null(new PlayerReader(new FakeMemory(), 0x100, Offsets).Read());
    }

    [Theory]
    [InlineData(0x3C2A2400u, float.NaN)]   // A position that is not a number.
    [InlineData(0x3C2A2400u, 9e9f)]        // Far beyond any map.
    [InlineData(0x00000000u, 10f)]         // No area: the offsets point somewhere else.
    [InlineData(0xFFFFFFFFu, 10f)]         // Not a map id.
    public void ReadPlayer_RejectsWhatCannotBeAPosition(uint map, float x)
    {
        Assert.Null(new PlayerReader(BuildPlayer(map, x, 0, 0), 0x100, Offsets).Read());
    }

    [Fact]
    public void ShippedSignatures_LoadWithPlayerOffsets()
    {
        var signatures = GameSignatures.Load(Path.Combine(AppContext.BaseDirectory, "Data", "signatures.json"));

        Assert.Equal(new Signature("44 89 7c 24 28 4c 8b 25 ? ? ? ? 4d 85 e4", 8, 12), signatures.EventFlagManager);
        Assert.NotNull(signatures.WorldChrMan);
        Assert.Equal(new PlayerOffsets(0x1E508, 0x6D0, 0x6D4), signatures.ParsePlayerOffsets());
    }

    [Theory]
    [InlineData("m60_42_36_00", 60, 42, 36, 0)]
    [InlineData("m10_00_00_00", 10, 0, 0, 0)]
    [InlineData("m61_44_45_10", 61, 44, 45, 10)]
    public void MapBlock_ParsesAndPrintsMapNames(string name, int area, int x, int z, int level)
    {
        var block = Navigation.MapBlock.Parse(name);
        Assert.Equal(new Navigation.MapBlock((byte)area, (byte)x, (byte)z, (byte)level), block);
        Assert.Equal(name, block.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hand placed")]
    [InlineData("m60_42_36")]
    [InlineData("m60_42_36_0x")]
    public void MapBlock_RejectsOtherText(string? name) => Assert.Null(Navigation.MapBlock.Parse(name));

    private sealed class FakeMemory : IMemoryReader
    {
        private readonly Dictionary<long, byte> _bytes = [];

        public void WriteByte(long address, byte value) => _bytes[address] = value;
        public void WriteInt(long address, int value) => Write(address, BitConverter.GetBytes(value));
        public void WriteLong(long address, long value) => Write(address, BitConverter.GetBytes(value));
        public void WriteFloat(long address, float value) => Write(address, BitConverter.GetBytes(value));

        public bool TryRead(long address, byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!_bytes.TryGetValue(address + i, out buffer[i]))
                    return false;
            }
            return true;
        }

        private void Write(long address, byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
                _bytes[address + i] = bytes[i];
        }
    }
}
