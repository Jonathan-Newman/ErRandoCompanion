using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Persistence;

namespace ErRandoCompanion.Core.Tests;

public sealed class TrackerStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SaveThenLoad_RestoresEverything()
    {
        var store = new TrackerStore(_directory);
        var state = new TrackerState { SeedName = "seed 1" };
        state.CheckedLocations.Add("some_location");
        state.Items["Great Runes"] = 3;
        state.Settings["leyndellRunes"] = "2";
        store.Save(state);

        var loaded = store.Load("seed 1")!;
        Assert.Equal("seed 1", loaded.SeedName);
        Assert.Contains("some_location", loaded.CheckedLocations);
        Assert.Equal(3, loaded.Items["Great Runes"]);
        Assert.Equal("2", loaded.Settings["leyndellRunes"]);
    }

    [Fact]
    public void Save_WritesTheSameFormatAsEarlierVersions()
    {
        // Saves were once written by reflection; the generated serializer must produce the very same text.
        var state = new TrackerState { SeedName = "Seed 1340422973", SpoilerLogPath = @"C:\rando\spoiler_logs\log.txt", ActiveTarget = 2 };
        state.CheckedLocations.Add("boss_margit_the_fell_omen_stormveil_castle");
        state.Items["Stonesword Key"] = 4;
        state.Settings["dlcEnabled"] = "true";
        state.AutoCredited.Add("location:boss_margit_the_fell_omen_stormveil_castle");
        state.SetTarget("key_rusty_key_stormveil_castle", 2);
        new TrackerStore(_directory).Save(state);

        var earlier = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(earlier, File.ReadAllText(Directory.GetFiles(_directory).Single()));
    }

    [Fact]
    public void Load_FillsInFieldsThatOlderSavesDoNotHave()
    {
        // Saves from before targets existed have no Targets or ActiveTarget, and the oldest lack AutoCredited too.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "old.seed.json"), """{ "SeedName": "old", "CheckedLocations": [ "a" ] }""");

        var loaded = new TrackerStore(_directory).Load("old")!;
        Assert.Equal(["a"], loaded.CheckedLocations);
        Assert.Empty(loaded.Items);
        Assert.Empty(loaded.Settings);
        Assert.Empty(loaded.AutoCredited);
        Assert.Empty(loaded.Targets);

        loaded.SetTarget("b", 1);
        Assert.Equal("b", loaded.ActiveTargetLocation());
    }

    [Fact]
    public void Load_ReturnsNullForUnknownSeed()
    {
        Assert.Null(new TrackerStore(_directory).Load("never saved"));
    }

    [Fact]
    public void Seeds_AreKeptApart_EvenWithAwkwardNames()
    {
        var store = new TrackerStore(_directory);
        store.Save(new TrackerState { SeedName = "a/b:c?" });
        store.Save(new TrackerState { SeedName = "a_b_c_" });

        Assert.Equal(["a/b:c?", "a_b_c_"], store.SeedNames().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Delete_RemovesOnlyThatSeed()
    {
        var store = new TrackerStore(_directory);
        store.Save(new TrackerState { SeedName = "old run" });
        store.Save(new TrackerState { SeedName = "current run" });

        Assert.True(store.Delete("old run"));
        Assert.Null(store.Load("old run"));
        Assert.Equal(["current run"], store.SeedNames());
        Assert.False(store.Delete("old run"));
    }

    [Fact]
    public void Load_RejectsDamagedFiles_AndLeavesThemInPlace()
    {
        var store = new TrackerStore(_directory);
        store.Save(new TrackerState { SeedName = "broken" });
        var path = Directory.GetFiles(_directory).Single();
        File.WriteAllText(path, "{ not json");

        Assert.Throws<TrackerStoreException>(() => store.Load("broken"));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        var store = new TrackerStore(_directory);
        store.Save(new TrackerState { SeedName = "one" });
        store.Save(new TrackerState { SeedName = "one" });
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ApplyMissingDefaults_FillsNewSettings_KeepsExistingOnes()
    {
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var state = new TrackerState();
        state.Settings["leyndellRunes"] = "5";
        state.ApplyMissingDefaults(data);

        Assert.Equal("5", state.Settings["leyndellRunes"]);
        Assert.Equal("normal", state.Settings["startMode"]);
        Assert.Equal("true", state.Settings["otherBosses"]);
    }

    [Fact]
    public void Save_BacksUpThePreviousSaveAtMostEveryTenMinutes()
    {
        var store = new TrackerStore(_directory);
        var state = new TrackerState { SeedName = "run" };
        store.Save(state);
        Assert.Empty(store.Backups("run"));  // nothing to back up before the first save

        state.CheckedLocations.Add("a");
        store.Save(state);
        state.CheckedLocations.Add("b");
        store.Save(state);
        var backups = store.Backups("run");
        Assert.Single(backups);              // the second save within ten minutes adds none
        Assert.Equal(0, backups[0].Checked); // it holds the state from before the first change
    }

    [Fact]
    public void Restore_PutsABackupBackAndKeepsWhatWasThere()
    {
        var store = new TrackerStore(_directory);
        var state = new TrackerState { SeedName = "run" };
        state.CheckedLocations.Add("a");
        store.Save(state);
        state.CheckedLocations.Add("b");
        store.Save(state);                   // backs up the one-check save

        var older = store.Backups("run").Single();
        var restored = store.Restore("run", older.Id);
        Assert.Equal(["a"], restored.CheckedLocations);
        Assert.Equal(["a"], store.Load("run")!.CheckedLocations);
        // The two-check save it replaced is now a backup, so the restore can be undone.
        Assert.Contains(store.Backups("run"), b => b.Checked == 2);
    }

    [Fact]
    public void Delete_KeepsABackupSoTheRunCanBeBroughtBack()
    {
        var store = new TrackerStore(_directory);
        var state = new TrackerState { SeedName = "gone" };
        state.CheckedLocations.Add("a");
        store.Save(state);
        Assert.True(store.Delete("gone"));

        Assert.DoesNotContain("gone", store.SeedNames());
        Assert.Contains("gone", store.RunsWithBackups());
        store.Restore("gone", store.Backups("gone")[0].Id);
        Assert.Contains("gone", store.SeedNames());
        Assert.Equal(["a"], store.Load("gone")!.CheckedLocations);
    }

    [Fact]
    public void Backups_KeepOnlyTheMostRecentTen()
    {
        var store = new TrackerStore(_directory);
        var state = new TrackerState { SeedName = "run" };
        store.Save(state);
        for (var i = 0; i < 15; i++)
        {
            store.Delete("run");             // each delete backs up, whatever the time since the last
            state.CheckedLocations.Add($"check {i}");
            store.Save(state);
        }
        Assert.Equal(TrackerStore.KeepBackups, store.Backups("run").Count);
    }
}
