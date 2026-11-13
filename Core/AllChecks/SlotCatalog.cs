using System.Globalization;
using System.Text.RegularExpressions;

namespace ErRandoCompanion.Core.AllChecks;

/// <summary>What sort of check a slot is, for filtering.</summary>
public static class CheckKinds
{
    public const string Pickup = "Pickup";
    public const string Chest = "Chest";
    public const string EnemyDrop = "Enemy drop";
    public const string Boss = "Boss";
    public const string Quest = "Quest";
    public const string Shop = "Shop";

    public static readonly IReadOnlyList<string> All = [Pickup, Chest, EnemyDrop, Boss, Quest, Shop];
}

/// <summary>
/// One place the randomizer can put an item: a lot (a corpse, a chest, a drop, a reward) or a shop row, as its
/// diste\Base\itemslots.txt describes it. <see cref="Replaces"/> names the item the unrandomized game has there,
/// which is not a spoiler: it tells apart several slots in one spot, such as a merchant's wares.
/// </summary>
public sealed record SlotEntry(
    string Id, bool IsShop, int Row, string Area, string Description, string Replaces, string Kind,
    string QuestRequirements, bool Missable, IReadOnlySet<string> Tags)
{
    /// <summary>The map the slot is in ("m10_00_00_00"), or "" when the randomizer does not say.</summary>
    public string Map { get; init; } = "";
}

/// <summary>Every item slot the randomizer knows, read once per randomizer folder. Holds no seed data.</summary>
public static partial class SlotCatalog
{
    // Never randomized, or duplicates the randomizer skips: not checks.
    private static readonly HashSet<string> Skipped = ["norandom", "ignore"];
    private static readonly HashSet<string> BossTags = ["boss", "altboss", "remembranceboss", "overworldboss", "minidungeonboss", "dlcboss", "majorboss"];
    private static readonly HashSet<string> EnemyTags = ["enemy", "enemygem", "enemyweapon"];

    public static IReadOnlyList<SlotEntry> Load(string itemSlotsPath) => Parse(File.ReadAllText(itemSlotsPath));

    public static IReadOnlyList<SlotEntry> Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        var entries = new List<SlotEntry>();
        var seen = new HashSet<string>();
        foreach (var block in text.Split("\n- Key: ").Skip(1))
        {
            var tags = (RandomizerLogic.Field(block, "Tags") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            if (tags.Overlaps(Skipped))
                continue;
            var area = (RandomizerLogic.Field(block, "Area") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var description = RandomizerLogic.Field(block, "Text") ?? "";
            if (description.Length == 0 || description.All(c => c == 'a'))
                description = NearGrace(block) ?? "";
            var quest = RandomizerLogic.Field(block, "QuestReqs") ?? "";
            var hasEvent = RandomizerLogic.Field(block, "Event") is not null;

            // One entry per lot or shop row; a lot giving several items is one pickup.
            var rows = new Dictionary<(bool Shop, int Row), List<string>>();
            var maps = new Dictionary<(bool Shop, int Row), string>();
            foreach (Match m in EntryLine().Matches(block))
            {
                var key = (m.Groups[2].Value == "shop", int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                if (!rows.TryGetValue(key, out var names))
                    rows[key] = names = [];
                var lineEnd = block.IndexOf('\n', m.Index + 1);
                var map = MapIn().Match(lineEnd < 0 ? block[m.Index..] : block[m.Index..lineEnd]);
                if (map.Success)
                    maps.TryAdd(key, map.Groups[1].Value);
                var name = m.Groups[1].Value.Replace("''", "'").TrimStart('\'', '"');
                if (!names.Contains(name))
                    names.Add(name);
            }

            foreach (var ((shop, row), names) in rows)
            {
                var id = $"{(shop ? "shop" : "lot")}:{row}";
                if (!seen.Add(id))
                    continue;
                entries.Add(new SlotEntry(id, shop, row, area, description, string.Join(", ", names),
                    KindOf(shop, tags, description, hasEvent), quest, tags.Contains("missable"), tags)
                {
                    Map = maps.GetValueOrDefault((shop, row)) ?? "",
                });
            }
        }
        return entries;
    }

    private static string KindOf(bool shop, IReadOnlySet<string> tags, string description, bool hasEvent)
    {
        if (shop)
            return CheckKinds.Shop;
        if (tags.Overlaps(BossTags) || hasEvent)
            return CheckKinds.Boss;
        if (tags.Contains("questreward"))
            return CheckKinds.Quest;
        if (tags.Overlaps(EnemyTags) || description.StartsWith("Dropped by", StringComparison.OrdinalIgnoreCase))
            return CheckKinds.EnemyDrop;
        if (tags.Contains("chest") || description.Contains("chest", StringComparison.OrdinalIgnoreCase))
            return CheckKinds.Chest;
        return CheckKinds.Pickup;
    }

    // "Near Liftside Chamber, 20 m away, 17 m up", for a slot the randomizer has no written description of.
    private static string? NearGrace(string block)
    {
        var by = ByLine().Match(block);
        if (!by.Success)
            return null;
        var away = double.Parse(by.Groups[2].Value, CultureInfo.InvariantCulture);
        var height = double.Parse(by.Groups[3].Value, CultureInfo.InvariantCulture);
        var rise = height switch
        {
            >= 3 => string.Create(CultureInfo.InvariantCulture, $", {height:0} m up"),
            <= -3 => string.Create(CultureInfo.InvariantCulture, $", {-height:0} m down"),
            _ => "",
        };
        return string.Create(CultureInfo.InvariantCulture, $"Near {by.Groups[1].Value}, {away:0} m away{rise}");
    }

    [GeneratedRegex(@"\n  - ['""]?(.+?) - (lot|shop) (\d+)\[")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@" in (m\d\d_\d\d_\d\d_\d\d) \(")]
    private static partial Regex MapIn();

    [GeneratedRegex(@"\n  - By (.+?) \([^)\n]*\) - ([\d.]+) away, [\d.]+ o'clock, (-?[\d.]+) height offset")]
    private static partial Regex ByLine();
}
