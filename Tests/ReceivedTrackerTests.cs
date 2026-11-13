using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;
using Xunit.Abstractions;

namespace ErRandoCompanion.Tests;

/// <summary>Ticking from what arrives in the inventory, for a guest in Seamless Co-op whose pickups set no flags.</summary>
public class ReceivedTrackerTests(ITestOutputHelper output)
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    [Fact]
    public void TicksThePlaceOfAnItemTheSeedPutsInOnePlace()
    {
        var location = Data.Locations.First(l => l.LotIds.Count > 0);
        var other = Data.Locations.First(l => l.LotIds.Count > 0 && l.Id != location.Id && !l.LotIds.Intersect(location.LotIds).Any());
        var regulation = new Regulation
        {
            Lots = new Dictionary<int, IReadOnlyList<LotEntry>>
            {
                [location.LotIds[0]] = [new LotEntry(33000000, 2, 1, 1), new LotEntry(1000, 1, 1, 1)],
                [other.LotIds[0]] = [new LotEntry(1000, 1, 1, 2)],
            },
            ShopRows = [],
        };
        var names = new Dictionary<(int, int), string> { [(2, 33000000)] = "Glintstone Staff", [(1, 1000)] = "Golden Rune [1]" };
        var tracker = ReceivedTracker.Build(Data, Flags([]), regulation, names,
            new Dictionary<string, int> { ["Glintstone Staff"] = 1, ["Golden Rune [1]"] = 2 });

        var state = TrackerState.Create("test", Data);
        // A rune placed twice says nothing about where it came from.
        Assert.False(tracker.Apply(state, ["Golden Rune [1]"]));
        // The staff is only here; upgraded or not, it ticks this place, once.
        Assert.True(tracker.Apply(state, ["Glintstone Staff"]));
        Assert.Contains(location.Id, state.CheckedLocations);
        state.CheckedLocations.Remove(location.Id);
        Assert.False(tracker.Apply(state, ["Glintstone Staff"]));
    }

    [Fact]
    public void TicksTheNearestPlaceOfAnItemTheSeedPutsInSeveral()
    {
        var places = Data.Locations.Where(l => l.LotIds.Count > 0).Take(3).ToList();
        var regulation = new Regulation
        {
            Lots = places.ToDictionary(l => l.LotIds[0], _ => (IReadOnlyList<LotEntry>)[new LotEntry(1000, 1, 1, 1)]),
            ShopRows = [],
        };
        var tracker = ReceivedTracker.Build(Data, Flags([]), regulation, new Dictionary<(int, int), string> { [(1, 1000)] = "Golden Rune [1]" },
            new Dictionary<string, int> { ["Golden Rune [1]"] = 3 });
        var state = TrackerState.Create("test", Data);
        var distances = new Dictionary<string, double?> { [places[0].Id] = 75, [places[1].Id] = 12, [places[2].Id] = null };

        // Without knowing where the player is, a rune placed three times says nothing.
        Assert.False(tracker.Apply(state, ["Golden Rune [1]"]));
        // Beside the second place: that one. Then nothing within reach is left.
        Assert.True(tracker.Apply(state, ["Golden Rune [1]"], id => distances.GetValueOrDefault(id)));
        Assert.Equal([places[1].Id], state.CheckedLocations.Intersect(places.Select(p => p.Id)));
        Assert.False(tracker.Apply(state, ["Golden Rune [1]"], id => distances.GetValueOrDefault(id)));
    }

    [Fact]
    public void TicksAFullListPlaceWhenItIsTheOnlyOneOnTheMapWithTheItem()
    {
        const string slots = """
            Slots:
            - Key: 'a'
              DebugText:
              - Golden Rune [1] - lot 100[asset (Corpse) in m10_00_00_00 (Stormveil Castle)] Golden Rune [1]
              Text: On a corpse in the castle
              Area: start
            - Key: 'b'
              DebugText:
              - Golden Rune [1] - lot 200[asset (Corpse) in m60_42_36_00 (Limgrave)] Golden Rune [1]
              Text: On a corpse in Limgrave
              Area: start
            - Key: 'c'
              DebugText:
              - Golden Rune [1] - lot 300[asset (Corpse) in m60_42_36_00 (Limgrave)] Golden Rune [1]
              Text: On another corpse in Limgrave
              Area: start
            """;
        var logic = RandomizerLogic.Parse("Areas:\n- Name: start\n  Text: Limgrave\n");
        var regulation = new Regulation
        {
            Lots = new Dictionary<int, IReadOnlyList<LotEntry>>
            {
                [100] = [new LotEntry(1000, 1, 1, 1)],
                [200] = [new LotEntry(1000, 1, 1, 2)],
                [300] = [new LotEntry(2000, 1, 1, 3)],
            },
            ShopRows = [],
        };
        var names = new Dictionary<(int, int), string> { [(1, 1000)] = "Smithing Stone [1]", [(1, 2000)] = "Smithing Stone [2]" };
        var seed = AllChecksSeed.Build(logic, SlotCatalog.Parse(slots), regulation, new HashSet<string>(), [],
            new Dictionary<string, int> { ["Smithing Stone [1]"] = 2, ["Smithing Stone [2]"] = 1 }, [], names);
        Assert.Equal("m60_42_36_00", seed.Checks.Single(c => c.Id == "lot:200").Slot.Map);

        var state = new TrackerState();
        // In Limgrave only lot 200 has a Smithing Stone [1]; the castle's copy is on another map.
        Assert.Equal(1, seed.ApplyOnMap(state, ["Smithing Stone [1] x1"], "m60_42_36_00"));
        Assert.Contains("lot:200", state.AllChecked);
        Assert.DoesNotContain("lot:100", state.AllChecked);
        Assert.Equal(0, seed.ApplyOnMap(state, ["Smithing Stone [1]"], null));
    }

    [Fact]
    public void CountsKeyItemsOnceEvenIfTheirFlagTurnsOnLater()
    {
        var placement = new ItemPlacement("Rusty Key", 1, 400, "Rusty Key");
        var stones = new[] { new ItemPlacement("Stonesword Key", 2, 501, "Stonesword Key|501"), new ItemPlacement("Stonesword Key", 1, 502, "Stonesword Key|502") };
        var flags = Flags([placement, .. stones]);
        var tracker = ReceivedTracker.Build(Data, flags, new Regulation { Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(), ShopRows = [] },
            new Dictionary<(int, int), string>(), new Dictionary<string, int>());
        var state = TrackerState.Create("test", Data);

        Assert.True(tracker.Apply(state, ["Rusty Key"]));
        Assert.Equal(1, state.Items["Rusty Key"]);
        // Two Stonesword Keys from one place that gives two: one placement, not two.
        Assert.True(tracker.Apply(state, ["Stonesword Key x2"]));
        Assert.Equal(2, state.Items["Stonesword Key"]);

        // Back in your own world the flag turns on: already counted.
        var auto = new AutoTracker(Data, state, flags);
        Assert.False(auto.Apply(new HashSet<uint> { 400, 501 }));
        Assert.Equal(1, state.Items["Rusty Key"]);
        Assert.Equal(2, state.Items["Stonesword Key"]);
    }

    [Fact]
    public void ReportsHowMuchOfARealSeedCanBeTrackedThisWay()
    {
        var folder = Environment.GetEnvironmentVariable("ER_TEST_RANDOMIZER");
        if (folder is null)
            return;
        var logPath = Directory.GetFiles(Path.Combine(folder, "spoiler_logs"), "*.txt").OrderBy(File.GetLastWriteTimeUtc).Last();
        var seed = SeedLoader.Load(Data, logPath);
        Assert.NotNull(seed.Received);
        var all = seed.All!;
        var lots = all.Checks.Where(c => c.Enemy is null).ToList();
        var tracked = lots.Count(c => c.TrackItems.Count > 0);
        var withLots = Data.Locations.Count(l => l.LotIds.Count > 0 || l.ShopRows.Count > 0);
        output.WriteLine($"All checks that tick from an arriving item: {tracked} of {lots.Count}");
        var pins = MapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map_data.json")).Pins.Select(p => p.Id).ToHashSet();
        var nearby = seed.Received.PlacesNearby.Count(pins.Contains);
        output.WriteLine($"Checklist places that tick from an arriving item: {seed.Received.Places.Count} anywhere, {nearby} when beside them, of {withLots} with lots or shops");
        // Many slots hold items the seed places many times (runes, materials), which say nothing about where they came from.
        Assert.True(tracked > lots.Count / 5);
        Assert.True(nearby > withLots * 3 / 4);
    }

    private static SeedFlags Flags(IReadOnlyList<ItemPlacement> placements) => new()
    {
        Placements = placements,
        LocationFlags = new Dictionary<string, IReadOnlyList<uint>>(),
        LogMismatches = 0,
        UntrackableItems = [],
    };
}
