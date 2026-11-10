using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Logic;

/// <summary>One region's checks: open (available, not skipped), skipped, locked and done. Counts only, never contents.</summary>
public sealed record RegionCount(string Region, int Open, int Skipped, int Locked, int Done, int Total);

/// <summary>The longest wait between two ticked checks, and the check that ended it.</summary>
public sealed record Stretch(double From, double To, string EndedWith)
{
    public double Seconds => To - From;
}

/// <summary>A run in numbers, for the end of a run and the history list. Only what the player has done.</summary>
/// <param name="Deaths">Deaths by place, most first.</param>
public sealed record RunReport(
    double PlayedSeconds,
    int Checked,
    int Total,
    int BossesDefeated,
    int DeathCount,
    IReadOnlyList<(string Place, int Count)> Deaths,
    Stretch? Slowest,
    IReadOnlyList<TimedEvent> KeyItems,
    IReadOnlyList<TimedEvent> Splits);

public static class RunReports
{
    /// <summary>Few enough open checks that the run is close to stuck.</summary>
    public const int DeadEndAt = 3;

    /// <summary>Each region's checks, in the data's order, leaving out regions with nothing this run counts.</summary>
    public static IReadOnlyList<RegionCount> Regions(GameData data, TrackerState state, LogicEvaluator logic)
    {
        var counts = new Dictionary<string, (int Open, int Skipped, int Locked, int Done, int Total)>();
        foreach (var location in data.Locations)
        {
            var status = logic.GetStatus(location);
            if (status == LocationStatus.Excluded)
                continue;
            var c = counts.GetValueOrDefault(location.Region);
            c.Total++;
            switch (status)
            {
                case LocationStatus.Checked:
                    c.Done++;
                    break;
                case LocationStatus.Locked:
                    c.Locked++;
                    break;
                default:
                    if (state.Skipped.Contains(location.Id))
                        c.Skipped++;
                    else
                        c.Open++;
                    break;
            }
            counts[location.Region] = c;
        }
        return data.Regions
            .Where(r => counts.ContainsKey(r.Id))
            .Select(r =>
            {
                var c = counts[r.Id];
                return new RegionCount(r.Name, c.Open, c.Skipped, c.Locked, c.Done, c.Total);
            })
            .ToList();
    }

    /// <summary>Available checks that are not skipped: what the player can go and do right now.</summary>
    public static int OpenCount(GameData data, TrackerState state, LogicEvaluator logic) =>
        data.Locations.Count(l => !state.Skipped.Contains(l.Id) && logic.GetStatus(l) == LocationStatus.Available);

    public static RunReport Build(GameData data, TrackerState state, LogicEvaluator logic)
    {
        var names = data.Locations.ToDictionary(l => l.Id, l => l.Name);
        var statuses = data.Locations.Select(l => (l, Status: logic.GetStatus(l))).ToList();
        var deaths = state.Deaths.OrderByDescending(d => d.Value).ThenBy(d => d.Key).Select(d => (d.Key, d.Value)).ToList();
        return new RunReport(
            state.PlayedSeconds,
            statuses.Count(s => s.Status == LocationStatus.Checked),
            statuses.Count(s => s.Status != LocationStatus.Excluded),
            statuses.Count(s => s.Status == LocationStatus.Checked && BossArenas.IsBoss(s.l)),
            deaths.Sum(d => d.Value),
            deaths,
            Slowest(state, names),
            state.KeyItemsFound,
            state.Splits);
    }

    /// <summary>The longest gap between ticks, from the start of the run (or the first timed tick, for older saves).</summary>
    public static Stretch? Slowest(TrackerState state, IReadOnlyDictionary<string, string> names)
    {
        var ticks = state.CheckedAt.Where(c => c.Value >= 0).OrderBy(c => c.Value).ToList();
        var untimed = state.CheckedAt.Values.Any(v => v < 0);
        Stretch? slowest = null;
        double? previous = untimed ? null : 0;
        foreach (var (id, at) in ticks)
        {
            if (previous is { } from && (slowest is null || at - from > slowest.Seconds))
                slowest = new Stretch(from, at, names.GetValueOrDefault(id, id));
            previous = at;
        }
        return slowest;
    }
}
