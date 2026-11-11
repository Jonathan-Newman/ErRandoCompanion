using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.AllChecks;

/// <summary>
/// One check of the full list: a slot, the area it is shown under, and the flags the game sets when it is taken.
/// The flags reveal nothing on their own and are never shown. No check says what it holds.
/// </summary>
public sealed record SeedCheck(SlotEntry Slot, string Place, IReadOnlyList<uint> Flags)
{
    public string Id => Slot.Id;

    /// <summary>False for checks auto-tracking cannot see (unlimited shop stock, some drops): ticked by hand.</summary>
    public bool Trackable => Flags.Count > 0;
}

/// <summary>
/// Every check the randomizer could fill in one seed: all of its item slots that the seed's options include, with
/// the randomizer's own logic for which are reachable. Built from the randomizer folder and the seed's regulation.bin;
/// the spoiler log only gives the options.
/// </summary>
public sealed class AllChecksSeed
{
    public required RandomizerLogic Logic { get; init; }
    public required IReadOnlySet<string> Options { get; init; }
    public required IReadOnlyList<SeedCheck> Checks { get; init; }

    /// <summary>Areas in the order the randomizer lists them, which roughly follows the game.</summary>
    public required IReadOnlyList<string> Places { get; init; }

    public static AllChecksSeed Build(RandomizerLogic logic, IReadOnlyList<SlotEntry> slots, Regulation regulation, IReadOnlySet<string> options)
    {
        var shopFlags = regulation.ShopRows
            .Where(s => s.Flag != 0 && s.Quantity > 0)
            .GroupBy(s => s.RowId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Flag).Distinct().ToList());

        // An area no amount of items can open in this seed (the DLC with the DLC off) has no checks in it.
        var possible = new AllChecksLogic(logic, options, null, null);
        var checks = new List<SeedCheck>();
        foreach (var slot in slots)
        {
            if (!logic.AreaNames.ContainsKey(slot.Area) || !possible.IsReachable(slot.Area))
                continue;
            IReadOnlyList<uint> flags = slot.IsShop
                ? shopFlags.GetValueOrDefault(slot.Row) ?? []
                : (regulation.Lots.GetValueOrDefault(slot.Row) ?? []).Select(e => e.Flag).Where(f => f != 0).Distinct().ToList();
            checks.Add(new SeedCheck(slot, logic.AreaNames[slot.Area], flags));
        }

        var order = logic.AreaNames.Values.Distinct().Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return new AllChecksSeed
        {
            Logic = logic,
            Options = options,
            Checks = checks,
            Places = checks.Select(c => c.Place).Distinct().OrderBy(p => order.GetValueOrDefault(p, int.MaxValue)).ToList(),
        };
    }

    public static string CreditKey(SeedCheck check) => $"all:{check.Id}";

    /// <summary>The flags that could still tick something.</summary>
    public IEnumerable<uint> PendingFlags(TrackerState state) =>
        Checks.Where(c => c.Trackable && !state.AutoCredited.Contains(CreditKey(c))).SelectMany(c => c.Flags).Distinct();

    /// <summary>Ticks every check whose flag is on, once each, so unticking one by hand sticks. Returns how many changed.</summary>
    public int Apply(TrackerState state, IReadOnlySet<uint> flagsOn)
    {
        var changed = 0;
        foreach (var check in Checks)
        {
            if (check.Trackable && check.Flags.Any(flagsOn.Contains) && state.AutoCredited.Add(CreditKey(check)))
            {
                if (state.AllChecked.Add(check.Id))
                    changed++;
            }
        }
        return changed;
    }
}

/// <summary>
/// Answers "is this area open?" with the randomizer's logic, from the items and settings in a run. Results are cached,
/// so make a new one (or call <see cref="Invalidate"/>) after the run changes.
/// </summary>
/// <param name="data">The tracker's items; null with a null state to assume every item is owned.</param>
public sealed class AllChecksLogic(RandomizerLogic logic, IReadOnlySet<string> options, GameData? data, TrackerState? state)
{
    private const string GreatRunes = "Great Runes";
    private readonly Dictionary<string, bool> _known = [];
    private readonly Dictionary<string, Requirement> _parsed = [];
    private readonly Dictionary<string, Item> _items = data?.Items.ToDictionary(i => RandomizerLogic.Normalize(i.Name)) ?? [];
    private readonly HashSet<string> _unknown = [];

    /// <summary>Names the logic used that nothing defines; they count as met. For diagnostics.</summary>
    public IReadOnlySet<string> Unknown => _unknown;

    public void Invalidate() => _known.Clear();

    public LocationStatus StatusOf(SeedCheck check)
    {
        if (state?.AllChecked.Contains(check.Id) == true)
            return LocationStatus.Checked;
        return IsReachable(check.Slot.Area) && Holds(Parse(check.Slot.QuestRequirements)) ? LocationStatus.Available : LocationStatus.Locked;
    }

    public bool IsReachable(string area) => IsTrue(area);

    private Requirement Parse(string text)
    {
        if (!_parsed.TryGetValue(text, out var parsed))
            _parsed[text] = parsed = Requirement.Parse(text);
        return parsed;
    }

    private bool Holds(Requirement requirement) => requirement switch
    {
        Requirement.Name n => IsTrue(n.Value),
        Requirement.All all => all.Parts.All(Holds),
        Requirement.Any any => any.Parts.Any(Holds),
        _ => true,
    };

    private bool IsTrue(string name)
    {
        if (_known.TryGetValue(name, out var known))
            return known;
        // Provisionally false, so a loop in the logic ends instead of overflowing.
        _known[name] = false;
        return _known[name] = Evaluate(name);
    }

    private bool Evaluate(string name)
    {
        switch (name)
        {
            case "true":
                return true;
            case "false":
                return false;
            // Great Rune counts come from the run's settings, as the randomizer takes them from its options.
            case "runes_leyndell":
                return Runes() >= Setting("leyndellRunes", 2);
            case "runes_rold":
                return IsTrue("roldmedallion") && Runes() >= Setting("mountaintopsRunes", 0);
            case "runes_rold_event":
                return IsTrue("roldmedallion");
            case "runes_randomania":
                return true;
        }

        if (logic.Requirements.TryGetValue(name, out var requirement))
            return Holds(Parse(requirement));
        if (logic.Variables.TryGetValue(name, out var variable))
            return Holds(Parse(variable));
        if (logic.Optional.Contains(name))
            return options.Contains(name);
        foreach (var group in logic.OneOf)
        {
            if (group.Contains(name))
                return name == (group.FirstOrDefault(options.Contains) ?? group[0]);
        }
        if (logic.ItemNames.TryGetValue(name, out var item))
            return Owns(name, item);
        // Copies get numbered names the file never declares ("imbuedswordkey2"); a tracked item may match by name.
        var baseName = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (logic.ItemNames.TryGetValue(baseName, out item))
            return Owns(name, item);
        if (_items.TryGetValue(baseName, out var direct))
            return Owns(name, direct.Name);

        _unknown.Add(name);
        return true;
    }

    private bool Owns(string configName, string itemName)
    {
        if (state is null)
            return true;
        if (itemName.Contains("Great Rune", StringComparison.Ordinal))
        {
            // Auto-tracking knows which runes were found; runes added by hand could be any, so they count for all.
            var credited = state.AutoCredited.Count(k => k.StartsWith("item:", StringComparison.Ordinal) && k.Contains("Great Rune", StringComparison.Ordinal));
            return state.AutoCredited.Contains($"item:{itemName}") || Runes() > credited;
        }
        if (!_items.TryGetValue(RandomizerLogic.Normalize(itemName), out var tracked))
        {
            _unknown.Add(configName);
            return true;
        }
        // Several copies have numbered names ("imbuedswordkey2"); the tracker may count fewer copies than that.
        var copy = configName.Length > 0 && char.IsDigit(configName[^1]) ? configName[^1] - '0' : 1;
        return state.Items.GetValueOrDefault(tracked.Name) >= Math.Min(copy, tracked.MaxCount);
    }

    private int Runes() => state?.Items.GetValueOrDefault(GreatRunes) ?? int.MaxValue;

    private int Setting(string id, int fallback) =>
        state is not null && int.TryParse(state.Settings.GetValueOrDefault(id), out var value) ? value : state is null ? 0 : fallback;
}
