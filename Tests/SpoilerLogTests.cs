using System.Text;
using System.Text.Json;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

public class SpoilerLogTests
{
    private static readonly string[] SampleLog =
    [
        "Options and seed: dlc raceloc_health runes_leyndell:3 seed:12345",
        "",
        "Version: v0.12.2",
        "-- Spoilers:",
        "Rusty Key in Limgrave: Sold by the Nomadic Merchant. Replaces Cracked Pot.",
        "  (cost: 1600)",
        "Stonesword Key 3x in Limgrave: In a chest. Replaces Bandit Mask.",
        "Dectus Medallion (Left) in Limgrave - Coast, Entrance: From Robed Corpse. Near Cave (Site of Grace) - 83.14 away. Replaces Golden Halberd.",
        "-- End of item spoilers",
        @"Writing to D:\Game\randomizer",
    ];

    private static ParsedSpoilerLog Parse() => SpoilerLogParser.Parse(SampleLog, @"C:\logs\log.txt");

    [Fact]
    public void Parse_ReadsTheHeaderFields()
    {
        var log = Parse();
        Assert.Equal("12345", log.Seed);
        Assert.Equal("v0.12.2", log.Version);
        Assert.Contains("raceloc_health", log.Options);
        Assert.Equal(@"D:\Game\randomizer", log.RandomizerDirectory);
    }

    [Fact]
    public void Parse_RecordsWhichItemsAreSold_IgnoringCounts()
    {
        var log = Parse();
        Assert.True(log.SoldItems["Rusty Key"]);
        Assert.False(log.SoldItems["Stonesword Key"]);
        Assert.False(log.SoldItems["Dectus Medallion (Left)"]);
    }

    [Fact]
    public void Parse_FallsBackToTheFolderAboveSpoilerLogs()
    {
        var log = SpoilerLogParser.Parse(SampleLog[..^1], @"C:\Game\randomizer\spoiler_logs\log.txt");
        Assert.Equal(@"C:\Game\randomizer", log.RandomizerDirectory);
    }

    [Fact]
    public void Parse_RejectsOtherFiles()
    {
        Assert.Throws<SpoilerLogException>(() => SpoilerLogParser.Parse(["just some text"], "x.txt"));
    }

    [Fact]
    public void ApplyOptions_CopiesMatchingSettings_AndLeavesCountsWithoutOptions()
    {
        var state = new TrackerState();
        state.Settings["mountaintopsRunes"] = "4";
        Parse().ApplyOptionsTo(state);

        Assert.Equal("true", state.Settings["dlcEnabled"]);
        Assert.Equal("true", state.Settings["seedsAndTears"]);
        Assert.Equal("false", state.Settings["otherBosses"]);
        Assert.Equal("false", state.Settings["merchantShops"]);
        Assert.Equal("3", state.Settings["leyndellRunes"]);
        Assert.Equal("4", state.Settings["mountaintopsRunes"]);
    }

    private static GameData SmallData(params Location[] locations) => new()
    {
        Locations = locations,
        Regions = [new Region { Id = "r", Name = "R" }],
        Settings = [],
        Items =
        [
            new Item { Name = "Rusty Key", Category = "key" },
            new Item { Name = "Dectus Medallion (Left)", Category = "key" },
            new Item { Name = "Stonesword Key", Category = "key", MaxCount = 99 },
            new Item { Name = "Great Runes", Category = "key", MaxCount = 7 },
            new Item { Name = "Letter from Volcano Manor (Rileigh)", Category = "optional" },
        ],
    };

    private static readonly (int, string)[] Goods =
    [
        (8010, "Rusty Key"), (8101, "Dectus Medallion (Left)"), (8000, "Stonesword Key"),
        (8148, "Godrick's Great Rune"), (8149, "Miquella's Great Rune"), (8132, "Letter from Volcano Manor"),
        (9999, "Golden Rune [1]"),
    ];

    private static Regulation SampleRegulation(params (int Lot, int Item, uint Flag, int Count)[] lots) => new()
    {
        Lots = lots.ToDictionary(
            l => l.Lot,
            l => (IReadOnlyList<LotEntry>)[new LotEntry(l.Item, Regulation.GoodsCategory, l.Count, l.Flag)]),
        ShopRows = [new ShopRow(8010, Regulation.GoodsShopType, 700, 1), new ShopRow(8000, Regulation.GoodsShopType, 0, -1)],
    };

    [Fact]
    public void Build_FindsLotsAndLimitedShopStock_ButNotOtherItems()
    {
        var seed = SeedFlags.Build(SmallData(), Parse(), SampleRegulation((1, 8101, 500, 1), (2, 9999, 501, 1)), Goods);

        Assert.Contains(seed.Placements, p => p.TrackedItem == "Dectus Medallion (Left)" && p.FlagId == 500);
        Assert.Contains(seed.Placements, p => p.TrackedItem == "Rusty Key" && p.FlagId == 700);
        Assert.DoesNotContain(seed.Placements, p => p.FlagId == 501);
        Assert.DoesNotContain(seed.Placements, p => p.FlagId == 0);
    }

    [Fact]
    public void Build_TicksAMerchantAtItsFirstLimitedPurchase_WhateverItSells()
    {
        var kale = new Location { Id = "merchant_kale", Name = "Kalé", Region = "r", Category = LocationCategory.Merchant, ShopRows = [100500, 100501, 100502] };
        var regulation = new Regulation
        {
            Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(),
            ShopRows =
            [
                new ShopRow(9999, Regulation.GoodsShopType, 160010, 1, 100500),   // a one-off item: its purchase flag counts
                new ShopRow(8000, Regulation.GoodsShopType, 0, -1, 100501),       // unlimited stock: no flag
                new ShopRow(9999, Regulation.GoodsShopType, 160030, 3, 100502),   // limited to three: flag once sold out
                new ShopRow(9999, Regulation.GoodsShopType, 170000, 1, 100900),   // someone else's shop
            ],
        };
        var seed = SeedFlags.Build(SmallData(kale), Parse(), regulation, Goods);

        Assert.Equal([160010u, 160030u], seed.LocationFlags["merchant_kale"].Order());
    }

    [Fact]
    public void Build_CountsGreatRunesTogether_ExcludingMiquellas()
    {
        var seed = SeedFlags.Build(SmallData(), Parse(), SampleRegulation((1, 8148, 171, 1), (2, 8149, 199, 1)), Goods);

        Assert.Contains(seed.Placements, p => p.TrackedItem == "Great Runes" && p.FlagId == 171);
        Assert.DoesNotContain(seed.Placements, p => p.FlagId == 199);
    }

    [Fact]
    public void Build_SeparatesTheTwoVolcanoManorLetters()
    {
        var seed = SeedFlags.Build(SmallData(), Parse(), SampleRegulation((1, 8132, 900, 1)), Goods);
        Assert.Contains(seed.Placements, p => p.TrackedItem == "Letter from Volcano Manor (Rileigh)");
    }

    [Fact]
    public void Build_UsesTheSeedsFlagForALocationsLot()
    {
        var location = new Location { Id = "here", Name = "Here", Region = "r", Category = LocationCategory.KeyItem, FlagId = 111, LotIds = [1, 2] };
        var seed = SeedFlags.Build(SmallData(location), Parse(), SampleRegulation((1, 9999, 222, 1)), Goods);
        Assert.Equal([222u], seed.LocationFlags["here"]);
    }

    [Fact]
    public void Build_NoticesALogFromADifferentRun()
    {
        // The sample log sells the Rusty Key, and this regulation does too.
        var matching = SeedFlags.Build(SmallData(), Parse(), SampleRegulation(), Goods);
        Assert.Equal(0, matching.LogMismatches);

        var noShop = new Regulation { Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(), ShopRows = [] };
        Assert.Equal(1, SeedFlags.Build(SmallData(), Parse(), noShop, Goods).LogMismatches);
    }

    [Fact]
    public void Build_ReportsLoggedKeyItemsWithoutAFlag()
    {
        var seed = SeedFlags.Build(SmallData(), Parse(), SampleRegulation((1, 8101, 0, 1)), Goods);
        Assert.Contains("Dectus Medallion (Left)", seed.UntrackableItems);
    }

    [Fact]
    public void Build_DoesNotWarnAboutItemsThatDoNotAffectLogic()
    {
        var data = new GameData
        {
            Locations = [],
            Regions = [],
            Settings = [],
            Items = [new Item { Name = "Dectus Medallion (Left)", Category = "optional" }],
        };
        var seed = SeedFlags.Build(data, Parse(), SampleRegulation(), Goods);
        Assert.Empty(seed.UntrackableItems);
    }

    [Fact]
    public void FromBinder_ReadsLotsAndShops()
    {
        var lotRow = new byte[152];
        BitConverter.GetBytes(8010).CopyTo(lotRow, 0);
        BitConverter.GetBytes(1).CopyTo(lotRow, 32);
        BitConverter.GetBytes(777u).CopyTo(lotRow, 128);
        lotRow[138] = 2;

        var shopRow = new byte[52];
        BitConverter.GetBytes(8000).CopyTo(shopRow, 0);
        BitConverter.GetBytes(888u).CopyTo(shopRow, 12);
        BitConverter.GetBytes((short)5).CopyTo(shopRow, 20);
        shopRow[0x17] = Regulation.GoodsShopType;

        var binder = Binder(
            ("N:\\param\\ItemLotParam_map.param", Param((42, lotRow), (43, new byte[152]))),
            ("N:\\param\\ItemLotParam_enemy.param", Param((50, lotRow), (51, new byte[152]))),
            ("N:\\param\\ShopLineupParam.param", Param((60, shopRow), (61, new byte[52]))));

        var regulation = Regulation.FromBinder(binder);

        var entry = Assert.Single(regulation.Lots[42]);
        Assert.Equal(new LotEntry(8010, 1, 2, 777), entry);
        Assert.Contains(50, regulation.Lots.Keys);
        Assert.Empty(regulation.Lots[43]);
        Assert.Equal(new ShopRow(8000, Regulation.GoodsShopType, 888, 5, RowId: 60), regulation.ShopRows[0]);
    }

    private static byte[] Param(params (int Id, byte[] Row)[] rows)
    {
        const int firstEntry = 0x40, entrySize = 24;
        var dataStart = firstEntry + rows.Length * entrySize;
        var param = new byte[dataStart + rows.Sum(r => r.Row.Length)];
        BitConverter.GetBytes((ushort)rows.Length).CopyTo(param, 0x0A);

        var offset = dataStart;
        for (var i = 0; i < rows.Length; i++)
        {
            var pos = firstEntry + i * entrySize;
            BitConverter.GetBytes(rows[i].Id).CopyTo(param, pos);
            BitConverter.GetBytes((long)offset).CopyTo(param, pos + 8);
            rows[i].Row.CopyTo(param, offset);
            offset += rows[i].Row.Length;
        }
        return param;
    }

    private static byte[] Binder(params (string Name, byte[] File)[] files)
    {
        const int firstHeader = 0x40, headerSize = 0x24;
        var stream = new MemoryStream();
        stream.Write(new byte[firstHeader + files.Length * headerSize]);

        var nameOffsets = files.Select(f =>
        {
            var offset = (int)stream.Position;
            stream.Write(Encoding.Unicode.GetBytes(f.Name + "\0"));
            return offset;
        }).ToList();
        var dataOffsets = files.Select(f =>
        {
            var offset = (int)stream.Position;
            stream.Write(f.File);
            return offset;
        }).ToList();

        var data = stream.ToArray();
        Encoding.ASCII.GetBytes("BND4").CopyTo(data, 0);
        BitConverter.GetBytes(files.Length).CopyTo(data, 0x0C);
        BitConverter.GetBytes((long)headerSize).CopyTo(data, 0x20);
        for (var i = 0; i < files.Length; i++)
        {
            var pos = firstHeader + i * headerSize;
            BitConverter.GetBytes((long)files[i].File.Length).CopyTo(data, pos + 8);
            BitConverter.GetBytes((long)files[i].File.Length).CopyTo(data, pos + 16);
            BitConverter.GetBytes(dataOffsets[i]).CopyTo(data, pos + 24);
            BitConverter.GetBytes(nameOffsets[i]).CopyTo(data, pos + 32);
        }
        return data;
    }

    [Fact]
    public void ShippedData_HasFlagsForEverythingExceptShopsAndInvaders()
    {
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var manual = data.Locations.Where(l => l.FlagId == 0).ToList();

        Assert.Equal(27, manual.Count);
        Assert.Equal(24, manual.Count(l => l.Category == LocationCategory.Merchant));
        Assert.Equal(3, manual.Count(l => l.Category == LocationCategory.ExtraBoss));
        Assert.Equal(data.Locations.Count(l => l.FlagId != 0), data.Locations.Where(l => l.FlagId != 0).Select(l => l.FlagId).Distinct().Count());
    }

    [Fact]
    public void ShippedMapData_PlacesEveryCheckExceptRoundtableHold()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Data");
        var data = GameData.Load(directory);
        var map = MapData.Load(Path.Combine(directory, "map_data.json"));

        Assert.Equal(["base", "underground", "dlc"], map.Maps.Select(m => m.Id));
        Assert.All(map.Pins, p =>
        {
            Assert.InRange(p.U, 0, 1);
            Assert.InRange(p.V, 0, 1);
            Assert.Contains(p.Map, map.Maps.Select(m => m.Id));
        });

        var pinned = map.Pins.Select(p => p.Id).ToHashSet();
        Assert.Equal(map.Pins.Count, pinned.Count);
        Assert.Equal(
            ["merchant_twin_maiden_husks_roundtable_hold"],
            data.Locations.Select(l => l.Id).Where(id => !pinned.Contains(id)));
        Assert.All(map.Maps, m => Assert.True(File.Exists(Path.Combine(directory, m.Image)), m.Image));
    }

    [Fact]
    public void ShippedMapData_PutsDlcChecksOnTheDlcMap()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Data");
        var locations = GameData.Load(directory).Locations.ToDictionary(l => l.Id);
        var map = MapData.Load(Path.Combine(directory, "map_data.json"));

        // Spot checks with known geography: the DLC is its own map, and north is up.
        Assert.Equal("dlc", map.Pins.Single(p => p.Id == "boss_bayle_the_dread_jagged_peak").Map);
        Assert.Equal("base", map.Pins.Single(p => p.Id == "boss_margit_the_fell_omen_stormveil_castle").Map);
        Assert.Equal("underground", map.Pins.Single(p => p.Id == "boss_ancestor_spirit_siofra_river").Map);

        double V(string id) => map.Pins.Single(p => p.Id == id).V;
        Assert.True(V("boss_leonine_misbegotten_weeping_peninsula") > V("boss_margit_the_fell_omen_stormveil_castle"));
        Assert.True(V("boss_margit_the_fell_omen_stormveil_castle") > V("boss_rykard_volcano_manor"));
        Assert.All(map.Pins, p => Assert.True(locations.ContainsKey(p.Id)));
    }

    [Theory]
    [InlineData("boss_rellana_twin_moon_knight_castle_ensis", 0.375, 0.405)]
    [InlineData("boss_putrescent_knight_cerulean_coast", 0.307, 0.929)]
    public void ShippedMapData_PutsDlcBossesOnTheirPlacesInTheArt(string id, double u, double v)
    {
        var map = MapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map_data.json"));
        var pin = map.Pins.Single(p => p.Id == id);

        // Fractions of the DLC sheet, read off the art itself: about 60 metres either way.
        Assert.InRange(pin.U, u - 0.015, u + 0.015);
        Assert.InRange(pin.V, v - 0.015, v + 0.015);
    }

    [Fact]
    public void ShippedMapData_WorldPositionsLandOnTheirOwnPins()
    {
        var map = MapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map_data.json"));
        var sheets = map.Maps.ToDictionary(m => m.Id);

        var misses = map.Pins.Select(p =>
        {
            var sheet = sheets[p.Map];
            Assert.Equal(sheet.Area, p.Area);
            Assert.NotNull(p.World);
            var (u, v) = sheet.ToUv(p.World.X, p.World.Z);
            return Math.Sqrt(Math.Pow((u - p.U) * sheet.WorldWidth, 2) + Math.Pow((v - p.V) * sheet.WorldHeight, 2));
        }).ToList();

        // Only checks that share a spot are drawn apart, and never by more than a few pin widths.
        Assert.All(misses, m => Assert.InRange(m, 0, 60));
        Assert.True(misses.Count(m => m < 1) > map.Pins.Count * 0.9);
    }

    [Fact]
    public void ShippedMapData_GivesDungeonChecksTheirWayIn()
    {
        var map = MapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map_data.json"));

        Assert.All(map.Pins, p =>
        {
            Assert.NotNull(p.MapId);
            var openWorld = p.MapId!.StartsWith("m60_") || p.MapId.StartsWith("m61_");
            Assert.Equal(!openWorld, p.Entrance is not null);
        });

        // Margit waits at Stormveil's gate, so the castle's way in is right beside him.
        var margit = map.Pins.Single(p => p.Id == "boss_margit_the_fell_omen_stormveil_castle");
        Assert.Equal("m10_00_00_00", margit.MapId);
        Assert.NotNull(margit.Local);
        Assert.InRange(Math.Sqrt(Math.Pow(margit.Entrance!.X - margit.World!.X, 2) + Math.Pow(margit.Entrance.Z - margit.World.Z, 2)), 0, 60);
    }

    [Fact]
    public void ShippedSignature_HasTheExpectedShape()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "signatures.json"));
        var signature = JsonDocument.Parse(json).RootElement.GetProperty("eventFlagManager");
        Assert.Equal(8, signature.GetProperty("operandOffset").GetInt32());
        Assert.Equal(12, signature.GetProperty("instructionEnd").GetInt32());
    }
}

public class AutoTrackerTests
{
    private static GameData Data() => new()
    {
        Regions = [new Region { Id = "r", Name = "R" }],
        Settings = [],
        Items =
        [
            new Item { Name = "Rusty Key", Category = "key" },
            new Item { Name = "Stonesword Key", Category = "key", MaxCount = 3 },
        ],
        Locations =
        [
            new Location { Id = "boss", Name = "Boss", Region = "r", Category = LocationCategory.MajorBoss, FlagId = 100 },
            new Location { Id = "chest", Name = "Chest", Region = "r", Category = LocationCategory.KeyItem, FlagId = 200 },
            new Location { Id = "shop", Name = "Shop", Region = "r", Category = LocationCategory.Merchant },
        ],
    };

    private static SeedFlags Seed(params ItemPlacement[] placements) => new()
    {
        Placements = placements,
        LocationFlags = new Dictionary<string, IReadOnlyList<uint>> { ["chest"] = [201] },
        LogMismatches = 0,
        UntrackableItems = [],
    };

    [Fact]
    public void Apply_ChecksLocationsWhoseFlagIsOn()
    {
        var state = new TrackerState();
        Assert.True(new AutoTracker(Data(), state, null).Apply(new HashSet<uint> { 100 }));
        Assert.Contains("boss", state.CheckedLocations);
        Assert.DoesNotContain("chest", state.CheckedLocations);
    }

    [Fact]
    public void Apply_PrefersTheSeedsFlagForALocation()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, Seed());

        Assert.False(tracker.Apply(new HashSet<uint> { 200 }));
        Assert.True(tracker.Apply(new HashSet<uint> { 201 }));
        Assert.Contains("chest", state.CheckedLocations);
    }

    [Fact]
    public void Apply_ReportsNoChangeWhenNothingIsNew()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, null);
        tracker.Apply(new HashSet<uint> { 100 });
        Assert.False(tracker.Apply(new HashSet<uint> { 100 }));
    }

    [Fact]
    public void Apply_DoesNotUndoAManualUntick()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, null);
        tracker.Apply(new HashSet<uint> { 100 });
        state.CheckedLocations.Remove("boss");

        tracker.Apply(new HashSet<uint> { 100 });
        Assert.DoesNotContain("boss", state.CheckedLocations);
    }

    [Fact]
    public void Apply_NeverClearsAnythingWhenFlagsAreOff()
    {
        var state = new TrackerState();
        state.CheckedLocations.Add("boss");
        state.Items["Rusty Key"] = 1;
        new AutoTracker(Data(), state, Seed()).Apply(new HashSet<uint>());

        Assert.Contains("boss", state.CheckedLocations);
        Assert.Equal(1, state.Items["Rusty Key"]);
    }

    [Fact]
    public void Apply_AddsItems_OncePerCopy()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, Seed(
            new ItemPlacement("Rusty Key", 1, 300, "Rusty Key"),
            new ItemPlacement("Rusty Key", 1, 301, "Rusty Key")));

        tracker.Apply(new HashSet<uint> { 300, 301 });
        Assert.Equal(1, state.Items["Rusty Key"]);
    }

    [Fact]
    public void Apply_AddsCountedItems_AndKeepsSpentKeysSpent()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, Seed(new ItemPlacement("Stonesword Key", 2, 400, "Stonesword Key|400")));

        tracker.Apply(new HashSet<uint> { 400 });
        Assert.Equal(2, state.Items["Stonesword Key"]);

        state.Items["Stonesword Key"] = 0;
        tracker.Apply(new HashSet<uint> { 400 });
        Assert.Equal(0, state.Items["Stonesword Key"]);
    }

    [Fact]
    public void Apply_CapsCountsAtTheItemsMaximum()
    {
        var state = new TrackerState();
        state.Items["Stonesword Key"] = 2;
        new AutoTracker(Data(), state, Seed(new ItemPlacement("Stonesword Key", 5, 400, "Stonesword Key|400")))
            .Apply(new HashSet<uint> { 400 });
        Assert.Equal(3, state.Items["Stonesword Key"]);
    }

    [Fact]
    public void PendingFlags_DropsWhatIsAlreadyCredited()
    {
        var state = new TrackerState();
        var tracker = new AutoTracker(Data(), state, Seed(new ItemPlacement("Rusty Key", 1, 300, "Rusty Key")));
        Assert.Equal([100u, 201u, 300u], tracker.PendingFlags().Order());

        tracker.Apply(new HashSet<uint> { 100, 300 });
        Assert.Equal([201u], tracker.PendingFlags());
    }
}

public sealed class SpoilerLogLocatorTests : IDisposable
{
    private readonly string _randomizer = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_randomizer))
            Directory.Delete(_randomizer, recursive: true);
    }

    [Fact]
    public void FindNewest_PicksTheLatestLog()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_randomizer, SpoilerLogLocator.LogsFolder)).FullName;
        var older = Path.Combine(logs, "older.txt");
        var newer = Path.Combine(logs, "newer.txt");
        File.WriteAllText(older, "");
        File.WriteAllText(newer, "");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));

        Assert.Equal(newer, SpoilerLogLocator.FindNewest(_randomizer));
    }

    [Fact]
    public void FindNewest_ReturnsNullWithoutLogs()
    {
        Assert.Null(SpoilerLogLocator.FindNewest(_randomizer));
    }

    [Fact]
    public void RandomizerDirectoryOf_OnlyRecognisesTheLogsFolder()
    {
        Assert.Equal(@"D:\Rando", SpoilerLogLocator.RandomizerDirectoryOf(@"D:\Rando\spoiler_logs\seed.txt"));
        Assert.Null(SpoilerLogLocator.RandomizerDirectoryOf(@"D:\Downloads\seed.txt"));
    }
}
