using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Persistence;

namespace ErRandoCompanion.Core.Tests;

public sealed class TargetTests
{
    [Fact]
    public void SetTarget_TakesTheNumberFromItsPreviousHolder()
    {
        var state = new TrackerState();
        state.SetTarget("a", 1);
        state.SetTarget("b", 1);

        Assert.Equal(new Dictionary<string, int> { ["b"] = 1 }, state.Targets);
    }

    [Fact]
    public void SetTarget_RenumbersAndClears()
    {
        var state = new TrackerState();
        state.SetTarget("a", 1);
        state.SetTarget("a", 3);
        Assert.Equal(3, state.Targets["a"]);

        state.SetTarget("a", null);
        Assert.Empty(state.Targets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TrackerState.MaxTargets + 1)]
    public void SetTarget_RejectsNumbersOutOfRange(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackerState().SetTarget("a", number));
    }

    [Fact]
    public void ActiveTarget_FallsBackToTheLowestNumber()
    {
        var state = new TrackerState();
        Assert.Null(state.ActiveTargetLocation());

        state.SetTarget("four", 4);
        state.SetTarget("two", 2);
        Assert.Equal("two", state.ActiveTargetLocation());

        state.ActiveTarget = 4;
        Assert.Equal("four", state.ActiveTargetLocation());

        state.ActiveTarget = 5;
        Assert.Equal("two", state.ActiveTargetLocation());
    }

    [Fact]
    public void ClearCheckedTargets_DropsOnlyFoundTargets()
    {
        var state = new TrackerState();
        state.SetTarget("found", 1);
        state.SetTarget("hunting", 2);
        state.CheckedLocations.Add("found");

        Assert.True(state.ClearCheckedTargets());
        Assert.Equal(["hunting"], state.Targets.Keys);
        Assert.False(state.ClearCheckedTargets());
    }

    [Fact]
    public void Targets_SurviveSaving()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());
        try
        {
            var store = new TrackerStore(directory);
            var state = new TrackerState { SeedName = "s", ActiveTarget = 2 };
            state.SetTarget("a", 2);
            store.Save(state);

            var loaded = store.Load("s")!;
            Assert.Equal(2, loaded.Targets["a"]);
            Assert.Equal(2, loaded.ActiveTarget);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
