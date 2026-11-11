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

    /// <summary>
    /// For a drop with no flag: the items the seed put there (normalized); it is ticked when one arrives as a drop.
    /// Spoiler data: never shown. Empty unless the seed puts those items nowhere else, so a find cannot be mistaken.
    /// </summary>
    public IReadOnlySet<string> TrackItems { get; init; } = new HashSet<string>();

    /// <summary>False for checks auto-tracking cannot see (unlimited shop stock, some rewards): ticked by hand.</summary>
    public bool Trackable => Flags.Count > 0 || TrackItems.Count > 0;
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

    /// <summary>Every item name the game has, normalized, so the logic can tell an unlock item from an unknown name.</summary>
    public IReadOnlySet<string> KnownItems { get; init; } = new HashSet<string>();

    /// <summary>Where drops any enemy of a kind can give are listed: they belong to no one place.</summary>
    public const string AnyArea = "Enemy drops anywhere";

    /// <summary>Areas in the order the randomizer lists them, which roughly follows the game.</summary>
    public required IReadOnlyList<string> Places { get; init; }

    public static AllChecksSeed Build(RandomizerLogic logic, IReadOnlyList<SlotEntry> slots, Regulation regulation, IReadOnlySet<string> options,
        IReadOnlyList<EnemyTypeDrop>? drops = null, IReadOnlyDictionary<string, int>? placements = null, IEnumerable<string>? knownItems = null)
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

        // Drops any enemy of a kind can give: one check per enemy and replaced item (an enemy's variants each have a
        // line). No flag marks them, so one is ticked when its item arrives as a drop, if the seed puts every copy of
        // that item in this check, so a find elsewhere cannot be mistaken for it.
        foreach (var group in (drops ?? []).GroupBy(d => (d.Enemy, d.Replaces)))
        {
            var (enemy, replaced) = group.Key;
            var items = group.GroupBy(d => d.Item).ToList();
            var onlyHere = placements is not null && items.All(i => placements.GetValueOrDefault(i.Key) == i.Count());
            var slot = new SlotEntry($"drop:{RandomizerLogic.Normalize(enemy)}:{RandomizerLogic.Normalize(replaced)}", false, 0, "",
                $"Dropped by any {enemy}", replaced, CheckKinds.EnemyDrop, "", false, new HashSet<string>());
            checks.Add(new SeedCheck(slot, AnyArea, []) { TrackItems = onlyHere ? items.Select(i => ItemKey(i.Key)).ToHashSet() : new HashSet<string>() });
        }

        var order = logic.AreaNames.Values.Distinct().Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return new AllChecksSeed
        {
            Logic = logic,
            Options = options,
            Checks = checks,
            Places = checks.Select(c => c.Place).Distinct().OrderBy(p => order.GetValueOrDefault(p, int.MaxValue)).ToList(),
            KnownItems = (knownItems ?? []).Select(ItemKey).ToHashSet(),
        };
    }

    /// <summary>"Rogier's Rapier +8", "Golden Rune [2] x3", "Smithing Stone [1] 3x" all match by name: "rogiersrapier".</summary>
    public static string ItemKey(string name)
    {
        name = SpoilerLogParser.StripCount(name.Trim());
        var space = name.LastIndexOf(' ');
        if (space > 0 && name.Length > space + 2 && name[space + 1] is '+' or 'x' && name[(space + 2)..].All(char.IsDigit))
            name = name[..space];
        return RandomizerLogic.Normalize(name);
    }

    /// <summary>The names of what the run has received, normalized (see <see cref="ItemKey"/>); only drops and the like if asked.</summary>
    public static HashSet<string> Received(TrackerState state, bool dropsOnly) =>
        state.Found.Where(f => !dropsOnly || f.Source == FoundItem.DropOrOther).SelectMany(f => f.Items).Select(ItemKey).ToHashSet();

    public static string CreditKey(SeedCheck check) => $"all:{check.Id}";

    /// <summary>The flags that could still tick something.</summary>
    public IEnumerable<uint> PendingFlags(TrackerState state) =>
        Checks.Where(c => c.Trackable && !state.AutoCredited.Contains(CreditKey(c))).SelectMany(c => c.Flags).Distinct();

    /// <summary>Ticks every check whose flag is on, once each, so unticking one by hand sticks. Returns how many changed.</summary>
    public int Apply(TrackerState state, IReadOnlySet<uint> flagsOn)
    {
        var changed = 0;
        HashSet<string>? dropped = null;
        foreach (var check in Checks)
        {
            var found = check.Flags.Any(flagsOn.Contains);
            if (!found && check.TrackItems.Count > 0)
                found = check.TrackItems.Overlaps(dropped ??= Received(state, dropsOnly: true));
            if (found && state.AutoCredited.Add(CreditKey(check)) && state.AllChecked.Add(check.Id))
                changed++;
        }
        return changed;
    }
}

/// <summary>
/// Answers "is this area open?" with the randomizer's logic, from the items and settings in a run. Results are cached,
/// so make a new one (or call <see cref="Invalidate"/>) after the run changes.
/// </summary>
/// <param name="data">The tracker's items; null with a null state to assume every item is owned.</param>
/// <param name="knownItems">Every item name the game has, normalized (<see cref="AllChecksSeed.KnownItems"/>).</param>
public sealed class AllChecksLogic(RandomizerLogic logic, IReadOnlySet<string> options, GameData? data, TrackerState? state,
    IReadOnlySet<string>? knownItems = null)
{
    private const string GreatRunes = "Great Runes";
    private readonly Dictionary<string, bool> _known = [];
    private readonly Dictionary<string, Requirement> _parsed = [];
    private readonly Dictionary<string, Item> _items = data?.Items.ToDictionary(i => RandomizerLogic.Normalize(i.Name)) ?? [];
    private readonly HashSet<string> _unknown = [];
    private HashSet<string>? _received;

    /// <summary>Names the logic used that nothing defines; they count as met. For diagnostics.</summary>
    public IReadOnlySet<string> Unknown => _unknown;

    public void Invalidate() => _known.Clear();

    public LocationStatus StatusOf(SeedCheck check)
    {
        if (state?.AllChecked.Contains(check.Id) == true)
            return LocationStatus.Checked;
        // A drop of every enemy of a kind: such enemies are in many places, so it counts as open.
        if (check.Slot.Area.Length == 0)
            return LocationStatus.Available;
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
        // An item the tracker has no button for, such as a bell bearing or a prayer book: owned once it was found.
        if (knownItems?.Contains(baseName) == true)
            return HasReceived(baseName);

        _unknown.Add(name);
        return true;
    }

    // Without pickup tracking nothing is known to be found, so such items count as owned rather than lock things for good.
    private bool HasReceived(string item) =>
        state is null || !state.PickupsScanned || (_received ??= AllChecksSeed.Received(state, dropsOnly: false)).Contains(item);

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
        var baseName = configName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (!_items.TryGetValue(RandomizerLogic.Normalize(itemName), out var tracked) && !_items.TryGetValue(baseName, out tracked))
        {
            var key = AllChecksSeed.ItemKey(itemName);
            if (knownItems?.Contains(key) == true)
                return HasReceived(key);
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
