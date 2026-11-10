using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ErRandoCompanion.Core.SpoilerLog;

/// <summary>What one pickup gives: an item, its kind for filtering, and how many.</summary>
public sealed record PickupItem(string Name, string Kind, int Count);

/// <summary>
/// One pickup in the world (a corpse, a chest, a unique enemy drop): the flag the game sets when it is taken, what it
/// gives in this seed, and where it is, described as the randomizer describes the spot.
/// This is spoiler data: only shown for pickups the player has already taken.
/// </summary>
public sealed record Pickup(uint Flag, IReadOnlyList<PickupItem> Items, string Where, string Map);

/// <summary>Builds the seed's pickups from its regulation, the randomizer's item names and its item slot descriptions.</summary>
public static partial class ItemPickups
{
    public const string Weapons = "Weapons";
    public const string Armour = "Armour";
    public const string Talismans = "Talismans";
    public const string AshesOfWar = "Ashes of War";
    public const string Spells = "Spells";
    public const string SpiritAshes = "Spirit Ashes";
    public const string Items = "Items";

    /// <summary>The kinds, in the order the Found lists offer them.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Weapons, Armour, Talismans, AshesOfWar, Spells, SpiritAshes, Items];

    /// <summary>
    /// Pickups by flag. <paramref name="randomizerDirectory"/> holds diste\Names (item names) and diste\Base\itemslots.txt
    /// (where each pickup is); without them the pickups are still found, named by id and placed nowhere in particular.
    /// </summary>
    /// <summary>diste\Names\MapName.txt: "m60_42_36_00 Limgrave" lines, for naming the map a pickup was made on.</summary>
    public static IReadOnlyDictionary<string, string> LoadMapNames(string randomizerDirectory)
    {
        var path = Path.Combine(randomizerDirectory, "diste", "Names", "MapName.txt");
        var names = new Dictionary<string, string>();
        if (!File.Exists(path))
            return names;
        foreach (var line in File.ReadLines(path))
        {
            var space = line.IndexOf(' ');
            if (space > 0)
                names.TryAdd(line[..space], line[(space + 1)..].Trim());
        }
        return names;
    }

    public static IReadOnlyDictionary<uint, Pickup> Build(Regulation regulation, string randomizerDirectory)
    {
        var names = LoadNames(Path.Combine(randomizerDirectory, "diste", "Names"));
        var places = LoadPlaces(Path.Combine(randomizerDirectory, "diste", "Base", "itemslots.txt"), out var maps);
        return Build(regulation, names, places, maps);
    }

    public static IReadOnlyDictionary<uint, Pickup> Build(
        Regulation regulation,
        IReadOnlyDictionary<(int Category, int Id), string> names,
        IReadOnlyDictionary<int, string> places,
        IReadOnlyDictionary<int, string> maps)
    {
        var byFlag = new Dictionary<uint, (List<PickupItem> Items, int Lot)>();
        foreach (var (lot, entries) in regulation.Lots)
        {
            foreach (var entry in entries.Where(e => e.Flag != 0 && e.ItemId > 0 && e.Category is >= 1 and <= 6))
            {
                if (!byFlag.TryGetValue(entry.Flag, out var pickup))
                    byFlag[entry.Flag] = pickup = ([], lot);
                pickup.Items.Add(new PickupItem(NameOf(names, entry.Category, entry.ItemId), KindOf(entry.Category, entry.ItemId), Math.Max(1, entry.Count)));
            }
        }
        return byFlag.ToDictionary(
            kv => kv.Key,
            kv => new Pickup(kv.Key, kv.Value.Items,
                places.GetValueOrDefault(kv.Value.Lot, Nowhere),
                maps.GetValueOrDefault(kv.Value.Lot, "")));
    }

    /// <summary>The kind of item, from its lot category and, for goods, its id range.</summary>
    public static string KindOf(int category, int id) => category switch
    {
        2 or 6 => Weapons,
        3 => Armour,
        4 => Talismans,
        5 => AshesOfWar,
        _ when id is >= 4000 and <= 7999 => Spells,
        _ when id is >= 200000 and <= 299999 => SpiritAshes,
        _ => Items,
    };

    /// <summary>An item's name from the name tables (keyed by item lot category), or "Item 1234".</summary>
    public static string NameOf(IReadOnlyDictionary<(int Category, int Id), string> names, int category, int id)
    {
        var table = category == 6 ? 2 : category;
        if (names.TryGetValue((table, id), out var name))
            return name;
        // Weapons can come upgraded or infused: the last digits are the level, the base weapon has the name.
        if (table == 2 && names.TryGetValue((2, id - id % 100), out name))
            return name;
        return $"Item {id}";
    }

    /// <summary>diste\Names\EquipParam*.txt: one "id name" a line, per item table.</summary>
    public static IReadOnlyDictionary<(int Category, int Id), string> LoadNames(string folder)
    {
        var names = new Dictionary<(int, int), string>();
        foreach (var (category, file) in new[] { (1, "EquipParamGoods.txt"), (2, "EquipParamWeapon.txt"), (3, "EquipParamProtector.txt"), (4, "EquipParamAccessory.txt"), (5, "EquipParamGem.txt") })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadLines(path))
            {
                var space = line.IndexOf(' ');
                if (space > 0 && int.TryParse(line.AsSpan(0, space), out var id) && !line.Contains("[ERROR]"))
                    names[(category, id)] = line[(space + 1)..].Trim();
            }
        }
        return names;
    }

    /// <summary>The place given to a pickup the randomizer does not describe.</summary>
    public const string Nowhere = "Somewhere in the world";

    /// <summary>
    /// itemslots.txt: each slot's description ("Text:") for every lot it lists ("- Item - lot 1046360500[... in
    /// m60_46_36_00 (Limgrave) ...]"), and the name of the map that lot is in.
    /// </summary>
    public static IReadOnlyDictionary<int, string> LoadPlaces(string path, out IReadOnlyDictionary<int, string> maps)
    {
        var places = new Dictionary<int, string>();
        var mapNames = new Dictionary<int, string>();
        maps = mapNames;
        if (!File.Exists(path))
            return places;

        foreach (var block in File.ReadAllText(path).Split("\n- Key: ").Skip(1))
        {
            var text = TextLine().Match(block);
            if (!text.Success)
                continue;
            string? where = text.Groups[1].Value.Trim().Trim('\'', '"');
            // Spots without a written description have a row of a's instead; the nearest grace says it better.
            if (IsPlaceholder(where))
                where = NearGrace(block);
            foreach (Match lot in LotLine().Matches(block))
            {
                var id = int.Parse(lot.Groups[1].Value);
                if (!string.IsNullOrEmpty(where))
                    places.TryAdd(id, where);
                if (lot.Groups[2].Success)
                    mapNames.TryAdd(id, lot.Groups[2].Value);
            }
        }
        return places;
    }

    /// <summary>The randomizer's stand-in for a spot it has no description of: "aaaaaaaaaaaaaaaaa".</summary>
    public static bool IsPlaceholder(string where) => where.Length > 0 && where.All(c => c == 'a');

    /// <summary>
    /// "Near Liftside Chamber, 20 m away, 17 m up", from the slot's "By Liftside Chamber (Stormveil Castle) - 19.86 away,
    /// 0.69 o'clock, 16.74 height offset" line, or null when it has none.
    /// </summary>
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

    [GeneratedRegex(@"\n  - By (.+?) \([^)\n]*\) - ([\d.]+) away, [\d.]+ o'clock, (-?[\d.]+) height offset")]
    private static partial Regex ByLine();

    [GeneratedRegex(@"\n  Text: ([^\n]*)")]
    private static partial Regex TextLine();

    [GeneratedRegex(@" - lot (\d+)\[(?:[^\]\n]*? in m\d\d_\d\d_\d\d_\d\d \(([^)\n]*)\))?")]
    private static partial Regex LotLine();
}
