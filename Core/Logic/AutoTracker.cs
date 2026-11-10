using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Logic;

/// <summary>
/// Turns event flags read from the game into tracker changes. Each location and item
/// placement is credited once per seed, and only ever adds to the player's progress.
/// </summary>
public sealed class AutoTracker(GameData data, TrackerState state, SeedFlags? seed)
{
    private readonly IReadOnlyDictionary<string, Item> _items = data.Items.ToDictionary(i => i.Name);

    /// <summary>The flags that could still change something.</summary>
    public IReadOnlyList<uint> PendingFlags()
    {
        var flags = new HashSet<uint>();
        foreach (var location in data.Locations)
        {
            if (!state.AutoCredited.Contains(LocationKey(location)))
                flags.UnionWith(FlagsFor(location));
        }

        foreach (var placement in seed?.Placements ?? [])
        {
            if (!state.AutoCredited.Contains(ItemKey(placement)))
                flags.Add(placement.FlagId);
        }
        return flags.ToList();
    }

    /// <summary>Applies flags that are on, returning whether the state changed.</summary>
    public bool Apply(IReadOnlySet<uint> flagsOn)
    {
        var changed = false;
        foreach (var location in data.Locations)
        {
            if (FlagsFor(location).Any(flagsOn.Contains) && state.AutoCredited.Add(LocationKey(location)))
            {
                state.CheckedLocations.Add(location.Id);
                changed = true;
            }
        }

        foreach (var placement in seed?.Placements ?? [])
        {
            if (flagsOn.Contains(placement.FlagId) && state.AutoCredited.Add(ItemKey(placement)))
            {
                var owned = state.Items.GetValueOrDefault(placement.TrackedItem);
                state.Items[placement.TrackedItem] = Math.Min(owned + placement.Count, _items[placement.TrackedItem].MaxCount);
                changed = true;
            }
        }
        return changed;
    }

    private IEnumerable<uint> FlagsFor(Location location)
    {
        if (seed is not null && seed.LocationFlags.TryGetValue(location.Id, out var seedFlags))
            return seedFlags;
        return location.FlagId != 0 ? [(uint)location.FlagId] : [];
    }

    private static string LocationKey(Location location) => $"location:{location.Id}";

    private static string ItemKey(ItemPlacement placement) => $"item:{placement.CreditKey}";
}
