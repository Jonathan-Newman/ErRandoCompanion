using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Persistence;

/// <param name="Goal">The goal in words, which is also what runs are compared by for best times.</param>
/// <param name="Best">The fastest finished run with this goal.</param>
public sealed record FinishedRun(string SeedName, string Goal, DateTime FinishedAt, double PlayedSeconds, int Checked, double? PerHour, bool Best)
{
    public int Deaths { get; init; }

    /// <summary>The steps toward the goal, as they were reached.</summary>
    public IReadOnlyList<TimedEvent> Splits { get; init; } = [];
}

/// <summary>
/// Finished runs and best times, read from the run saves themselves (a run is finished once its goal is met),
/// so there is no second file to keep in step. A deleted run drops out; restoring its backup brings it back.
/// </summary>
public static class RunHistory
{
    /// <summary>Newest first.</summary>
    public static IReadOnlyList<FinishedRun> Read(TrackerStore store)
    {
        var finished = new List<TrackerState>();
        foreach (var name in store.SeedNames())
        {
            try
            {
                if (store.Load(name) is { FinishedAt: not null, Goal: not null } state)
                    finished.Add(state);
            }
            catch (TrackerStoreException)
            {
                // A damaged save is shown elsewhere; it just has no place here.
            }
        }
        return Build(finished);
    }

    public static IReadOnlyList<FinishedRun> Build(IEnumerable<TrackerState> finished)
    {
        var runs = finished
            .Where(s => s.FinishedAt is not null && s.Goal is not null)
            .Select(s => (State: s, Goal: Describe(s.Goal!)))
            .ToList();
        var best = runs.GroupBy(r => r.Goal).ToDictionary(g => g.Key, g => g.Min(r => r.State.PlayedSeconds));
        return runs
            .OrderByDescending(r => r.State.FinishedAt)
            .Select(r => new FinishedRun(
                r.State.SeedName,
                r.Goal,
                r.State.FinishedAt!.Value,
                r.State.PlayedSeconds,
                r.State.CheckedLocations.Count,
                RunClock.PerHour(r.State.CheckedLocations.Count, r.State.PlayedSeconds),
                r.State.PlayedSeconds <= best[r.Goal])
            {
                Deaths = r.State.Deaths.Values.Sum(),
                Splits = r.State.Splits,
            })
            .ToList();
    }

    /// <summary>The splits of the fastest finished run with this goal (other than this run), to race against.</summary>
    public static IReadOnlyList<TimedEvent>? BestSplits(IEnumerable<FinishedRun> runs, RunGoal? goal, string seedName)
    {
        if (goal is null || goal.Kind == RunGoals.None)
            return null;
        var described = Describe(goal);
        return runs.Where(r => r.Goal == described && r.SeedName != seedName && r.Splits.Count > 0)
            .MinBy(r => r.PlayedSeconds)?.Splits;
    }

    /// <summary>A goal in words without needing the run's data: "Defeat 5 major bosses", "Find all the key items".</summary>
    public static string Describe(RunGoal goal) => goal.Kind switch
    {
        RunGoals.EldenBeast => "Defeat the Elden Beast",
        RunGoals.ConsortRadahn => "Defeat Promised Consort Radahn",
        RunGoals.MajorBosses => goal.Count is { } n ? $"Defeat {n} major boss{(n == 1 ? "" : "es")}" : "Defeat all the major bosses",
        RunGoals.KeyItems => goal.Count is { } n ? $"Find {n} key item{(n == 1 ? "" : "s")}" : "Find all the key items",
        RunGoals.ChosenBosses => $"Defeat {goal.Bosses.Count} chosen boss{(goal.Bosses.Count == 1 ? "" : "es")}",
        RunGoals.AllChecks => "Tick every check",
        _ => "No goal",
    };
}
