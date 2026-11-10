using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

public sealed class BossGoalTests
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
    private const string Margit = "boss_margit_the_fell_omen_stormveil_castle";
    private const string Rennala = "boss_rennala_academy_of_raya_lucaria";

    private static int FlagOf(string id) => Data.Locations.Single(l => l.Id == id).FlagId;

    [Fact]
    public void ReadsBossPlacementsFromTheLog()
    {
        string[] lines =
        [
            "Options and seed: dlc seed:1",
            "-- Boss placements",
            $"Replacing Margit (#{FlagOf(Margit)}) in Stormveil Castle: Rennala, Queen of the Full Moon (#{FlagOf(Rennala)}) from Raya Lucaria",
            "-- Miniboss placements",
            "Replacing Something (#123) in Somewhere: Other (#456) from Elsewhere",
            "-- Spoilers:",
            "-- End of item spoilers",
        ];
        var log = SpoilerLogParser.Parse(lines, @"C:\rando\spoiler_logs\log.txt");
        Assert.Equal([(FlagOf(Margit), FlagOf(Rennala)), (123, 456)], log.BossPlacements);
    }

    [Fact]
    public void ABossIsDefeatedWhereTheSeedPutIt()
    {
        // Rennala now stands in Margit's arena; Margit's own arena was not touched.
        var arenas = BossArenas.Build(Data, [(FlagOf(Margit), FlagOf(Rennala))]);
        Assert.Equal(Margit, arenas[Rennala]);

        var state = TrackerState.Create("t", Data);
        state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses, Bosses = [Rennala] };
        var logic = new LogicEvaluator(Data, state);

        // Clearing Rennala's own arena is someone else.
        state.CheckedLocations.Add(Rennala);
        var progress = RunGoals.Progress(Data, state, logic, arenas)!;
        Assert.False(progress.Met);
        Assert.False(progress.Bosses.Single().Defeated);

        // Clearing the arena she was put in is defeating her.
        state.CheckedLocations.Add(Margit);
        progress = RunGoals.Progress(Data, state, logic, arenas)!;
        Assert.True(progress.Met);
        Assert.Equal("Defeat 1 chosen boss", progress.Description);
    }

    [Fact]
    public void WithoutPlacementsEachBossIsInItsOwnArena()
    {
        var state = TrackerState.Create("t", Data);
        state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses, Bosses = [Rennala, Margit] };
        state.CheckedLocations.Add(Rennala);
        var progress = RunGoals.Progress(Data, state, new LogicEvaluator(Data, state))!;
        Assert.Equal((1, 2, false), (progress.Done, progress.Needed, progress.Met));
    }

    [Fact]
    public void NoBossesChosenIsNeverMet()
    {
        var state = TrackerState.Create("t", Data);
        state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses };
        var progress = RunGoals.Progress(Data, state, new LogicEvaluator(Data, state))!;
        Assert.False(progress.Met);
        Assert.Equal("Choose the bosses to defeat", progress.Description);
    }

    [Fact]
    public void ASecondPhaseIdCountsAsItsBoss()
    {
        // Rennala's first phase (#14000801) moved into Margit's arena.
        var arenas = BossArenas.Build(Data, [(FlagOf(Margit), 14000801)]);
        Assert.Equal(Margit, arenas[Rennala]);
    }
}
