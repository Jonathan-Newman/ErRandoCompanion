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

    /// <summary>Every item the seed put in this slot (normalized), for ticking it from an arrival on its map. Spoiler data: never shown.</summary>
    public IReadOnlySet<string> GivenItems { get; init; } = new HashSet<string>();

    /// <summary>For a drop of every enemy of a kind: the kind ("Imp"), as the log and the randomizer's model names call it.</summary>
    public string? Enemy { get; init; }

    /// <summary>For a drop of every enemy of a kind: every item the seed put there (normalized). Spoiler data: never shown.</summary>
    public IReadOnlySet<string> DropItems { get; init; } = new HashSet<string>();

    /// <summary>False for checks auto-tracking cannot see (unlimited shop stock, some rewards): ticked by hand.</summary>
    public bool Trackable => Flags.Count > 0 || TrackItems.Count > 0 || DropItems.Count > 0;
}

/// <summary>
/// What the kills say about a drop: how many enemies of its kind were killed, the chance each kill had of giving it,
/// and the chance it would still not have dropped after that many. Null chances when no kill could have given it.
/// </summary>
public sealed record DropOdds(int Kills, double? PerKill, double? StillMissing)
{
    /// <summary>Below this chance of still missing, the drop is overdue: it would usually have dropped by now.</summary>
    public const double Overdue = 0.1;

    public bool IsOverdue => StillMissing is < Overdue;

    /// <summary>"23 killed  ·  5% a kill  ·  31% chance of no drop yet", or null before any kill.</summary>
    public string? Text()
    {
        if (Kills == 0)
            return null;
        var text = $"{Kills} killed";
        if (PerKill is { } p)
            text += $"  ·  {Percent(p)} a kill";
        if (StillMissing is { } missing)
            text += IsOverdue ? $"  ·  overdue: only {Percent(missing)} chance of no drop by now" : $"  ·  {Percent(missing)} chance of no drop yet";
        return text;
    }

    private static string Percent(double value) => value switch
    {
        >= 0.995 => "100%",
        >= 0.1 => $"{value * 100:0}%",
        >= 0.001 => $"{value * 100:0.#}%",
        _ => "under 0.1%",
    };
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

    /// <summary>
    /// Each enemy kind's (NpcParam row's) drop table: its rows, each rolled on a kill, as (item, chance) entries.
    /// Spoiler data, for counting and odds only: never shown.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<(string Item, double Chance)>>> DropTables { get; init; } =
        new Dictionary<int, IReadOnlyList<IReadOnlyList<(string Item, double Chance)>>>();

    /// <summary>
    /// The names kills are given (the randomizer's model names). A drop whose enemy is one of them takes only kills of
    /// that name; the few the log names otherwise (horses, strays, crabs) take any kill whose table could give them.
    /// </summary>
    public IReadOnlySet<string> KindNames { get; init; } = new HashSet<string>();

    /// <summary>Whether a kill of this enemy kind (NpcParam row) could have given this drop.</summary>
    public bool CouldDrop(int npcParam, SeedCheck check, TrackerState state)
    {
        if (check.Enemy is null || ChancePerKill(npcParam, check.DropItems) <= 0)
            return false;
        return state.KillKinds.GetValueOrDefault(npcParam) == check.Enemy || !KindNames.Contains(check.Enemy);
    }

    /// <summary>Areas in the order the randomizer lists them, which roughly follows the game.</summary>
    public required IReadOnlyList<string> Places { get; init; }

    public static AllChecksSeed Build(RandomizerLogic logic, IReadOnlyList<SlotEntry> slots, Regulation regulation, IReadOnlySet<string> options,
        IReadOnlyList<EnemyTypeDrop>? drops = null, IReadOnlyDictionary<string, int>? placements = null, IEnumerable<string>? knownItems = null,
        IReadOnlyDictionary<(int Category, int Id), string>? itemNames = null, IEnumerable<string>? kindNames = null)
    {
        var shopFlags = regulation.ShopRows
            .Where(s => s.Flag != 0 && s.Quantity > 0)
            .GroupBy(s => s.RowId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Flag).Distinct().ToList());

        // An item the seed puts in only one place tells which check it came from when it arrives without a flag (a
        // guest in Seamless Co-op picks things up without their flags being set).
        var counts = placements is null ? null : ReceivedTracker.PlacementsByKey(placements);
        IReadOnlySet<string> UniqueItems(IEnumerable<string> names) =>
            counts is null ? new HashSet<string>() : names.Select(ItemKey).Where(k => counts.GetValueOrDefault(k) == 1).ToHashSet();
        var shopItems = regulation.ShopRows.GroupBy(s => s.RowId).ToDictionary(g => g.Key, g => g.ToList());

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
            var given = itemNames is null
                ? []
                : slot.IsShop
                    ? (shopItems.GetValueOrDefault(slot.Row) ?? []).Select(s => ItemPickups.NameOf(itemNames, ReceivedTracker.ShopCategory(s.ItemType), s.ItemId))
                    : (regulation.Lots.GetValueOrDefault(slot.Row) ?? []).Where(e => e.Category is >= 1 and <= 6)
                        .Select(e => ItemPickups.NameOf(itemNames, e.Category, e.ItemId));
            var givenList = given.ToList();
            checks.Add(new SeedCheck(slot, logic.AreaNames[slot.Area], flags)
            {
                TrackItems = UniqueItems(givenList),
                GivenItems = givenList.Select(ItemKey).ToHashSet(),
            });
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
            checks.Add(new SeedCheck(slot, AnyArea, [])
            {
                TrackItems = onlyHere ? items.Select(i => ItemKey(i.Key)).ToHashSet() : new HashSet<string>(),
                Enemy = enemy,
                DropItems = items.Select(i => ItemKey(i.Key)).ToHashSet(),
            });
        }

        // Every enemy kind's drop table, by item name, for counting drops after kills and for the odds.
        var tables = new Dictionary<int, IReadOnlyList<IReadOnlyList<(string Item, double Chance)>>>();
        if (itemNames is not null)
        {
            foreach (var npc in regulation.NpcDropLots.Keys)
            {
                var rows = regulation.DropRows(npc)
                    .Select(row => (IReadOnlyList<(string, double)>)row
                        .Where(e => e.Category is >= 1 and <= 6)
                        .Select(e => (ItemKey(ItemPickups.NameOf(itemNames, e.Category, e.ItemId)), e.Chance))
                        .ToList())
                    .Where(row => row.Count > 0)
                    .ToList();
                if (rows.Count > 0)
                    tables[npc] = rows;
            }
        }

        var order = logic.AreaNames.Values.Distinct().Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return new AllChecksSeed
        {
            Logic = logic,
            Options = options,
            Checks = checks,
            Places = checks.Select(c => c.Place).Distinct().OrderBy(p => order.GetValueOrDefault(p, int.MaxValue)).ToList(),
            KnownItems = (knownItems ?? []).Select(ItemKey).ToHashSet(),
            DropTables = tables,
            KindNames = (kindNames ?? []).ToHashSet(),
        };
    }

    /// <summary>The chance one kill of this enemy kind gives any of these items: each row rolled once.</summary>
    public double ChancePerKill(int npcParam, IReadOnlySet<string> items)
    {
        if (!DropTables.TryGetValue(npcParam, out var rows))
            return 0;
        var none = 1.0;
        foreach (var row in rows)
            none *= 1 - Math.Min(1, row.Where(e => items.Contains(e.Item)).Sum(e => e.Chance));
        return 1 - none;
    }

    /// <summary>
    /// What the run's kills say about a drop of every enemy of a kind: every kill whose drop table could give it counts,
    /// whatever the enemy is called. Null for any other check.
    /// </summary>
    public DropOdds? OddsOf(SeedCheck check, TrackerState state)
    {
        if (check.Enemy is null)
            return null;
        var kills = 0;
        var weightedChance = 0.0;
        var missing = 1.0;
        foreach (var (npc, count) in state.Kills)
        {
            if (!CouldDrop(npc, check, state))
                continue;
            var chance = ChancePerKill(npc, check.DropItems);
            kills += count;
            weightedChance += chance * count;
            missing *= Math.Pow(1 - chance, count);
        }
        if (kills == 0)
            return new DropOdds(0, null, null);
        return new DropOdds(kills, weightedChance / kills, state.AllChecked.Contains(check.Id) ? null : missing);
    }

    /// <summary>
    /// Ticks places from items that arrived with no flag (a guest in Seamless Co-op): when only one unticked place on
    /// the player's map holds the item, that is where it came from. Returns how many checks were ticked.
    /// </summary>
    public int ApplyOnMap(TrackerState state, IEnumerable<string> arrived, string? playerMap)
    {
        if (string.IsNullOrEmpty(playerMap))
            return 0;
        var changed = 0;
        foreach (var item in arrived.Select(i => ItemKey(SpoilerLogParser.StripCount(StripTimes(i)))).Distinct())
        {
            var here = Checks
                .Where(c => c.Slot.Map == playerMap && c.GivenItems.Contains(item)
                    && !state.AllChecked.Contains(c.Id) && !state.AutoCredited.Contains(CreditKey(c)))
                .Take(2)
                .ToList();
            if (here.Count == 1 && state.AutoCredited.Add(CreditKey(here[0])) && state.AllChecked.Add(here[0].Id))
                changed++;
        }
        return changed;
    }

    // "Golden Rune [2] x3", as the Found log writes several, to "Golden Rune [2]".
    private static string StripTimes(string item)
    {
        var split = item.LastIndexOf(" x", StringComparison.Ordinal);
        return split > 0 && int.TryParse(item[(split + 2)..], out _) ? item[..split] : item;
    }

    /// <summary>
    /// Ticks drops of every enemy of a kind from items that just arrived: an item counts for a kind killed in the last
    /// few minutes whose drop table has it. Returns how many checks were ticked.
    /// </summary>
    public int ApplyDrops(TrackerState state, IEnumerable<string> arrived, IReadOnlyList<int> recentKinds)
    {
        var changed = 0;
        foreach (var item in arrived.Select(ItemKey).Distinct())
        {
            foreach (var npc in recentKinds)
            {
                if (!DropTables.TryGetValue(npc, out var rows) || !rows.Any(r => r.Any(e => e.Item == item)))
                    continue;
                // The drop of this kind, by name; or, when the names differ (horses, strays, crabs), the only drop
                // that has this item.
                var kind = state.KillKinds.GetValueOrDefault(npc);
                var candidates = Checks.Where(c => c.Enemy is not null && c.DropItems.Contains(item) && CouldDrop(npc, c, state)).ToList();
                var named = candidates.Where(c => c.Enemy == kind).ToList();
                foreach (var check in named.Count > 0 ? named : candidates.Count == 1 ? candidates : [])
                {
                    if (state.AutoCredited.Add(CreditKey(check)) && state.AllChecked.Add(check.Id))
                        changed++;
                }
            }
        }
        return changed;
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
