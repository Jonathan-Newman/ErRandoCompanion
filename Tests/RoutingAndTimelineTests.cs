using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Navigation;
using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

public sealed class RoutingAndTimelineTests
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
    private const string Margit = "boss_margit_the_fell_omen_stormveil_castle";
    private const string Godrick = "boss_godrick_the_grafted_stormveil_castle";
    private const string Rennala = "boss_rennala_academy_of_raya_lucaria";

    [Fact]
    public void RecordsWhenChecksAndKeyItemsHappen()
    {
        var state = TrackerState.Create("t", Data);
        var logic = new LogicEvaluator(Data, state);
        state.PlayedSeconds = 100;
        state.CheckedLocations.Add(Margit);
        state.Items["Great Runes"] = 1;
        Assert.True(RunTimeline.Update(Data, state, logic, null));
        Assert.Equal(100, state.CheckedAt[Margit]);
        Assert.Equal("Great Runes 1", state.KeyItemsFound.Single().Name);

        state.PlayedSeconds = 400;
        state.CheckedLocations.Add(Godrick);
        state.Items["Great Runes"] = 2;
        state.CheckedLocations.Remove(Margit);          // unticked by mistake
        RunTimeline.Update(Data, state, logic, null);
        Assert.False(state.CheckedAt.ContainsKey(Margit));
        Assert.Equal(400, state.CheckedAt[Godrick]);
        Assert.Equal(["Great Runes 1", "Great Runes 2"], state.KeyItemsFound.Select(k => k.Name));

        // Spending one does not take it off the list of what was found.
        state.Items["Great Runes"] = 1;
        Assert.False(RunTimeline.Update(Data, state, logic, null));
        Assert.Equal(2, state.KeyItemsFound.Count);
    }

    [Fact]
    public void AnOlderSaveGetsNoTimesForWhatItAlreadyHad()
    {
        var state = new TrackerState { SeedName = "old", PlayedSeconds = 5000 };   // as loaded from an older save
        state.CheckedLocations.Add(Margit);
        RunTimeline.Update(Data, state, new LogicEvaluator(Data, state), null);
        Assert.Equal(RunTimeline.Before, state.CheckedAt[Margit]);

        state.PlayedSeconds = 5100;
        state.CheckedLocations.Add(Godrick);
        RunTimeline.Update(Data, state, new LogicEvaluator(Data, state), null);
        Assert.Equal(5100, state.CheckedAt[Godrick]);
        // The stretch before the first timed tick is unknown, so there is no slowest stretch yet.
        Assert.Null(RunReports.Slowest(state, new Dictionary<string, string>()));
    }

    [Fact]
    public void SplitsFollowTheGoalAndCompareWithTheBestRun()
    {
        var arenas = BossArenas.Build(Data, [(FlagOf(Margit), FlagOf(Rennala))]);
        var state = TrackerState.Create("t", Data);
        state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses, Bosses = [Rennala, Godrick] };
        var logic = new LogicEvaluator(Data, state);
        state.PlayedSeconds = 300;
        state.CheckedLocations.Add(Margit);             // where Rennala was put
        RunTimeline.Update(Data, state, logic, arenas);
        state.PlayedSeconds = 900;
        state.CheckedLocations.Add(Godrick);
        RunTimeline.Update(Data, state, logic, arenas);

        Assert.Equal([("Rennala", 300.0), ("Godrick the Grafted", 900.0)],
            state.Splits.Select(s => (s.Name, s.Seconds)));

        var best = new List<TimedEvent> { new() { Name = "Godrick the Grafted", Seconds = 250 } };
        var compared = RunTimeline.Compare(state.Splits, best);
        Assert.Equal(250, compared[0].Best);                // step 1 against step 1, whichever boss it was
        Assert.Null(compared[1].Best);

        // The best run with the same goal, not this run itself.
        var other = TrackerState.Create("other", Data);
        other.Goal = new RunGoal { Kind = RunGoals.ChosenBosses, Bosses = [Rennala, Godrick] };
        other.FinishedAt = DateTime.UtcNow;
        other.PlayedSeconds = 2000;
        other.Splits = best;
        var history = RunHistory.Build([other]);
        Assert.Same(best, RunHistory.BestSplits(history, state.Goal, "t"));
        Assert.Null(RunHistory.BestSplits(history, state.Goal, "other"));
    }

    [Fact]
    public void CountsDeathsWhereTheyHappen()
    {
        var state = TrackerState.Create("t", Data);
        var deaths = new DeathCounter();
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(deaths.Update(state, 10, "Limgrave - Agheel Lake", true, now));          // a starting point
        Assert.True(deaths.Update(state, 11, "Limgrave - Agheel Lake", true, now.AddSeconds(1)));
        // The count goes up during the loading screen after dying: it still counts where the player was.
        Assert.True(deaths.Update(state, 12, null, false, now.AddSeconds(5)));
        Assert.Equal(2, state.Deaths["Limgrave"]);
        Assert.True(deaths.Update(state, 13, "Stormveil Castle", true, now.AddSeconds(30)));
        Assert.Equal(1, state.Deaths["Stormveil Castle"]);

        // A long stay on the title screen, then another character with more deaths: not counted.
        Assert.False(deaths.Update(state, 13, null, false, now.AddMinutes(5)));
        Assert.False(deaths.Update(state, 15, "Liurnia of the Lakes - Academy Gate Town", true, now.AddMinutes(6)));
        // Nor is a count that jumps by many at once.
        Assert.False(deaths.Update(state, 90, "Liurnia of the Lakes - Academy Gate Town", true, now.AddMinutes(7)));
        Assert.Equal(3, state.Deaths.Values.Sum());
    }

    [Fact]
    public void CountsEachRegionAndLeavesSkippedChecksOutOfWhatIsOpen()
    {
        var state = TrackerState.Create("t", Data);
        var logic = new LogicEvaluator(Data, state);
        var before = RunReports.Regions(Data, state, logic);
        var open = RunReports.OpenCount(Data, state, logic);
        Assert.All(before, r => Assert.Equal(r.Total, r.Open + r.Skipped + r.Locked + r.Done));

        var some = Data.Locations.First(l => logic.GetStatus(l) == LocationStatus.Available);
        state.Skipped.Add(some.Id);
        Assert.Equal(open - 1, RunReports.OpenCount(Data, state, logic));
        var region = Data.Regions.Single(r => r.Id == some.Region).Name;
        Assert.Equal(1, RunReports.Regions(Data, state, logic).Single(r => r.Region == region).Skipped);
    }

    [Fact]
    public void ReportsTheSlowestStretchAndDeaths()
    {
        var state = TrackerState.Create("t", Data);
        state.CheckedLocations.UnionWith([Margit, Godrick]);
        state.CheckedAt[Margit] = 600;
        state.CheckedAt[Godrick] = 3000;
        state.Deaths["Limgrave"] = 2;
        state.Deaths["Stormveil Castle"] = 7;
        state.PlayedSeconds = 3100;
        var report = RunReports.Build(Data, state, new LogicEvaluator(Data, state));
        Assert.Equal(2, report.BossesDefeated);
        Assert.Equal(9, report.DeathCount);
        Assert.Equal(("Stormveil Castle", 7), report.Deaths[0]);
        Assert.Equal(600, report.Slowest!.From);
        Assert.Equal(3000, report.Slowest.To);
        Assert.Equal("Godrick the Grafted", report.Slowest.EndedWith);
    }

    [Fact]
    public void FindsTheNearestFoundGraceToATarget()
    {
        var limgrave = new MapBlock(60, 42, 36, 0);
        var castle = new MapBlock(10, 0, 0, 0);
        Grace[] graces =
        [
            new("Church of Elleh", 1, limgrave, 0, 0, 60, 42 * 256 + 10, 36 * 256 + 10),
            new("Stormhill Shack", 2, limgrave, 0, 0, 60, 42 * 256 + 200, 36 * 256 + 200),
            new("Castleward Tunnel", 3, castle, 100, 100, 60, 42 * 256 + 900, 36 * 256 + 900),
            new("Shadow grace", 4, new MapBlock(61, 45, 40, 0), 0, 0, 61, 0, 0),
        ];
        var outside = new MapPin("x", "m", 0, 0, false) { Area = 60, World = new WorldPoint(42 * 256 + 190, null, 36 * 256 + 190) };
        Assert.Equal("Church of Elleh", Grace.Nearest(graces, new HashSet<uint> { 1, 3, 4 }, outside)!.Value.Grace.Name);
        Assert.Equal("Stormhill Shack", Grace.Nearest(graces, new HashSet<uint> { 1, 2 }, outside)!.Value.Grace.Name);

        // Inside the castle, a grace in the castle is measured in the castle.
        var inside = new MapPin("y", "m", 0, 0, false)
        {
            Area = 60, MapId = "m10_00_00_00", Local = new WorldPoint(110, null, 100),
            World = new WorldPoint(0, null, 0), Entrance = new WorldPoint(42 * 256 + 90, null, 36 * 256 + 90),
        };
        var near = Grace.Nearest(graces, new HashSet<uint> { 1, 3 }, inside)!.Value;
        Assert.Equal("Castleward Tunnel", near.Grace.Name);
        Assert.Equal(10, near.Distance, 3);
        Assert.Null(Grace.Nearest(graces, new HashSet<uint>(), outside));
    }

    /// <summary>Reads the graces from a real randomizer folder (set ER_TEST_RANDOMIZER), to check the param layout.</summary>
    [Fact]
    public void ReadsGracesFromARealRegulation()
    {
        var folder = Environment.GetEnvironmentVariable("ER_TEST_RANDOMIZER");
        if (folder is null)
            return;
        var regulation = Regulation.Load(Path.Combine(folder, SeedLoader.RegulationFile), Path.Combine(folder, SeedLoader.OodleFile));
        var graces = Grace.Build(regulation.GraceRows, Grace.LoadNames(folder), new MapLinks(regulation.MapLinkRows));
        foreach (var name in new[] { "The First Step", "Church of Elleh", "Castleward Tunnel", "Rampart Tower" })
        {
            var g = graces.FirstOrDefault(g => g.Name == name);
            Console.WriteLine(g is null ? $"{name}: missing" : $"{name}: flag {g.Flag} map {g.Map} area {g.Area} at {g.X:F0},{g.Z:F0}");
        }
        Console.WriteLine($"{regulation.GraceRows.Count} rows, {graces.Count} placed, flags {graces.Min(g => g.Flag)}-{graces.Max(g => g.Flag)}");
        Assert.True(graces.Count > 300);
        Assert.All(graces, g => Assert.InRange(g.Flag, 70000u, 79999u));
        var first = graces.Single(g => g.Name == "The First Step");
        var elleh = graces.Single(g => g.Name == "Church of Elleh");
        Assert.Equal(60, first.Area);
        Assert.InRange(Math.Sqrt(Math.Pow(first.X - elleh.X, 2) + Math.Pow(first.Z - elleh.Z, 2)), 20, 400);
    }

    private static int FlagOf(string id) => Data.Locations.Single(l => l.Id == id).FlagId;
}
