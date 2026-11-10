using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Logic;

/// <summary>A goal step and its time, beside the same step in the fastest finished run with this goal.</summary>
/// <param name="Seconds">Played time when it was reached; negative when it was reached before timing started.</param>
/// <param name="Best">The best run's time at the same step (by number, as a randomizer's order varies), when known.</param>
public sealed record SplitTime(int Number, string Name, double Seconds, double? Best);

/// <summary>
/// Remembers when things happened in a run: when each check was ticked, the order key items were found, and the
/// steps toward the goal (splits). Kept in the run's save, so finished runs keep their splits for comparison.
/// </summary>
public static class RunTimeline
{
    /// <summary>The time given to things that happened before a run had a timeline (saves made by older versions).</summary>
    public const double Before = -1;

    /// <summary>Brings the timeline up to date with the run. Returns whether anything changed.</summary>
    /// <param name="bossArenas">Where each boss is in this seed, for "defeat chosen bosses" splits.</param>
    public static bool Update(GameData data, TrackerState state, LogicEvaluator logic, IReadOnlyDictionary<string, string>? bossArenas)
    {
        // A run saved before timelines existed: what it already has gets no time rather than a wrong one.
        var now = state.TimelineStarted ? state.PlayedSeconds : Before;
        var changed = !state.TimelineStarted;
        state.TimelineStarted = true;
        foreach (var id in state.CheckedLocations)
            changed |= state.CheckedAt.TryAdd(id, now);
        // An untick is a correction, so its time goes with it.
        foreach (var id in state.CheckedAt.Keys.Where(id => !state.CheckedLocations.Contains(id)).ToList())
            changed |= state.CheckedAt.Remove(id);

        // Key items only ever add up: one spent (a Stonesword Key) was still found.
        foreach (var item in data.Items.Where(i => i.Category == "key"))
        {
            var owned = state.Items.GetValueOrDefault(item.Name);
            var recorded = state.KeyItemsFound.Count(e => BaseName(e.Name) == item.Name);
            for (var n = recorded + 1; n <= owned; n++)
            {
                state.KeyItemsFound.Add(new TimedEvent { Name = item.MaxCount > 1 ? $"{item.Name} {n}" : item.Name, Seconds = now });
                changed = true;
            }
        }

        var splits = Splits(data, state, logic, bossArenas);
        if (!splits.Select(s => (s.Name, s.Seconds)).SequenceEqual(state.Splits.Select(s => (s.Name, s.Seconds))))
        {
            state.Splits = splits.ToList();
            changed = true;
        }
        return changed;
    }

    /// <summary>The goal's steps reached so far, in the order they were reached.</summary>
    public static IReadOnlyList<TimedEvent> Splits(GameData data, TrackerState state, LogicEvaluator logic, IReadOnlyDictionary<string, string>? bossArenas)
    {
        var goal = state.Goal;
        if (goal is null)
            return [];
        var steps = new List<TimedEvent>();
        void Check(string id, string name)
        {
            if (state.CheckedAt.TryGetValue(id, out var at))
                steps.Add(new TimedEvent { Name = name, Seconds = at });
        }

        switch (goal.Kind)
        {
            case RunGoals.EldenBeast:
                steps.AddRange(state.KeyItemsFound.Where(e => BaseName(e.Name) == "Great Runes")
                    .Select(e => new TimedEvent { Name = e.Name.Replace("Great Runes", "Great Rune"), Seconds = e.Seconds }));
                Check(RunGoals.EldenBeastLocation, "Elden Beast");
                break;
            case RunGoals.ConsortRadahn:
                foreach (var boss in data.Locations.Where(l => l.Category == LocationCategory.MajorBoss && l.Dlc && l.Id != RunGoals.ConsortRadahnLocation))
                    Check(boss.Id, boss.Name);
                Check(RunGoals.ConsortRadahnLocation, "Promised Consort Radahn");
                break;
            case RunGoals.MajorBosses:
                foreach (var boss in data.Locations.Where(l => l.Category == LocationCategory.MajorBoss && logic.GetStatus(l) != LocationStatus.Excluded))
                    Check(boss.Id, boss.Name);
                break;
            case RunGoals.ChosenBosses:
                var names = data.Locations.ToDictionary(l => l.Id, l => l.Name);
                // The boss is beaten where the seed put it, so its time is that arena's.
                foreach (var id in goal.Bosses.Where(names.ContainsKey).Distinct())
                    Check(bossArenas?.GetValueOrDefault(id) ?? id, names[id]);
                break;
            case RunGoals.KeyItems:
                var relevant = data.Items.Where(i => i.Category == "key" && logic.IsItemRelevant(i)).Select(i => i.Name).ToHashSet();
                steps.AddRange(state.KeyItemsFound.Where(e => relevant.Contains(BaseName(e.Name))));
                break;
            case RunGoals.AllChecks:
                var total = data.Locations.Count(l => logic.GetStatus(l) != LocationStatus.Excluded);
                var times = state.CheckedAt.Values.Order().ToList();
                foreach (var quarter in new[] { 1, 2, 3, 4 })
                {
                    var needed = (int)Math.Ceiling(total * quarter / 4.0);
                    if (needed > 0 && times.Count >= needed)
                        steps.Add(new TimedEvent { Name = $"{quarter * 25}% of checks", Seconds = times[needed - 1] });
                }
                break;
        }
        return steps.OrderBy(s => s.Seconds).ToList();
    }

    /// <summary>The run's splits beside the best run's, step by step.</summary>
    public static IReadOnlyList<SplitTime> Compare(IReadOnlyList<TimedEvent> splits, IReadOnlyList<TimedEvent>? best) =>
        splits.Select((s, i) => new SplitTime(i + 1, s.Name, s.Seconds,
                best is not null && i < best.Count && best[i].Seconds >= 0 && s.Seconds >= 0 ? best[i].Seconds : null))
            .ToList();

    /// <summary>"Great Runes 3" is a "Great Runes"; a name without a number is itself.</summary>
    public static string BaseName(string name)
    {
        var space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), out _) ? name[..space] : name;
    }
}

/// <summary>
/// Counts deaths where they happen, from the game's own death count. The count belongs to the loaded character, so
/// the first reading after loading in is only a starting point, and a count that goes down means another character.
/// </summary>
public sealed class DeathCounter
{
    /// <summary>More deaths than this between two looks is another save being loaded, not dying.</summary>
    private const uint MostAtOnce = 5;

    /// <summary>How long after leaving the world a death still counts (dying is followed by a loading screen).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    private uint? _last;
    private string? _place;
    private DateTime _lastInWorld;

    /// <param name="count">The game's death count, or null when it cannot be read.</param>
    /// <param name="mapName">The player's map name ("Limgrave - Agheel Lake"), or null when not in the world.</param>
    /// <returns>Whether a death was recorded.</returns>
    public bool Update(TrackerState state, uint? count, string? mapName, bool inWorld, DateTime now)
    {
        if (inWorld)
        {
            _lastInWorld = now;
            if (mapName is not null)
                _place = PlaceOf(mapName);
        }
        else if (now - _lastInWorld > Grace)
        {
            // Long enough on the title screen that the next character may be another one.
            _last = null;
            return false;
        }
        if (count is not { } deaths)
            return false;
        if (_last is not { } last || deaths < last || deaths - last > MostAtOnce)
        {
            _last = deaths;
            return false;
        }
        _last = deaths;
        if (deaths == last)
            return false;
        var place = _place ?? "Somewhere";
        state.Deaths[place] = state.Deaths.GetValueOrDefault(place) + (int)(deaths - last);
        return true;
    }

    public void Reset() => _last = null;

    /// <summary>"Limgrave - Agheel Lake" is counted as Limgrave; a dungeon is its own place.</summary>
    public static string PlaceOf(string mapName)
    {
        var dash = mapName.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? mapName[..dash] : mapName;
    }
}
