using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.SpoilerLog;

/// <summary>A tracked item the seed placed, and the flag that turns on when it is collected.</summary>
/// <param name="CreditKey">Credited once per save. Copies of a one-of-a-kind item share a key, so duplicates of its lot count once.</param>
public sealed record ItemPlacement(string TrackedItem, int Count, uint FlagId, string CreditKey);

/// <summary>
/// The flags to watch for one seed. This is spoiler data, as a placement's flag reveals where an
/// item is, so it is only used for tracking and never shown.
/// </summary>
public sealed class SeedFlags
{
    private const string GreatRunesItem = "Great Runes";

    // The tracker counts these as one item. Miquella's is deliberately excluded, as in the checklist.
    private static readonly HashSet<string> GreatRunes =
    [
        "Godrick's Great Rune", "Radahn's Great Rune", "Morgott's Great Rune", "Rykard's Great Rune",
        "Mohg's Great Rune", "Malenia's Great Rune", "Great Rune of the Unborn",
    ];

    // Two goods share this name in-game; the randomizer's iteminfo.txt tells them apart by recipient.
    private static readonly Dictionary<int, string> SharedNames = new()
    {
        [8127] = "Letter from Volcano Manor (Istvan)",
        [8132] = "Letter from Volcano Manor (Rileigh)",
    };

    public required IReadOnlyList<ItemPlacement> Placements { get; init; }

    /// <summary>The flags this seed uses for a location's lots, by location id, where the seed has any.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<uint>> LocationFlags { get; init; }

    /// <summary>Items the log and regulation disagree about, which means they came from different randomizer runs.</summary>
    public required int LogMismatches { get; init; }

    /// <summary>Tracked items the log places that have no flag in the regulation, so collecting them cannot be seen.</summary>
    public required IReadOnlyList<string> UntrackableItems { get; init; }

    /// <param name="goodsNames">Goods ids and names, from the randomizer's EquipParamGoods.txt.</param>
    public static SeedFlags Build(
        GameData data, ParsedSpoilerLog log, Regulation regulation, IEnumerable<(int Id, string Name)> goodsNames)
    {
        var tracked = data.Items.ToDictionary(i => i.Name);

        // Log item names map to tracked items directly, or through the Great Rune group.
        var sourcesById = new Dictionary<int, string>();
        foreach (var (id, goodsName) in goodsNames)
        {
            var name = SharedNames.GetValueOrDefault(id, goodsName);
            if (tracked.ContainsKey(name) || GreatRunes.Contains(name))
                sourcesById[id] = name;
        }

        var placements = new List<ItemPlacement>();
        var soldInSeed = new HashSet<string>();

        foreach (var entry in regulation.Lots.Values.SelectMany(lot => lot).Where(e => e.Category == Regulation.GoodsCategory))
        {
            if (sourcesById.TryGetValue(entry.ItemId, out var source) && entry.Flag != 0)
                placements.Add(Placement(source, Math.Max(1, entry.Count), entry.Flag, tracked));
        }

        foreach (var shop in regulation.ShopRows.Where(s => s.ItemType == Regulation.GoodsShopType))
        {
            if (sourcesById.TryGetValue(shop.ItemId, out var source))
            {
                soldInSeed.Add(source);

                // Unlimited stock sets no flag, and buying it could not be counted anyway.
                if (shop.Flag != 0 && shop.Quantity > 0)
                    placements.Add(Placement(source, 1, shop.Flag, tracked));
            }
        }

        var distinctPlacements = placements.Distinct().ToList();
        var placedNames = distinctPlacements.Select(p => SourceOf(p)).ToHashSet();
        var relevantLogItems = log.SoldItems.Where(kv => tracked.ContainsKey(kv.Key) || GreatRunes.Contains(kv.Key)).ToList();

        return new SeedFlags
        {
            Placements = distinctPlacements,
            LocationFlags = BuildLocationFlags(data, regulation),

            // One-of-a-kind items must be sold in the regulation exactly when the log says so.
            LogMismatches = relevantLogItems.Count(kv =>
                (!tracked.TryGetValue(kv.Key, out var item) || item.MaxCount == 1) && soldInSeed.Contains(kv.Key) != kv.Value),
            // Only items the logic can use are worth a warning.
            UntrackableItems = relevantLogItems.Select(kv => kv.Key)
                .Where(name => !placedNames.Contains(name)
                    && (!tracked.TryGetValue(name, out var item) || (item.MaxCount == 1 && (item.Category == "key" || item.Shortcut))))
                .ToList(),
        };
    }

    /// <summary>Parses EquipParamGoods.txt lines of the form "8148 Godrick's Great Rune".</summary>
    public static IEnumerable<(int Id, string Name)> ParseGoodsNames(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line.AsSpan(0, space), out var id))
                yield return (id, line[(space + 1)..].Trim());
        }
    }

    private static string SourceOf(ItemPlacement placement) =>
        placement.CreditKey.Split('|')[0];

    private static ItemPlacement Placement(string source, int count, uint flag, IReadOnlyDictionary<string, Item> tracked)
    {
        var trackedName = GreatRunes.Contains(source) ? GreatRunesItem : source;
        var isUnique = GreatRunes.Contains(source) || tracked[source].MaxCount == 1;
        return new ItemPlacement(trackedName, count, flag, isUnique ? source : $"{source}|{flag}");
    }

    private static Dictionary<string, IReadOnlyList<uint>> BuildLocationFlags(GameData data, Regulation regulation)
    {
        var shopFlags = regulation.ShopRows
            .Where(s => s.Flag != 0 && s.Quantity > 0)
            .GroupBy(s => s.RowId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Flag).ToList());
        var flags = new Dictionary<string, IReadOnlyList<uint>>();
        foreach (var location in data.Locations)
        {
            // A merchant counts as checked at the first purchase of a limited item from it: the shop was opened
            // and everything it sells was seen. That holds whatever the seed placed there, so it gives nothing away.
            var lotFlags = location.LotIds
                .Where(regulation.Lots.ContainsKey)
                .SelectMany(id => regulation.Lots[id])
                .Select(e => e.Flag)
                .Concat(location.ShopRows.SelectMany(row => shopFlags.GetValueOrDefault(row) ?? []))
                .Where(f => f != 0)
                .Distinct()
                .ToList();
            if (lotFlags.Count > 0)
                flags[location.Id] = lotFlags;
        }
        return flags;
    }
}
