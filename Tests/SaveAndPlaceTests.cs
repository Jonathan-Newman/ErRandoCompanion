using System.Runtime.CompilerServices;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

internal static class NoRealSaves
{
    /// <summary>Every test looks for game saves in an empty folder, so the real save is never read or touched.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable(GameSaves.FolderOverride, Path.Combine(Path.GetTempPath(), "ErRandoTests", "no-saves"));
}

public sealed class SaveAndPlaceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ErRandoTests", Guid.NewGuid().ToString());

    public SaveAndPlaceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ThePlaceholderPlaceIsReplacedByTheNearestGrace()
    {
        var path = Path.Combine(_folder, "itemslots.txt");
        File.WriteAllText(path, string.Join("\n",
            "Slots:",
            "- Key: '1'",
            "  DebugText:",
            "  - Cracked Pot - lot 10000770[asset AEG099 (Shiny Item) in m10_00_00_00 (Stormveil Castle)] Cracked Pot",
            "  - By Liftside Chamber (Stormveil Castle) - 19.86 away, 0.69 o'clock, 16.74 height offset",
            "  Text: aaaaaaaaaaaaaaaaa",
            "- Key: '2'",
            "  DebugText:",
            "  - Rune - lot 10000780[asset AEG099 (Shiny Item) in m10_00_00_00 (Stormveil Castle)] Rune",
            "  Text: aaaaaaaaaaaaaaaaa",
            "- Key: '3'",
            "  DebugText:",
            "  - Sword - lot 10000790[asset AEG099 (Corpse) in m10_00_00_00 (Stormveil Castle)] Sword",
            "  Text: on a corpse by the gate",
            ""));
        var places = ItemPickups.LoadPlaces(path, out var maps);
        Assert.Equal("Near Liftside Chamber, 20 m away, 17 m up", places[10000770]);
        Assert.False(places.ContainsKey(10000780));          // no description at all: the map is used instead
        Assert.Equal("Stormveil Castle", maps[10000780]);
        Assert.Equal("on a corpse by the gate", places[10000790]);
    }

    [Fact]
    public void SavedPlaceholderEntriesAreMended()
    {
        var state = new TrackerState();
        state.Found.Add(new FoundItem { Flag = 7, Where = "aaaaaaaaaaaaaaaaa", Map = "Stormveil Castle" });
        state.Found.Add(new FoundItem { Flag = 8, Where = "aaaaaaaaaaaaaaaaa", Map = "Volcano Manor" });
        state.Found.Add(new FoundItem { Flag = 9, Where = "on a corpse", Map = "Limgrave" });
        var pickups = new Dictionary<uint, Pickup>
        {
            [7] = new(7, [], "Near Liftside Chamber, 20 m away", "Stormveil Castle"),
            [8] = new(8, [], ItemPickups.Nowhere, "Volcano Manor"),
        };
        Assert.Equal(2, PickupLog.Repair(state, pickups));
        Assert.Equal(["Near Liftside Chamber, 20 m away", "", "on a corpse"], state.Found.Select(f => f.Where));
        Assert.Equal(0, PickupLog.Repair(state, pickups));
    }

    [Fact]
    public void TheGameSaveIsCopiedWithEachBackupAndCanBePutBack()
    {
        var saves = Path.Combine(_folder, "EldenRing", "7656");
        Directory.CreateDirectory(saves);
        var save = Path.Combine(saves, "ER0000.RD2");
        File.WriteAllBytes(save, Enumerable.Repeat((byte)1, 5000).ToArray());
        File.WriteAllBytes(save + ".bak", [9]);                    // the game's own spare is not the save
        File.WriteAllBytes(Path.Combine(saves, "ER0000.sl2"), [2]);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "ER0000.sl2"), DateTime.UtcNow.AddDays(-3));

        var store = new TrackerStore(Path.Combine(_folder, "Seeds"), () => save);
        store.Save(new TrackerState { SeedName = "run" });
        store.Save(new TrackerState { SeedName = "run" });          // a save over an existing one makes a backup
        var backup = Assert.Single(store.Backups("run"));
        Assert.True(backup.HasGameSave);

        File.WriteAllBytes(save, [3, 3, 3]);                         // played on since
        var copy = store.GameSaveOf("run", backup.Id)!.Value;
        Assert.Equal(save, copy.Info.Path);
        GameSaves.Restore(copy.Copy, copy.Info);
        Assert.Equal(5000, new FileInfo(save).Length);
        Assert.Equal([3, 3, 3], File.ReadAllBytes(save + ".before-restore"));
    }

    [Fact]
    public void FindsTheSaveTheGameWroteLast()
    {
        var saves = Path.Combine(_folder, "EldenRing", "7656");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "ER0000.sl2"), [1]);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "ER0000.sl2"), DateTime.UtcNow.AddHours(-2));
        File.WriteAllBytes(Path.Combine(saves, "ER0000.RD2"), [1]);
        File.WriteAllBytes(Path.Combine(saves, "ER0000.RD2.bak"), [1]);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "ER0000.RD2.bak"), DateTime.UtcNow.AddHours(1));
        Assert.Equal(Path.Combine(saves, "ER0000.RD2"), GameSaves.FindActive(Path.Combine(_folder, "EldenRing")));
    }
}
