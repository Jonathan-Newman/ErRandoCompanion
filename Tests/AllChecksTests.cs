using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;
using Xunit.Abstractions;

namespace ErRandoCompanion.Tests;

public class AllChecksTests(ITestOutputHelper output)
{
    private const string Annotations = """
        ConfigItems:
        - GroupName: keyitems
          Items:
          - Name: Rusty Key
          - ConfigName: runegodrick
            Name: Godrick's Great Rune
          - ConfigName: imbuedswordkey1
            Name: Imbued Sword Key
        Config:
        - Opt: dlc
        - Oneof:
          - noliurniagate
          - liurniagate
        ConfigVars:
          runes_leyndell: runegodrick runerennala
        Events:
        - Name: godrick
          Req: castle_boss
        Areas:
        - Name: start
          Text: Limgrave
        - Name: castle
          Text: Stormveil Castle
          Req: start AND (rustykey OR noliurniagate)
        - Name: castle_boss
          Text: Stormveil Castle
          Req: castle
        - Name: tower
          Text: Divine Tower of Limgrave
          Req: castle AND godrick AND runegodrick
        - Name: capital
          Text: Leyndell
          Req: runes_leyndell
        - Name: land_of_shadow
          Text: Gravesite Plain
          Req: dlc AND start
        """;

    private const string Slots = """
        Slots:
        - Key: 'a'
          DebugText:
          - Iron Whetblade - lot 100[asset (Corpse) in m10_00_00_00 (Stormveil Castle)] Iron Whetblade
          - By Rampart Tower (Stormveil Castle) - 19.25 away, 2.26 o'clock, -32.67 height offset
          Text: On a corpse in front of the fireplace
          Area: castle
          Tags: outoftheway
        - Key: 'b'
          DebugText:
          - Buckler - shop 200[enemy in m11_10_00_00 (Roundtable Hold)]
          - Caestus - shop 201[enemy in m11_10_00_00 (Roundtable Hold)]
          Text: Sold by Gostoc
          Area: start
          Tags: ''
        - Key: 'c'
          DebugText:
          - Godrick's Great Rune - lot 300[event enemy (Godrick) in m10_00_00_00 (Stormveil Castle)] Godrick's Great Rune
          Text: Dropped by Godrick the Grafted
          Area: castle_boss
          Event: godrick
          Tags: boss
        - Key: 'd'
          DebugText:
          - Golden Rune [1] - lot 400[asset (Corpse) in m60_00_00_00 (Limgrave)] Golden Rune [1]
          - By The First Step (Limgrave) - 120.4 away, 3 o'clock, 8 height offset
          Text: aaaaaaaaaaaaaaaa
          Area: start
          Tags: auto
        - Key: 'e'
          DebugText:
          - Flask - lot 500[asset in m10_00_00_00 (Stormveil Castle)] Flask
          Text: Never moved
          Area: castle
          Tags: norandom
        - Key: 'f'
          DebugText:
          - Shadow Thing - lot 600[asset in m61_00_00_00 (Gravesite Plain)] Shadow Thing
          Text: In the Land of Shadow
          Area: land_of_shadow
        """;

    private static readonly RandomizerLogic Logic = RandomizerLogic.Parse(Annotations);

    [Fact]
    public void ParsesRequirements()
    {
        var parsed = Requirement.Parse("a AND (b OR c) AND d");
        var all = Assert.IsType<Requirement.All>(parsed);
        Assert.Equal(3, all.Parts.Count);
        Assert.IsType<Requirement.Any>(all.Parts[1]);
        Assert.IsType<Requirement.All>(Requirement.Parse("godrick thestormhawkking"));
        Assert.Same(Requirement.Always, Requirement.Parse(""));
    }

    [Fact]
    public void ReadsTheRandomizersLogic()
    {
        Assert.Equal("Stormveil Castle", Logic.AreaNames["castle"]);
        Assert.Equal("castle_boss", Logic.Requirements["godrick"]);
        Assert.Equal("Godrick's Great Rune", Logic.ItemNames["runegodrick"]);
        Assert.Equal("Rusty Key", Logic.ItemNames["rustykey"]);
        Assert.Contains("dlc", Logic.Optional);
        Assert.Equal(["noliurniagate", "liurniagate"], Logic.OneOf[0]);
    }

    [Fact]
    public void ReadsSlotsOnePerLotOrShopRow()
    {
        var slots = SlotCatalog.Parse(Slots);
        Assert.Equal(["lot:100", "shop:200", "shop:201", "lot:300", "lot:400", "lot:600"], slots.Select(s => s.Id));
        Assert.Equal(CheckKinds.Shop, slots[1].Kind);
        Assert.Equal("Caestus", slots[2].Replaces);
        Assert.Equal(CheckKinds.Boss, slots[3].Kind);
        // No written description: the nearest grace stands in.
        Assert.Equal("Near The First Step, 120 m away, 8 m up", slots[4].Description);
    }

    [Fact]
    public void FollowsTheRandomizersLogic()
    {
        var data = TestData.Load();
        var seed = Build(["liurniagate"]);
        // The Land of Shadow is left out without the DLC.
        Assert.DoesNotContain(seed.Checks, c => c.Id == "lot:600");
        Assert.Contains(Build(["dlc"]).Checks, c => c.Id == "lot:600");

        var state = TrackerState.Create("test", data);
        var logic = new AllChecksLogic(Logic, seed.Options, data, state);
        Assert.False(logic.IsReachable("castle"));
        Assert.Equal(LocationStatus.Available, logic.StatusOf(seed.Checks.Single(c => c.Id == "shop:200")));

        state.Items["Rusty Key"] = 1;
        logic = new AllChecksLogic(Logic, seed.Options, data, state);
        Assert.True(logic.IsReachable("castle"));
        Assert.False(logic.IsReachable("tower"));

        // Any rune added by hand counts for Godrick's; one auto-tracked rune counts only for itself.
        state.Items["Great Runes"] = 1;
        Assert.True(new AllChecksLogic(Logic, seed.Options, data, state).IsReachable("tower"));
        state.AutoCredited.Add("item:Radahn's Great Rune");
        Assert.False(new AllChecksLogic(Logic, seed.Options, data, state).IsReachable("tower"));

        // Leyndell follows the run's Great Rune setting.
        state.Settings["leyndellRunes"] = "1";
        Assert.True(new AllChecksLogic(Logic, seed.Options, data, state).IsReachable("capital"));
        Assert.Empty(logic.Unknown);
    }

    [Fact]
    public void TicksChecksFromFlagsOnce()
    {
        var seed = Build([]);
        var state = TrackerState.Create("test", TestData.Load());
        Assert.Contains(1000u, seed.PendingFlags(state));
        Assert.Equal(1, seed.Apply(state, new HashSet<uint> { 1000 }));
        Assert.Contains("lot:100", state.AllChecked);
        Assert.DoesNotContain(1000u, seed.PendingFlags(state));

        // Unticked by hand, it stays unticked.
        state.AllChecked.Remove("lot:100");
        Assert.Equal(0, seed.Apply(state, new HashSet<uint> { 1000 }));
        // An unlimited shop row sets no flag, so it is ticked by hand.
        Assert.False(seed.Checks.Single(c => c.Id == "shop:201").Trackable);
        Assert.True(seed.Checks.Single(c => c.Id == "shop:200").Trackable);
    }

    [Fact]
    public void ReadsDropsOfEveryEnemyOfAKind()
    {
        string[] lines =
        [
            "Options and seed: dlc seed:1",
            "-- Spoilers:",
            "Shortbow: From Albinauric. Replaces Dirty Chainmail.",
            "Ash of War: Glintblade Phalanx: From Imp. Replaces Imp Head (Cat).",
            "Uchigatana in Limgrave: From Shiny Item. Near The First Step. Replaces Golden Rune [1].",
            "Shortbow in Stormveil Castle: On a corpse. Replaces Iron Whetblade.",
            "-- End of item spoilers",
        ];
        var log = SpoilerLogParser.Parse(lines, "log.txt");
        Assert.Equal(2, log.EnemyTypeDrops.Count);
        Assert.Equal(new EnemyTypeDrop("Ash of War: Glintblade Phalanx", "Imp", "Imp Head (Cat)"), log.EnemyTypeDrops[1]);
        Assert.Equal(2, log.PlacementCounts["Shortbow"]);
        Assert.Equal(1, log.PlacementCounts["Uchigatana"]);
    }

    [Fact]
    public void TicksEnemyDropsFromWhatArrives()
    {
        EnemyTypeDrop[] drops = [new("Glintstone Staff", "Imp", "Imp Head (Cat)"), new("Shortbow", "Albinauric", "Dirty Chainmail")];
        var placements = new Dictionary<string, int> { ["Glintstone Staff"] = 1, ["Shortbow"] = 2 };
        var seed = AllChecksSeed.Build(Logic, SlotCatalog.Parse(Slots), new Regulation { Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(), ShopRows = [] },
            new HashSet<string>(), drops, placements);
        var imp = seed.Checks.Single(c => c.Id == "drop:imp:impheadcat");
        Assert.Equal(AllChecksSeed.AnyArea, imp.Place);
        Assert.Equal("Dropped by any Imp", imp.Slot.Description);
        Assert.True(imp.Trackable);
        // The Shortbow is in two places, so finding one says nothing about this drop: it is ticked by hand.
        Assert.False(seed.Checks.Single(c => c.Id == "drop:albinauric:dirtychainmail").Trackable);

        var state = TrackerState.Create("test", TestData.Load());
        Assert.Equal(LocationStatus.Available, new AllChecksLogic(Logic, seed.Options, TestData.Load(), state).StatusOf(imp));
        // Picked up somewhere does not count; received as a drop does, upgraded or not.
        state.Found.Add(new FoundItem { Items = ["Glintstone Staff"] });
        Assert.Equal(0, seed.Apply(state, new HashSet<uint>()));
        state.Found.Add(new FoundItem { Items = ["Glintstone Staff +3"], Source = FoundItem.DropOrOther });
        Assert.Equal(1, seed.Apply(state, new HashSet<uint>()));
        Assert.Contains(imp.Id, state.AllChecked);
    }

    [Fact]
    public void UnlockItemsAreOwnedOnceFound()
    {
        var logic = RandomizerLogic.Parse(Annotations.Replace("Req: dlc AND start", "Req: start AND bonepeddlersbellbearing"));
        var seed = AllChecksSeed.Build(logic, SlotCatalog.Parse(Slots), new Regulation { Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(), ShopRows = [] },
            new HashSet<string>(), knownItems: ["Bone Peddler's Bell Bearing"]);
        var data = TestData.Load();
        var state = TrackerState.Create("test", data);
        // Before pickups are tracked nothing is known, so the shop is not locked for good.
        Assert.True(new AllChecksLogic(logic, seed.Options, data, state, seed.KnownItems).IsReachable("land_of_shadow"));
        state.PickupsScanned = true;
        Assert.False(new AllChecksLogic(logic, seed.Options, data, state, seed.KnownItems).IsReachable("land_of_shadow"));
        state.Found.Add(new FoundItem { Items = ["Bone Peddler's Bell Bearing"] });
        var after = new AllChecksLogic(logic, seed.Options, data, state, seed.KnownItems);
        Assert.True(after.IsReachable("land_of_shadow"));
        Assert.Empty(after.Unknown);
    }

    private static AllChecksSeed Build(IReadOnlyCollection<string> options)
    {
        var regulation = new Regulation
        {
            Lots = new Dictionary<int, IReadOnlyList<LotEntry>>
            {
                [100] = [new LotEntry(1, 1, 1, 1000)],
                [300] = [new LotEntry(2, 1, 1, 3000)],
            },
            ShopRows = [new ShopRow(1, 3, 2000, 1, 200), new ShopRow(2, 3, 0, -1, 201)],
        };
        return AllChecksSeed.Build(Logic, SlotCatalog.Parse(Slots), regulation, options.ToHashSet());
    }

    /// <summary>The real randomizer folder (ER_TEST_RANDOMIZER) and a spoiler log in it: what the full list holds.</summary>
    [Fact]
    public void BuildsTheFullListForARealSeed()
    {
        var folder = Environment.GetEnvironmentVariable("ER_TEST_RANDOMIZER");
        if (folder is null)
            return;
        var logPath = Directory.GetFiles(Path.Combine(folder, "spoiler_logs"), "*.txt").OrderBy(File.GetLastWriteTimeUtc).Last();
        var log = SpoilerLogParser.Parse(File.ReadAllLines(logPath), logPath);
        var regulation = Regulation.Load(Path.Combine(folder, SeedLoader.RegulationFile), Path.Combine(folder, SeedLoader.OodleFile));
        var seed = SeedLoader.LoadAll(folder, regulation, log);
        Assert.NotNull(seed);

        var data = TestData.Load();
        var state = TrackerState.Create("test", data);
        var logic = new AllChecksLogic(seed.Logic, seed.Options, data, state, seed.KnownItems);
        var available = seed.Checks.Count(c => logic.StatusOf(c) == LocationStatus.Available);
        var anywhere = seed.Checks.Where(c => c.Place == AllChecksSeed.AnyArea).ToList();
        output.WriteLine($"Enemy drops anywhere: {anywhere.Count} ({anywhere.Count(c => c.Trackable)} auto-tracked) from {log.EnemyTypeDrops.Count} log lines");
        Assert.True(anywhere.Count > 100);
        output.WriteLine($"{seed.Checks.Count} checks in {seed.Places.Count} places; {seed.Checks.Count(c => c.Trackable)} auto-tracked; {available} open at the start");
        foreach (var kind in seed.Checks.GroupBy(c => c.Slot.Kind))
            output.WriteLine($"  {kind.Key}: {kind.Count()} ({kind.Count(c => c.Trackable)} auto-tracked)");
        output.WriteLine($"Unknown names: {string.Join(", ", logic.Unknown)}");

        Assert.True(seed.Checks.Count > 3000);
        Assert.True(available > 50 && available < seed.Checks.Count / 2);

        // With every key item, all of it opens.
        foreach (var item in data.Items)
            state.Items[item.Name] = item.MaxCount;
        // Unlock items such as bell bearings are owned once found; this pretends every item has been.
        state.PickupsScanned = false;
        var everything = new AllChecksLogic(seed.Logic, seed.Options, data, state, seed.KnownItems);
        var locked = seed.Checks.Where(c => everything.StatusOf(c) == LocationStatus.Locked).ToList();
        output.WriteLine($"Locked with every item: {locked.Count}: {string.Join("; ", locked.Take(8).Select(c => $"{c.Place} ({c.Slot.Area}, {c.Slot.QuestRequirements})"))}");
        Assert.True(locked.Count < seed.Checks.Count / 50);
    }
}

internal static class TestData
{
    public static GameData Load() => GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
}
