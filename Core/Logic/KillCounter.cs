using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Logic;

/// <summary>An enemy as the game reader sees it: its handle, its kind (NpcParam row), its model number and HP.</summary>
public readonly record struct EnemySighting(ulong Handle, int NpcParam, int Model, int Hp);

/// <summary>An enemy that died: its kind and its model number, which names the kind ("c3080" is an Imp).</summary>
public readonly record struct Kill(int NpcParam, int Model);

/// <summary>
/// Counts kills from the enemies loaded in the world: one is counted when an enemy seen alive is next seen with no HP.
/// An enemy already dead when first seen (a corpse from before) is not counted, and one that comes back to life (after
/// resting) can be killed again. Not saved: a fresh one starts with each load into the world.
/// </summary>
public sealed class KillCounter
{
    private readonly Dictionary<ulong, (int NpcParam, int Model, bool Alive)> _known = [];

    public IReadOnlyList<Kill> Update(IEnumerable<EnemySighting> enemies)
    {
        var kills = new List<Kill>();
        var seen = new HashSet<ulong>();
        foreach (var enemy in enemies)
        {
            if (enemy.NpcParam <= 0 || !seen.Add(enemy.Handle))
                continue;
            var alive = enemy.Hp > 0;
            // A handle given to a different enemy is a new one.
            if (_known.TryGetValue(enemy.Handle, out var before) && before.NpcParam == enemy.NpcParam)
            {
                if (before.Alive && !alive)
                    kills.Add(new Kill(enemy.NpcParam, enemy.Model));
            }
            _known[enemy.Handle] = (enemy.NpcParam, enemy.Model, alive);
        }
        // Unloaded enemies are forgotten; when they load again they are seen afresh.
        foreach (var gone in _known.Keys.Where(h => !seen.Contains(h)).ToList())
            _known.Remove(gone);
        return kills;
    }

    /// <summary>Forgets every enemy, as when leaving the world (another save, or the title screen).</summary>
    public void Reset() => _known.Clear();

    /// <summary>"c3080", the randomizer's name for a model number, as its ModelName.txt lists them.</summary>
    public static string ModelKey(int model) => $"c{model:D4}";

    /// <summary>The kind's name from the randomizer's model names ("Imp"), or its model key when there is none.</summary>
    public static string KindName(IReadOnlyDictionary<string, string> modelNames, int model) =>
        modelNames.GetValueOrDefault(ModelKey(model)) ?? ModelKey(model);

    /// <summary>Adds kills to a run: the count for each kind, and its name. Returns whether there were any.</summary>
    public static bool Record(TrackerState state, IEnumerable<Kill> kills, IReadOnlyDictionary<string, string> modelNames)
    {
        var any = false;
        foreach (var kill in kills)
        {
            state.Kills[kill.NpcParam] = state.Kills.GetValueOrDefault(kill.NpcParam) + 1;
            state.KillKinds[kill.NpcParam] = KindName(modelNames, kill.Model);
            any = true;
        }
        return any;
    }

    /// <summary>Kills by kind name ("Imp": 23), most first.</summary>
    public static IReadOnlyList<(string Kind, int Kills)> ByKind(TrackerState state) =>
        state.Kills.GroupBy(k => state.KillKinds.GetValueOrDefault(k.Key) ?? $"Enemy {k.Key}")
            .Select(g => (g.Key, g.Sum(k => k.Value)))
            .OrderByDescending(k => k.Item2)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Kills from the last few minutes, so a drop picked up soon after can be put down to the enemy that dropped it.</summary>
public sealed class RecentKills
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly List<(int NpcParam, DateTime At)> _kills = [];

    public void Add(IEnumerable<Kill> kills, DateTime now)
    {
        foreach (var kill in kills)
            _kills.Add((kill.NpcParam, now));
        _kills.RemoveAll(k => now - k.At > Window);
        if (_kills.Count > 200)
            _kills.RemoveRange(0, _kills.Count - 200);
    }

    /// <summary>The kinds killed within the window, newest first.</summary>
    public IReadOnlyList<int> Kinds(DateTime now) =>
        _kills.Where(k => now - k.At <= Window).OrderByDescending(k => k.At).Select(k => k.NpcParam).Distinct().ToList();

    public void Clear() => _kills.Clear();
}
