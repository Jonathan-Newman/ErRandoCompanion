using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Logic;

/// <summary>
/// Tracks from what arrives in the inventory rather than from flags. In someone else's world (Seamless Co-op), a
/// guest's game does not set the pickup flags, so every pickup arrives unexplained, as "Drop or other" in the Found
/// log. When an item the seed puts in only one place arrives that way, that place has been checked; and a tracked key
/// item arriving that way counts toward what the player has. Only items no flag explained are used, so nothing is
/// counted twice when flags do work. The item-to-place map is spoiler data, used only after the item is in hand.
/// </summary>
public sealed class ReceivedTracker
{
    private readonly Dictionary<string, string> _locationByItem;
    private readonly Dictionary<string, List<string>> _locationsWithItem;

    /// <summary>How close the player must be to a check's pin for an arriving item to be put down to it.</summary>
    public const double NearbyMetres = 60;
    private readonly Dictionary<string, List<ItemPlacement>> _placementsByItem;
    private readonly IReadOnlyDictionary<string, Item> _items;

    private ReceivedTracker(Dictionary<string, string> locationByItem, Dictionary<string, List<string>> locationsWithItem,
        Dictionary<string, List<ItemPlacement>> placementsByItem, GameData data)
    {
        _locationByItem = locationByItem;
        _locationsWithItem = locationsWithItem;
        _placementsByItem = placementsByItem;
        _items = data.Items.ToDictionary(i => i.Name);
    }

    public static ReceivedTracker Empty(GameData data) => new([], [], [], data);

    /// <summary>Metres from the player to a check's pin, for <see cref="Apply"/>; null when it is in another map or has no pin.</summary>
    public static Func<string, double?> DistanceFrom(Navigation.MapBlock map, double x, double y, double z,
        IReadOnlyDictionary<string, MapPin> pins, Navigation.MapLinks links) =>
        id => pins.TryGetValue(id, out var pin)
            && Navigation.Guide.For(map, x, y, z, pin, links) is { Mode: Navigation.GuideMode.SameMap or Navigation.GuideMode.Direct } way
                ? way.Distance
                : null;

    /// <summary>The checklist places an arriving item can tick, for reports.</summary>
    public IReadOnlyCollection<string> Places => _locationByItem.Values.Distinct().ToList();

    /// <summary>The checklist places an arriving item can tick when the player is beside them, for reports.</summary>
    public IReadOnlyCollection<string> PlacesNearby => _locationsWithItem.Values.SelectMany(v => v).Distinct().ToList();

    /// <summary>How many places the seed puts each item in, by item key, from the spoiler log's counts.</summary>
    public static Dictionary<string, int> PlacementsByKey(IReadOnlyDictionary<string, int> placementCounts)
    {
        var byKey = new Dictionary<string, int>();
        foreach (var (name, count) in placementCounts)
        {
            var key = AllChecksSeed.ItemKey(name);
            byKey[key] = byKey.GetValueOrDefault(key) + count;
        }
        return byKey;
    }

    /// <summary>A shop row's item as a lot category (goods 1, weapon 2, armour 3, talisman 4, ash of war 5).</summary>
    public static int ShopCategory(byte equipType) => equipType switch
    {
        0 => 2,
        1 => 3,
        2 => 4,
        4 => 5,
        _ => 1,
    };

    public static ReceivedTracker Build(GameData data, SeedFlags flags, Regulation regulation,
        IReadOnlyDictionary<(int Category, int Id), string> names, IReadOnlyDictionary<string, int> placementCounts)
    {
        var counts = PlacementsByKey(placementCounts);
        bool Unique(string key) => counts.GetValueOrDefault(key) == 1;

        // Each item the seed puts in only one place, and the checklist place that holds it.
        var locationByItem = new Dictionary<string, string>();
        var locationsWithItem = new Dictionary<string, List<string>>();
        var ambiguous = new HashSet<string>();
        foreach (var location in data.Locations)
        {
            var items = location.LotIds
                .SelectMany(id => regulation.Lots.GetValueOrDefault(id) ?? [])
                .Where(e => e.Category is >= 1 and <= 6)
                .Select(e => ItemPickups.NameOf(names, e.Category, e.ItemId))
                .Concat(regulation.ShopRows.Where(s => location.ShopRows.Contains(s.RowId))
                    .Select(s => ItemPickups.NameOf(names, ShopCategory(s.ItemType), s.ItemId)))
                .Select(AllChecksSeed.ItemKey)
                .Distinct();
            foreach (var key in items)
            {
                if (!locationsWithItem.TryGetValue(key, out var holders))
                    locationsWithItem[key] = holders = [];
                holders.Add(location.Id);
                if (Unique(key) && !locationByItem.TryAdd(key, location.Id))
                    ambiguous.Add(key);
            }
        }
        foreach (var key in ambiguous)
            locationByItem.Remove(key);

        // The tracked items' placements by the item each gives, so one arriving can be credited once.
        var placementsByItem = new Dictionary<string, List<ItemPlacement>>();
        foreach (var placement in flags.Placements)
        {
            var key = AllChecksSeed.ItemKey(placement.CreditKey.Split('|')[0]);
            if (!placementsByItem.TryGetValue(key, out var list))
                placementsByItem[key] = list = [];
            list.Add(placement);
        }
        return new ReceivedTracker(locationByItem, locationsWithItem, placementsByItem, data);
    }

    /// <summary>
    /// Credits items that arrived with no flag to explain them ("Name" or "Name x3", as the Found log writes them).
    /// An item the seed puts in one place ticks that place; one it puts in several ticks the nearest of them within
    /// <see cref="NearbyMetres"/> of the player, when <paramref name="distanceTo"/> can tell (metres to a check's pin,
    /// or null when unknown). Returns whether anything changed.
    /// </summary>
    public bool Apply(TrackerState state, IEnumerable<string> received, Func<string, double?>? distanceTo = null)
    {
        var changed = false;
        foreach (var entry in received)
        {
            var split = entry.LastIndexOf(" x", StringComparison.Ordinal);
            var (name, count) = split > 0 && int.TryParse(entry[(split + 2)..], out var n) ? (entry[..split], n) : (entry, 1);
            var key = AllChecksSeed.ItemKey(name);

            var location = _locationByItem.GetValueOrDefault(key);
            if (location is null && distanceTo is not null && _locationsWithItem.TryGetValue(key, out var holders))
            {
                location = holders
                    .Where(id => !state.AutoCredited.Contains($"location:{id}") && !state.CheckedLocations.Contains(id))
                    .Select(id => (Id: id, Distance: distanceTo(id)))
                    .Where(h => h.Distance is <= NearbyMetres)
                    .OrderBy(h => h.Distance)
                    .Select(h => h.Id)
                    .FirstOrDefault();
            }
            if (location is not null && state.AutoCredited.Add($"location:{location}"))
            {
                state.CheckedLocations.Add(location);
                changed = true;
            }

            // A key item: credit as many of its placements as arrived, each once, so a flag seen later adds nothing.
            if (_placementsByItem.TryGetValue(key, out var placements))
            {
                var left = count;
                foreach (var placement in placements.Where(p => !state.AutoCredited.Contains($"item:{p.CreditKey}")).ToList())
                {
                    if (left <= 0)
                        break;
                    left -= placement.Count;
                    state.AutoCredited.Add($"item:{placement.CreditKey}");
                    var owned = state.Items.GetValueOrDefault(placement.TrackedItem);
                    var max = _items.TryGetValue(placement.TrackedItem, out var item) ? item.MaxCount : int.MaxValue;
                    state.Items[placement.TrackedItem] = Math.Min(owned + placement.Count, max);
                    changed = true;
                }
            }
        }
        return changed;
    }
}
