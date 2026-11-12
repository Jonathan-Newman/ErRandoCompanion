using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.SpoilerLog;
using ErRandoCompanion.GameReader;

namespace ErRandoCompanion.Tests;

public class KillTests
{
    [Fact]
    public void CountsAKillWhenAnEnemySeenAliveHasNoHpLeft()
    {
        var counter = new KillCounter();
        Assert.Empty(counter.Update([new EnemySighting(1, 43000, 3080, 100), new EnemySighting(2, 43000, 3080, 0)]));
        // The one seen alive dies; the corpse from before is not counted.
        Assert.Equal([new Kill(43000, 3080)], counter.Update([new EnemySighting(1, 43000, 3080, 0), new EnemySighting(2, 43000, 3080, 0)]));
        Assert.Empty(counter.Update([new EnemySighting(1, 43000, 3080, 0)]));
        // Back after resting, and killed again.
        counter.Update([new EnemySighting(1, 43000, 3080, 100)]);
        Assert.Single(counter.Update([new EnemySighting(1, 43000, 3080, 0)]));
        // Unloaded and loaded again dead: not a new kill.
        counter.Update([]);
        Assert.Empty(counter.Update([new EnemySighting(1, 43000, 3080, 0)]));
    }

    [Fact]
    public void RecordsKillsByKindWithTheRandomizersNames()
    {
        var state = new TrackerState();
        var names = new Dictionary<string, string> { ["c3080"] = "Imp" };
        Assert.True(KillCounter.Record(state, [new Kill(43000, 3080), new Kill(43000, 3080), new Kill(43001, 3080), new Kill(9, 4321)], names));
        Assert.Equal(2, state.Kills[43000]);
        Assert.Equal("Imp", state.KillKinds[43001]);
        Assert.Equal("c4321", state.KillKinds[9]);
        Assert.Equal([("Imp", 3), ("c4321", 1)], KillCounter.ByKind(state));
    }

    [Fact]
    public void WorksOutDropOddsAndTicksDropsAfterKills()
    {
        var seed = Seed();
        var imp = seed.Checks.Single(c => c.Id == "drop:imp:impheadcat");
        var state = new TrackerState();
        Assert.Equal(new DropOdds(0, null, null), seed.OddsOf(imp, state));

        // A 10% row and a 50% row that never has it: each kill gives the staff one time in ten.
        Assert.Equal(0.1, seed.ChancePerKill(100, imp.DropItems), 6);
        state.Kills[100] = 20;
        state.KillKinds[100] = "Imp";
        var odds = seed.OddsOf(imp, state)!;
        Assert.Equal(20, odds.Kills);
        Assert.Equal(0.1, odds.PerKill!.Value, 6);
        Assert.Equal(Math.Pow(0.9, 20), odds.StillMissing!.Value, 6);
        Assert.False(odds.IsOverdue);
        state.Kills[100] = 30;
        Assert.True(seed.OddsOf(imp, state)!.IsOverdue);
        Assert.Contains("overdue", seed.OddsOf(imp, state)!.Text());

        // A kill of another kind with the same item in its table does not count toward the Imp's drop.
        state.Kills[200] = 50;
        state.KillKinds[200] = "Albinauric";
        Assert.Equal(30, seed.OddsOf(imp, state)!.Kills);

        // The staff arrives after an Imp kill: the Imp drop is ticked. An item no recent kill could give is not.
        Assert.Equal(0, seed.ApplyDrops(state, ["Glintstone Staff"], []));
        Assert.Equal(0, seed.ApplyDrops(state, ["Uchigatana"], [100]));
        Assert.Equal(1, seed.ApplyDrops(state, ["Glintstone Staff +2"], [100]));
        Assert.Contains(imp.Id, state.AllChecked);
        Assert.Null(seed.OddsOf(imp, state)!.StillMissing);
    }

    [Fact]
    public void ReadsEnemyOffsetsWithOverrides()
    {
        Assert.Equal(0x1DED8, EnemyOffsets.From(null).ChrSets);
        var changed = EnemyOffsets.From(new Dictionary<string, string> { ["chrSets"] = "0x1E000", ["hp"] = "140" });
        Assert.Equal(0x1E000, changed.ChrSets);
        Assert.Equal(0x140, changed.Hp);
        Assert.Equal(0x60, changed.NpcParam);
    }

    private static AllChecksSeed Seed()
    {
        var regulation = new Regulation
        {
            Lots = new Dictionary<int, IReadOnlyList<LotEntry>>(),
            ShopRows = [],
            EnemyDropLots = new Dictionary<int, IReadOnlyList<LotEntry>>
            {
                [500] = [new LotEntry(33000000, 2, 1, 0, 0.1)],
                [501] = [new LotEntry(1000, 1, 1, 0, 0.5)],
            },
            NpcDropLots = new Dictionary<int, int> { [100] = 500, [200] = 500 },
        };
        var names = new Dictionary<(int, int), string> { [(2, 33000000)] = "Glintstone Staff", [(1, 1000)] = "Golden Rune [1]" };
        var logic = RandomizerLogic.Parse("Areas:\n- Name: start\n  Text: Limgrave\n");
        return AllChecksSeed.Build(logic, [], regulation, new HashSet<string>(),
            [new EnemyTypeDrop("Glintstone Staff", "Imp", "Imp Head (Cat)")], new Dictionary<string, int> { ["Glintstone Staff"] = 2 }, [], names, ["Imp", "Albinauric"]);
    }
}
