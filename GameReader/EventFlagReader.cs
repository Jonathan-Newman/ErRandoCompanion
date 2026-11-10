namespace ErRandoCompanion.GameReader;

public interface IMemoryReader
{
    /// <summary>Fills the buffer from the given address, returning false if any of it is unreadable.</summary>
    bool TryRead(long address, byte[] buffer, int count);
}

/// <summary>
/// Reads Elden Ring event flags from the game's flag manager. The flag lookup is
/// ported from SoulSplitter (GPL-3.0), which derived it from the game's own code:
/// https://github.com/FrankvdStam/SoulSplitter
/// </summary>
public sealed class EventFlagReader(IMemoryReader memory, long managerPointerAddress)
{
    private const int MaxTreeDepth = 64;
    private readonly byte[] _buffer = new byte[8];

    /// <summary>
    /// Reads many flags at once, returning the ones that are on, or null when the game cannot answer yet.
    /// Flags are stored in blocks of one category each, so each category's block is found once and read whole:
    /// a handful of reads per category instead of a full tree walk per flag.
    /// </summary>
    public HashSet<uint>? ReadFlags(IEnumerable<uint> flagIds)
    {
        if (!TryReadLong(managerPointerAddress, out var manager) || manager == 0)
            return null;
        if (!TryReadInt(manager + 0x1C, out var divisor) || divisor <= 0)
            return null;

        var on = new HashSet<uint>();
        foreach (var category in flagIds.Distinct().GroupBy(f => (int)(f / (uint)divisor)))
        {
            var block = FindBlock(manager, category.Key);
            if (block is null)
                return null;
            if (block == 0)
                continue; // a category the game has no block for: all its flags are off
            var bits = new byte[(divisor + 7) / 8];
            if (!memory.TryRead(block.Value, bits, bits.Length))
                return null;
            foreach (var flag in category)
            {
                var remainder = (int)(flag % (uint)divisor);
                if ((bits[remainder >> 3] & (1 << (7 - (remainder & 7)))) != 0)
                    on.Add(flag);
            }
        }
        return on;
    }

    /// <summary>The address of a category's flag block; 0 when it has none (all off); null when it cannot be read.</summary>
    private long? FindBlock(long manager, int category)
    {
        // The manager keeps its flag blocks in a binary search tree keyed by category.
        if (!TryReadLong(manager + 0x38, out var candidate) || !TryReadLong(candidate + 0x8, out var node))
            return null;

        // Each step reads the whole node (children, leaf mark, category) at once rather than field by field.
        var fields = new byte[0x24];
        for (var depth = 0; ; depth++)
        {
            if (depth > MaxTreeDepth || !memory.TryRead(node, fields, fields.Length))
                return null;
            if (fields[0x19] != 0)
                break; // a leaf: the search ends
            var nodeCategory = BitConverter.ToInt32(fields, 0x20);
            if (nodeCategory < category)
            {
                node = BitConverter.ToInt64(fields, 0x10);
            }
            else
            {
                candidate = node;
                node = BitConverter.ToInt64(fields, 0x0);
            }
        }

        if (candidate == node)
            return 0;
        if (!TryReadInt(candidate + 0x20, out var candidateCategory))
            return null;
        if (category < candidateCategory)
            return 0;
        if (!TryReadInt(candidate + 0x28, out var blockKind))
            return null;

        switch (blockKind)
        {
            case 1:
                if (!TryReadInt(manager + 0x20, out var blockSize)
                    || !TryReadInt(candidate + 0x30, out var blockIndex)
                    || !TryReadLong(manager + 0x28, out var blocksStart))
                {
                    return null;
                }
                return (long)blockSize * blockIndex + blocksStart;
            case 2:
                return 0;
            default:
                return TryReadLong(candidate + 0x30, out var block) ? block : null;
        }
    }

    /// <summary>
    /// Returns whether a flag is on, or null when the game cannot answer yet,
    /// such as while it is still starting up.
    /// </summary>
    public bool? ReadFlag(uint flagId)
    {
        if (!TryReadLong(managerPointerAddress, out var manager) || manager == 0)
            return null;
        if (!TryReadInt(manager + 0x1C, out var divisor) || divisor <= 0)
            return null;

        var remainder = (int)(flagId % (uint)divisor);
        var block = FindBlock(manager, (int)(flagId / (uint)divisor));
        if (block is null)
            return null;
        if (block == 0)
            return false;
        if (!TryReadByte(block.Value + (remainder >> 3), out var bits))
            return null;
        return (bits & (1 << (7 - (remainder & 7)))) != 0;
    }

    private bool TryReadByte(long address, out byte value)
    {
        var ok = memory.TryRead(address, _buffer, 1);
        value = _buffer[0];
        return ok;
    }

    private bool TryReadInt(long address, out int value)
    {
        var ok = memory.TryRead(address, _buffer, sizeof(int));
        value = BitConverter.ToInt32(_buffer, 0);
        return ok;
    }

    private bool TryReadLong(long address, out long value)
    {
        var ok = memory.TryRead(address, _buffer, sizeof(long));
        value = BitConverter.ToInt64(_buffer, 0);
        return ok;
    }
}
