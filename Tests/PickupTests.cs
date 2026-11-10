using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;

namespace ErRandoCompanion.Core.Tests;

public sealed class PickupTests
{
    private static readonly Regulation Seed = new()
    {
        Lots = new Dictionary<int, IReadOnlyList<LotEntry>>
        {
            [100] = [new LotEntry(9000000, 2, 1, 700)],                                  // a weapon, upgraded (+0 name used)
            [101] = [new LotEntry(4000, 1, 1, 701), new LotEntry(2900, 1, 3, 701)],    // a spell and three runes, one pickup
            [102] = [new LotEntry(1000, 1, 1, 0)],                                       // no flag: a random drop, not trackable
        },
        ShopRows = [],
    };

    private static readonly Dictionary<(int, int), string> Names = new()
    {
        [(2, 9000000)] = "Uchigatana",
        [(1, 4000)] = "Glintstone Pebble",
        [(1, 2900)] = "Golden Rune [2]",
    };

    private static IReadOnlyDictionary<uint, Pickup> Pickups() =>
        ItemPickups.Build(Seed, Names, new Dictionary<int, string> { [100] = "In a chest by the gate" }, new Dictionary<int, string> { [100] = "Limgrave" });

    [Fact]
    public void BuildsPickupsWithNamesKindsAndPlaces()
    {
        var pickups = Pickups();
        Assert.Equal([700u, 701u], pickups.Keys.Order());
        Assert.Equal("In a chest by the gate", pickups[700].Where);
        Assert.Equal(("Uchigatana", ItemPickups.Weapons), (pickups[700].Items[0].Name, pickups[700].Items[0].Kind));
        Assert.Equal([ItemPickups.Spells, ItemPickups.Items], pickups[701].Items.Select(i => i.Kind));
        Assert.Equal(3, pickups[701].Items[1].Count);
    }

    [Fact]
    public void ReadsPlacesFromTheRandomizersSlotList()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            Slots:
            - Key: 'x'
              DebugText:
              - Uchigatana - lot 100[asset AEG099 (Corpse - id 1) in m60_42_36_00 (Limgrave)] Uchigatana
              Text: In a chest by the gate
            """.Replace("\r", ""));
        var places = ItemPickups.LoadPlaces(path, out var maps);
        File.Delete(path);
        Assert.Equal("In a chest by the gate", places[100]);
        Assert.Equal("Limgrave", maps[100]);
    }

    [Fact]
    public void TheFirstScanIsBeforeTrackingAndLaterFindsGetTheirTime()
    {
        var pickups = Pickups();
        var state = new TrackerState { PlayedSeconds = 600 };
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1, PickupLog.Record(state, pickups, new HashSet<uint> { 700 }, now, "Limgrave"));
        Assert.Null(state.Found[0].At);                       // taken before tracking
        Assert.Equal([701u], PickupLog.Pending(state, pickups));

        Assert.Equal(1, PickupLog.Record(state, pickups, new HashSet<uint> { 700, 701 }, now, "Liurnia"));
        var spell = state.Found[1];
        Assert.Equal(now, spell.At);
        Assert.Equal(600, spell.PlayedSeconds);
        Assert.Equal(["Glintstone Pebble", "Golden Rune [2] x3"], spell.Items);
        Assert.Equal("Picked up in Liurnia", spell.Where);    // no slot text: the map the player was on
        Assert.Empty(PickupLog.Pending(state, pickups));
        Assert.Equal(0, PickupLog.Record(state, pickups, new HashSet<uint> { 700, 701 }, now, null)); // each once
    }
}
