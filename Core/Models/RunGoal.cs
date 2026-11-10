using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;

namespace ErRandoCompanion.Core.Models;

/// <summary>What finishes a run. Saved with the run; the run's clock stops when it is met.</summary>
public sealed class RunGoal
{
    /// <summary>One of <see cref="RunGoals.Kinds"/>.</summary>
    public string Kind { get; set; } = RunGoals.None;

    /// <summary>How many, for goals that count (major bosses, key items); null means all of them.</summary>
    public int? Count { get; set; }

    /// <summary>For "defeat chosen bosses": the bosses, by their own boss check, wherever the seed put them.</summary>
    public List<string> Bosses { get; set; } = [];
}

/// <summary>A chosen boss and whether it has been defeated. Never says where it is.</summary>
public sealed record GoalBoss(string Id, string Name, bool Defeated);

/// <param name="Description">"Defeat 5 major bosses", in words for the player.</param>
public sealed record GoalProgress(string Kind, string Description, int Done, int Needed, bool Met)
{
    /// <summary>For "defeat chosen bosses": each chosen boss and whether it is defeated.</summary>
    public IReadOnlyList<GoalBoss> Bosses { get; init; } = [];
}

public static class RunGoals
{
    public const string None = "none";
    public const string EldenBeast = "eldenBeast";
    public const string ConsortRadahn = "consortRadahn";
    public const string MajorBosses = "majorBosses";
    public const string KeyItems = "keyItems";
    public const string AllChecks = "allChecks";
    public const string ChosenBosses = "chosenBosses";

    public const string EldenBeastLocation = "boss_elden_beast_ashen_leyndell";
    public const string ConsortRadahnLocation = "boss_promised_consort_radahn_enir_ilim";

    /// <summary>Every goal, with its name for the player, in the order they are offered.</summary>
    public static readonly IReadOnlyList<(string Kind, string Name, bool Counts)> Kinds =
    [
        (None, "No goal", false),
        (EldenBeast, "Beat the game: defeat the Elden Beast", false),
        (ConsortRadahn, "Beat the DLC: defeat Promised Consort Radahn", false),
        (MajorBosses, "Defeat major bosses", true),
        (ChosenBosses, "Defeat bosses I choose (wherever they are)", false),
        (KeyItems, "Find key items", true),
        (AllChecks, "Tick every check", false),
    ];

    /// <summary>How far the run is toward its goal, or null when it has none.</summary>
    /// <param name="bossArenas">Where each boss is in this seed (from its spoiler log); bosses not in it are in their own arenas.</param>
    public static GoalProgress? Progress(GameData data, TrackerState state, LogicEvaluator logic,
        IReadOnlyDictionary<string, string>? bossArenas = null)
    {
        var goal = state.Goal;
        if (goal is null || goal.Kind == None)
            return null;

        switch (goal.Kind)
        {
            case EldenBeast:
            case ConsortRadahn:
            {
                var id = goal.Kind == EldenBeast ? EldenBeastLocation : ConsortRadahnLocation;
                var done = state.CheckedLocations.Contains(id) ? 1 : 0;
                var name = goal.Kind == EldenBeast ? "Defeat the Elden Beast" : "Defeat Promised Consort Radahn";
                return new GoalProgress(goal.Kind, name, done, 1, done == 1);
            }
            case MajorBosses:
            {
                // Only bosses this run's settings include: no DLC bosses when the DLC is off.
                var bosses = data.Locations
                    .Where(l => l.Category == LocationCategory.MajorBoss && logic.GetStatus(l) != LocationStatus.Excluded)
                    .ToList();
                var done = bosses.Count(l => state.CheckedLocations.Contains(l.Id));
                var needed = Math.Clamp(goal.Count ?? bosses.Count, 1, Math.Max(1, bosses.Count));
                var what = needed == bosses.Count ? "all the major bosses" : $"{needed} major boss{(needed == 1 ? "" : "es")}";
                return new GoalProgress(goal.Kind, $"Defeat {what}", Math.Min(done, needed), needed, done >= needed);
            }
            case KeyItems:
            {
                // Key items the logic uses in this run, counted as found when any of each is owned.
                var keys = data.Items.Where(i => i.Category == "key" && logic.IsItemRelevant(i)).ToList();
                var done = keys.Count(i => state.Items.GetValueOrDefault(i.Name) > 0);
                var needed = Math.Clamp(goal.Count ?? keys.Count, 1, Math.Max(1, keys.Count));
                var what = needed == keys.Count ? "all the key items" : $"{needed} key item{(needed == 1 ? "" : "s")}";
                return new GoalProgress(goal.Kind, $"Find {what}", Math.Min(done, needed), needed, done >= needed);
            }
            case ChosenBosses:
            {
                // A boss is defeated when the arena it stands in is cleared, wherever the seed put it.
                var names = data.Locations.ToDictionary(l => l.Id, l => l.Name);
                var chosen = goal.Bosses.Where(names.ContainsKey).Distinct()
                    .Select(id => new GoalBoss(id, names[id], state.CheckedLocations.Contains(bossArenas?.GetValueOrDefault(id) ?? id)))
                    .ToList();
                var done = chosen.Count(b => b.Defeated);
                var description = chosen.Count == 0
                    ? "Choose the bosses to defeat"
                    : $"Defeat {chosen.Count} chosen boss{(chosen.Count == 1 ? "" : "es")}";
                return new GoalProgress(goal.Kind, description, done, chosen.Count, chosen.Count > 0 && done == chosen.Count) { Bosses = chosen };
            }
            case AllChecks:
            {
                var checks = data.Locations.Where(l => logic.GetStatus(l) != LocationStatus.Excluded).ToList();
                var done = checks.Count(l => state.CheckedLocations.Contains(l.Id));
                return new GoalProgress(goal.Kind, "Tick every check", done, checks.Count, checks.Count > 0 && done >= checks.Count);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Marks the run finished the moment its goal is met, which stops its clock, and unfinished again if it no longer
    /// is (a check unticked by mistake). Returns true when the run has just been finished, for a "run complete" message.
    /// </summary>
    public static bool Update(TrackerState state, GoalProgress? progress, DateTime now)
    {
        var met = progress?.Met == true;
        if (met && state.FinishedAt is null)
        {
            state.FinishedAt = now;
            return true;
        }
        if (!met && state.FinishedAt is not null)
            state.FinishedAt = null;
        return false;
    }
}
