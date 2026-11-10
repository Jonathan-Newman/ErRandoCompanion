using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Tests;

public sealed class RunGoalTests
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
    private static readonly DateTime Now = new(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);

    private static (TrackerState State, LogicEvaluator Logic) Run(RunGoal? goal)
    {
        var state = TrackerState.Create("goal test", Data);
        state.Goal = goal;
        return (state, new LogicEvaluator(Data, state));
    }

    [Fact]
    public void NoGoalHasNoProgressAndNeverFinishes()
    {
        var (state, logic) = Run(null);
        var progress = RunGoals.Progress(Data, state, logic);
        Assert.Null(progress);
        Assert.False(RunGoals.Update(state, progress, Now));
        Assert.Null(state.FinishedAt);
    }

    [Fact]
    public void DefeatingTheEldenBeastFinishesTheRunAndStopsItsClock()
    {
        var (state, logic) = Run(new RunGoal { Kind = RunGoals.EldenBeast });
        Assert.False(RunGoals.Progress(Data, state, logic)!.Met);

        state.CheckedLocations.Add(RunGoals.EldenBeastLocation);
        logic.Invalidate();
        Assert.True(RunGoals.Update(state, RunGoals.Progress(Data, state, logic), Now));
        Assert.Equal(Now, state.FinishedAt);
        Assert.False(RunGoals.Update(state, RunGoals.Progress(Data, state, logic), Now.AddMinutes(1)), "finished only once");

        // Unticked by mistake: the run carries on.
        state.CheckedLocations.Remove(RunGoals.EldenBeastLocation);
        logic.Invalidate();
        RunGoals.Update(state, RunGoals.Progress(Data, state, logic), Now);
        Assert.Null(state.FinishedAt);
    }

    [Fact]
    public void MajorBossesCountOnlyMajorBossesAndCapAtTheGoal()
    {
        var (state, logic) = Run(new RunGoal { Kind = RunGoals.MajorBosses, Count = 2 });
        var majors = Data.Locations.Where(l => l.Category == LocationCategory.MajorBoss && logic.GetStatus(l) != LocationStatus.Excluded).ToList();
        var other = Data.Locations.First(l => l.Category == LocationCategory.OtherBoss);

        state.CheckedLocations.Add(other.Id);
        state.CheckedLocations.Add(majors[0].Id);
        logic.Invalidate();
        var progress = RunGoals.Progress(Data, state, logic)!;
        Assert.Equal((1, 2, false), (progress.Done, progress.Needed, progress.Met));
        Assert.Equal("Defeat 2 major bosses", progress.Description);

        state.CheckedLocations.UnionWith(majors.Skip(1).Take(3).Select(l => l.Id));
        logic.Invalidate();
        progress = RunGoals.Progress(Data, state, logic)!;
        Assert.Equal((2, 2, true), (progress.Done, progress.Needed, progress.Met));
    }

    [Fact]
    public void AllMajorBossesMeansEveryOneTheSettingsInclude()
    {
        var (state, logic) = Run(new RunGoal { Kind = RunGoals.MajorBosses });
        var included = Data.Locations.Count(l => l.Category == LocationCategory.MajorBoss && logic.GetStatus(l) != LocationStatus.Excluded);
        var progress = RunGoals.Progress(Data, state, logic)!;
        Assert.Equal(included, progress.Needed);
        Assert.Equal("Defeat all the major bosses", progress.Description);
    }

    [Fact]
    public void KeyItemsCountEachKindOnce()
    {
        var (state, logic) = Run(new RunGoal { Kind = RunGoals.KeyItems, Count = 2 });
        var keys = Data.Items.Where(i => i.Category == "key" && logic.IsItemRelevant(i)).ToList();
        state.Items[keys[0].Name] = 3;   // three of one kind is still one key item
        var progress = RunGoals.Progress(Data, state, logic)!;
        Assert.Equal((1, false), (progress.Done, progress.Met));

        state.Items[keys[1].Name] = 1;
        progress = RunGoals.Progress(Data, state, logic)!;
        Assert.True(progress.Met);
    }
}
