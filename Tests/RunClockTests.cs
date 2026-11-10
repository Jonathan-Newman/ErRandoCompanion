using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Tests;

public sealed class RunClockTests
{
    private static readonly DateTime Start = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CountsOnlyWhileInTheWorld()
    {
        var clock = new RunClock();
        var state = new TrackerState();
        clock.Tick(state, inWorld: true, Start);                    // the first tick only starts the clock
        clock.Tick(state, inWorld: true, Start.AddSeconds(1));
        clock.Tick(state, inWorld: false, Start.AddSeconds(2));      // title screen
        clock.Tick(state, inWorld: true, Start.AddSeconds(3));
        Assert.Equal(2, state.PlayedSeconds);
    }

    [Fact]
    public void ALongGapCountsOnlyAFewSeconds()
    {
        var clock = new RunClock();
        var state = new TrackerState();
        clock.Tick(state, true, Start);
        clock.Tick(state, true, Start.AddHours(3));                  // the PC slept
        Assert.Equal(RunClock.MaxStep.TotalSeconds, state.PlayedSeconds);
    }

    [Fact]
    public void StopsOnceTheGoalIsMet()
    {
        var clock = new RunClock();
        var state = new TrackerState { PlayedSeconds = 100, FinishedAt = Start };
        clock.Tick(state, true, Start);
        clock.Tick(state, true, Start.AddSeconds(1));
        Assert.Equal(100, state.PlayedSeconds);
    }

    [Fact]
    public void AsksToBeSavedAboutOnceAMinute()
    {
        var clock = new RunClock();
        var state = new TrackerState();
        var saves = 0;
        for (var second = 0; second <= 150; second++)
            saves += clock.Tick(state, true, Start.AddSeconds(second)) ? 1 : 0;
        Assert.Equal(3, saves);                                      // at the first counted second, then 60 and 120 later
    }

    [Fact]
    public void FormatsTimeAndPace()
    {
        Assert.Equal("2:05", RunClock.Format(125.9));
        Assert.Equal("1:02:03", RunClock.Format(3723));
        Assert.Null(RunClock.PerHour(10, 200));
        Assert.Equal(15, RunClock.PerHour(30, 7200));
    }
}
