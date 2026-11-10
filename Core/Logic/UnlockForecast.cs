using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Logic;

/// <param name="Opens">Checks that are locked now and would be available with one more of this item.</param>
/// <param name="Regions">Where those checks are, most first (region names only).</param>
public sealed record ItemUnlock(string Item, int Opens, IReadOnlyList<string> Regions);

/// <summary>
/// What each item you are still missing would open up right now. It only asks the logic "what if I had this", whose
/// requirements are public knowledge, so it says nothing about where any item is.
/// </summary>
public static class UnlockForecast
{
    public static IReadOnlyList<ItemUnlock> Compute(GameData data, TrackerState state)
    {
        var now = new LogicEvaluator(data, state);
        var locked = data.Locations.Where(l => now.GetStatus(l) == LocationStatus.Locked).ToList();
        if (locked.Count == 0)
            return [];
        var regionNames = data.Regions.ToDictionary(r => r.Id, r => r.Name);

        var unlocks = new List<ItemUnlock>();
        foreach (var item in data.Items.Where(i => (i.Category == "key" || i.Shortcut) && now.IsItemRelevant(i)))
        {
            var owned = state.Items.GetValueOrDefault(item.Name);
            if (owned >= item.MaxCount)
                continue;

            // The same run with one more of this item; everything else is shared, as the logic only reads it.
            var trial = new TrackerState
            {
                CheckedLocations = state.CheckedLocations,
                Settings = state.Settings,
                Items = new Dictionary<string, int>(state.Items) { [item.Name] = owned + 1 },
            };
            var then = new LogicEvaluator(data, trial);
            var opened = locked.Where(l => then.GetStatus(l) == LocationStatus.Available).ToList();
            if (opened.Count == 0)
                continue;
            var regions = opened.GroupBy(l => regionNames.GetValueOrDefault(l.Region, l.Region))
                .OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            unlocks.Add(new ItemUnlock(item.Name, opened.Count, regions));
        }
        return unlocks.OrderByDescending(u => u.Opens).ThenBy(u => u.Item).ToList();
    }
}
