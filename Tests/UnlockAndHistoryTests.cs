using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Persistence;

namespace ErRandoCompanion.Core.Tests;

public sealed class UnlockAndHistoryTests : IDisposable
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ErRandoHistory", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void EachMissingItemSaysHowManyLockedChecksItWouldOpen()
    {
        var state = TrackerState.Create("t", Data);
        var unlocks = UnlockForecast.Compute(Data, state);
        Assert.NotEmpty(unlocks);
        Assert.True(unlocks.Zip(unlocks.Skip(1)).All(p => p.First.Opens >= p.Second.Opens), "biggest first");

        // Every count matches really getting the item.
        var now = new LogicEvaluator(Data, state);
        foreach (var unlock in unlocks)
        {
            var with = TrackerState.Create("t", Data);
            with.Items[unlock.Item] = 1;
            var logic = new LogicEvaluator(Data, with);
            var opened = Data.Locations.Count(l => now.GetStatus(l) == LocationStatus.Locked && logic.GetStatus(l) == LocationStatus.Available);
            Assert.Equal(opened, unlock.Opens);
        }
    }

    [Fact]
    public void AnItemAlreadyHeldInFullIsNotListed()
    {
        var state = TrackerState.Create("t", Data);
        var first = UnlockForecast.Compute(Data, state)[0];
        state.Items[first.Item] = Data.Items.Single(i => i.Name == first.Item).MaxCount;
        Assert.DoesNotContain(UnlockForecast.Compute(Data, state), u => u.Item == first.Item);
    }

    [Fact]
    public void HistoryListsFinishedRunsNewestFirstAndMarksTheBestTimePerGoal()
    {
        var store = new TrackerStore(_directory);
        var day = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        TrackerState Run(string name, double seconds, int daysLater, RunGoal? goal, bool finished = true) => new()
        {
            SeedName = name,
            PlayedSeconds = seconds,
            FinishedAt = finished ? day.AddDays(daysLater) : null,
            Goal = goal,
        };
        var beast = new RunGoal { Kind = RunGoals.EldenBeast };
        store.Save(Run("slow", 9000, 1, beast));
        store.Save(Run("fast", 6000, 2, beast));
        store.Save(Run("bosses", 7000, 3, new RunGoal { Kind = RunGoals.MajorBosses, Count = 5 }));
        store.Save(Run("going", 100, 4, beast, finished: false));

        var history = RunHistory.Read(store);
        Assert.Equal(["bosses", "fast", "slow"], history.Select(r => r.SeedName));
        Assert.Equal([true, true, false], history.Select(r => r.Best));
        Assert.Equal("Defeat 5 major bosses", history[0].Goal);
    }
}
