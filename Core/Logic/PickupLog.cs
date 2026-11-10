using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Logic;

/// <summary>
/// Keeps a run's log of everything picked up. Pickups set a flag each; this watches the ones not yet taken and records
/// what each gave and where. Shared by the app and the mod, so both log the same way.
/// </summary>
public static class PickupLog
{
    /// <summary>How often pickups and the inventory are scanned, in polls: seconds late is fine.</summary>
    public const int ScanEvery = 3;

    /// <summary>The pickup flags still worth watching: those not yet in the log.</summary>
    public static IEnumerable<uint> Pending(TrackerState state, IReadOnlyDictionary<uint, Pickup> pickups)
    {
        if (pickups.Count == 0)
            return [];
        var found = state.Found.Select(f => f.Flag).ToHashSet();
        return pickups.Keys.Where(flag => !found.Contains(flag));
    }

    /// <summary>
    /// Logs items that arrived in the inventory without a pickup flag explaining them: random enemy drops, purchases,
    /// crafting. <paramref name="explained"/> are the items the pickups logged in the same look gave (name to count),
    /// which are not logged twice. Returns how many were added.
    /// </summary>
    public static int RecordReceived(TrackerState state, IReadOnlyList<(uint Id, int Count)> gains,
        IReadOnlyDictionary<(int Category, int Id), string> names, IDictionary<string, int> explained, DateTime now, string? mapName)
    {
        var added = 0;
        foreach (var (id, count) in gains)
        {
            var category = ReceivedItems.LotCategory(id);
            var name = ItemPickups.NameOf(names, category, ReceivedItems.ParamId(id));
            var left = count - (explained.TryGetValue(name, out var already) ? already : 0);
            if (explained.ContainsKey(name))
                explained[name] = Math.Max(0, already - count);
            if (left <= 0)
                continue;
            state.Found.Add(new FoundItem
            {
                Items = [left > 1 ? $"{name} x{left}" : name],
                Kinds = [ItemPickups.KindOf(category, ReceivedItems.ParamId(id))],
                Where = mapName is null ? "Received" : $"Received in {mapName}",
                At = now,
                PlayedSeconds = state.PlayedSeconds,
                Source = FoundItem.DropOrOther,
            });
            added++;
        }
        return added;
    }

    /// <summary>
    /// Mends entries saved with the randomizer's placeholder text for a place ("aaaaaaaaaaaaaaaaa") by reading the place
    /// again from the seed's pickups; entries it cannot place keep only their map. Returns how many were mended.
    /// </summary>
    public static int Repair(TrackerState state, IReadOnlyDictionary<uint, Pickup> pickups)
    {
        var mended = 0;
        foreach (var found in state.Found.Where(f => ItemPickups.IsPlaceholder(f.Where)))
        {
            found.Where = pickups.TryGetValue(found.Flag, out var pickup) && !ItemPickups.IsPlaceholder(pickup.Where)
                && pickup.Where != ItemPickups.Nowhere
                ? pickup.Where
                : "";
            mended++;
        }
        return mended;
    }

    /// <summary>What a set of found entries gave, by item name, to keep the inventory comparison from logging them twice.</summary>
    public static Dictionary<string, int> Given(IEnumerable<FoundItem> found)
    {
        var given = new Dictionary<string, int>();
        foreach (var item in found.SelectMany(f => f.Items))
        {
            var split = item.LastIndexOf(" x", StringComparison.Ordinal);
            var (name, count) = split > 0 && int.TryParse(item[(split + 2)..], out var n) ? (item[..split], n) : (item, 1);
            given[name] = given.GetValueOrDefault(name) + count;
        }
        return given;
    }

    /// <summary>
    /// Adds the pickups whose flags are now on. The run's very first scan logs what was taken before tracking, without
    /// a time. Returns how many were added.
    /// </summary>
    /// <param name="mapName">The map the player is on, for pickups the randomizer has no description for.</param>
    public static int Record(TrackerState state, IReadOnlyDictionary<uint, Pickup> pickups, IReadOnlySet<uint> flagsOn,
        DateTime now, string? mapName)
    {
        var earlier = !state.PickupsScanned;
        state.PickupsScanned = true;
        var found = state.Found.Select(f => f.Flag).ToHashSet();
        var added = 0;
        foreach (var flag in flagsOn)
        {
            if (!pickups.TryGetValue(flag, out var pickup) || !found.Add(flag))
                continue;
            state.Found.Add(new FoundItem
            {
                Flag = flag,
                Items = pickup.Items.Select(i => i.Count > 1 ? $"{i.Name} x{i.Count}" : i.Name).ToList(),
                Kinds = pickup.Items.Select(i => i.Kind).ToList(),
                Where = pickup.Where != ItemPickups.Nowhere || earlier || mapName is null
                    ? pickup.Where
                    : $"Picked up in {mapName}",
                // A place without a description already names the map ("Picked up in Liurnia"), so not twice.
                Map = pickup.Map,
                At = earlier ? null : now,
                PlayedSeconds = earlier ? 0 : state.PlayedSeconds,
            });
            added++;
        }
        return added;
    }
}
