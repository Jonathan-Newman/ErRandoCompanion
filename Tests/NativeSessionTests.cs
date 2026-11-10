using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.SpoilerLog;
using ErRandoCompanion.Native;

namespace ErRandoCompanion.Core.Tests;

/// <summary>The mod's session, run against the shipped data and a throwaway Seeds folder. It never polls, so no game is read.</summary>
public sealed class NativeSessionTests : IDisposable
{
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");

    public void Dispose()
    {
        if (Directory.Exists(_seeds))
            Directory.Delete(_seeds, recursive: true);
    }

    private Session Open() => new(DataDirectory, _seeds, randomizerDirectory: null, readOwnProcess: false);

    [Fact]
    public void FreshRun_CountsLikeTheApp()
    {
        using var session = Open();
        var summary = session.OpenSeed("fresh");

        // Default settings, nothing found: the same counts the app shows for a new run.
        Assert.Equal((0, 245, 94), (summary.Checked, summary.Total, summary.Available));
        Assert.False(summary.Spoiler.Loaded);
        Assert.Equal(summary.Total, session.Checks("all").Count);
        Assert.Equal(summary.Available, session.Checks("available").Count);
        Assert.All(session.Checks("all"), c => Assert.Contains(c.Status, new[] { "available", "locked" }));
    }

    [Fact]
    public void Skips_RegionsAndReport_WorkThroughTheSession()
    {
        using var session = Open();
        var start = session.OpenSeed("routing");
        Assert.Equal(start.Available, start.Open);
        var id = session.Checks("available")[0].Id;

        var skipped = session.ToggleSkip(id);
        Assert.Equal(start.Open - 1, skipped.Open);
        Assert.Equal(1, skipped.Skipped);
        Assert.True(session.Checks("all").Single(c => c.Id == id).Skipped);
        Assert.Equal(1, session.Regions().Sum(r => r.Skipped));
        Assert.Equal(start.Open, session.ToggleSkip(id).Open);

        session.SetGoal("majorBosses", 2);
        session.SetChecked(id, true);
        var report = session.Report(null);
        Assert.Equal(1, report.Checked);
        Assert.Equal("Defeat 2 major bosses", report.Goal);
        Assert.False(report.Finished);
        // Skipping the nearest needs the player's position, which only a poll in the game gives.
        Assert.Throws<SessionException>(() => session.SkipNearest());

        var saved = new TrackerStore(_seeds).Load("routing")!;
        Assert.True(saved.TimelineStarted);
        Assert.True(saved.CheckedAt.ContainsKey(id));
    }

    [Fact]
    public void Changes_AreSavedInTheAppsFormat()
    {
        using (var session = Open())
        {
            session.OpenSeed("shared run");
            var id = session.Checks("available")[0].Id;
            session.SetChecked(id, true);
            session.SetTarget(session.Checks("available")[0].Id, 2);
            session.SetItem("Stonesword Key", 99);
        }

        // The app's own store reads what the mod wrote.
        var saved = new TrackerStore(_seeds).Load("shared run")!;
        Assert.Single(saved.CheckedLocations);
        Assert.Single(saved.Targets);
        Assert.True(saved.Items["Stonesword Key"] > 0);
    }

    [Fact]
    public void Targets_FollowTheTrackersRules()
    {
        using var session = Open();
        session.OpenSeed("targets");
        var first = session.Checks("available")[0].Id;
        var second = session.Checks("available")[1].Id;

        session.SetTarget(first, 1);
        var summary = session.SetTarget(second, 1);
        Assert.Equal(second, Assert.Single(summary.Targets).Id);

        summary = session.SetTarget(first, 4);
        Assert.Equal([1, 4], summary.Targets.Select(t => t.Number));
        Assert.Equal(second, summary.Targets.Single(t => t.Active).Id);

        summary = session.SetActiveTarget(4);
        Assert.Equal(first, summary.Targets.Single(t => t.Active).Id);
        Assert.Equal(4, session.Checks("all").Single(c => c.Id == first).Target);

        summary = session.SetChecked(first, true);
        Assert.Equal(second, Assert.Single(summary.Targets).Id);
    }

    [Fact]
    public void Settings_ChangeWhatCounts()
    {
        using var session = Open();
        var before = session.OpenSeed("settings").Total;
        var after = session.SetSetting("dlcEnabled", "false").Total;

        Assert.True(after < before);
        Assert.Equal("false", session.Settings().Single(s => s.Id == "dlcEnabled").Value);
    }

    [Fact]
    public void BadRequests_AreRefusedWithAReason()
    {
        using var session = Open();
        session.OpenSeed("bad");

        Assert.Throws<SessionException>(() => session.SetChecked("nowhere", true));
        Assert.Throws<SessionException>(() => session.SetTarget(session.Checks("all")[0].Id, 6));
        Assert.Throws<SessionException>(() => session.SetSetting("dlcEnabled", "maybe"));
        Assert.Throws<SessionException>(() => session.SetItem("Nothing", 1));
        Assert.Throws<SessionException>(() => session.Checks("sideways"));
        Assert.Throws<SessionException>(() => session.OpenSeed("  "));
        Assert.Throws<SessionException>(() => session.OpenNewestSeed());
    }

    [Fact]
    public void DeletingTheOpenRun_MovesToAnother()
    {
        using var session = Open();
        session.OpenSeed("keep");
        session.OpenSeed("drop");

        Assert.True(session.DeleteSeed("drop"));
        Assert.Equal("keep", session.Summary().SeedName);
        Assert.DoesNotContain("drop", session.SeedNames());
    }

    private static ParsedSpoilerLog Log(string seed = "1340422973", string version = "v0.11", string[]? options = null, bool sold = false) => new()
    {
        Seed = seed,
        Version = version,
        Options = (options ?? ["dlc", "seed:" + seed]).ToHashSet(),
        RandomizerDirectory = "",
        SoldItems = new Dictionary<string, bool> { ["Rusty Key"] = sold, ["Stonesword Key"] = true },
    };

    [Fact]
    public void SameRandomization_NeedsSeedOptionsVersionAndPlacementsToMatch()
    {
        Assert.True(Log().IsSameRandomization(Log()));
        Assert.False(Log().IsSameRandomization(Log(seed: "42")));
        Assert.False(Log().IsSameRandomization(Log(version: "v0.12")));
        Assert.False(Log().IsSameRandomization(Log(options: ["seed:1340422973"])));
        Assert.False(Log().IsSameRandomization(Log(sold: true)));
        Assert.False(Log(seed: "").IsSameRandomization(Log(seed: "")));
    }
}
