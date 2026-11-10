namespace ErRandoCompanion.Core.Logic;

/// <summary>
/// Spots items the player has just received by comparing the inventory with the last look: what pickup flags cannot
/// show, such as random enemy drops. Items taken out of the storage chest are not counted. The first look after
/// loading in is only a starting point, so loading a save never floods the log.
/// </summary>
public sealed class ReceivedItems
{
    private IReadOnlyDictionary<uint, int>? _held;
    private IReadOnlyDictionary<uint, int>? _stored;

    /// <summary>Items gained since the last look (item id, how many), not counting those from the storage chest.</summary>
    public IReadOnlyList<(uint Id, int Count)> Compare(IReadOnlyDictionary<uint, int> held, IReadOnlyDictionary<uint, int> stored)
    {
        var lastHeld = _held;
        var lastStored = _stored;
        _held = held;
        _stored = stored;
        if (lastHeld is null || lastStored is null)
            return [];

        var gains = new List<(uint, int)>();
        foreach (var (id, count) in held)
        {
            var gained = count - lastHeld.GetValueOrDefault(id);
            if (gained <= 0)
                continue;
            var fromChest = Math.Max(0, lastStored.GetValueOrDefault(id) - stored.GetValueOrDefault(id));
            gained -= fromChest;
            if (gained > 0)
                gains.Add((id, gained));
        }
        return gains;
    }

    /// <summary>Forgets the last look, for when the player leaves the world (the title screen, another save).</summary>
    public void Reset()
    {
        _held = null;
        _stored = null;
    }

    /// <summary>The item table (as item lots number them) for an inventory item id's top four bits.</summary>
    public static int LotCategory(uint id) => (id >> 28) switch
    {
        0 => 2, // weapon
        1 => 3, // armour
        2 => 4, // talisman
        8 => 5, // ash of war
        _ => 1, // goods
    };

    /// <summary>The param id inside its table: the inventory id without its kind bits.</summary>
    public static int ParamId(uint id) => (int)(id & 0x0FFFFFFF);
}
