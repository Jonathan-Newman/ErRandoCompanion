using System.Text.Json;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Tests;

public class LogicEvaluatorTests
{
    private static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private const string Margit = "boss_margit_the_fell_omen_stormveil_castle";
    private const string Godrick = "boss_godrick_the_grafted_stormveil_castle";
    private const string Makar = "boss_magma_wyrm_makar_ruin_strewn_precipice";
    private const string Lansseax = "other_ancient_dragon_lansseax_altus_plateau";
    private const string DraconicSentinel = "extra_draconic_tree_sentinel_capital_outskirts";
    private const string MohgOmen = "boss_mohg_the_omen_subterranean_shunning_grounds";
    private const string Radahn = "boss_starscourge_radahn_caelid";
    private const string RedmaneFight = "other_crucible_knight_and_misbegotten_warrior_caelid";
    private const string Morgott = "boss_morgott_the_omen_king_leyndell";
    private const string Niall = "boss_commander_niall_mountaintops_of_the_giants";
    private const string Belurat = "boss_divine_beast_dancing_lion_belurat";
    private const string Agheel = "other_flying_dragon_agheel_limgrave";
    private const string Coastal = "other_demi_human_chiefs_limgrave";

    private static TrackerState NewState() => TrackerState.Create("test", Data);

    private static LocationStatus Status(TrackerState state, string id) =>
        new LogicEvaluator(Data, state).GetStatus(Data.Locations.Single(l => l.Id == id));

    [Fact]
    public void FreshSeed_OpensBaseGameOnly()
    {
        var state = NewState();
        Assert.Equal(LocationStatus.Available, Status(state, Margit));
        Assert.Equal(LocationStatus.Locked, Status(state, Godrick));
        Assert.Equal(LocationStatus.Locked, Status(state, Lansseax));
        Assert.Equal(LocationStatus.Locked, Status(state, Belurat));
    }

    [Fact]
    public void BeatingMargit_OpensGodrick()
    {
        var state = NewState();
        state.CheckedLocations.Add(Margit);
        Assert.Equal(LocationStatus.Checked, Status(state, Margit));
        Assert.Equal(LocationStatus.Available, Status(state, Godrick));
    }

    [Fact]
    public void Altus_NeedsBothMedallionHalves_OrMakar()
    {
        var state = NewState();
        state.Items["Dectus Medallion (Left)"] = 1;
        Assert.Equal(LocationStatus.Locked, Status(state, Lansseax));

        state.Items["Dectus Medallion (Right)"] = 1;
        Assert.Equal(LocationStatus.Available, Status(state, Lansseax));

        var viaMakar = NewState();
        viaMakar.CheckedLocations.Add(Makar);
        Assert.Equal(LocationStatus.Available, Status(viaMakar, Lansseax));
    }

    [Theory]
    [InlineData("2", 1, LocationStatus.Locked)]
    [InlineData("2", 2, LocationStatus.Available)]
    [InlineData("0", 0, LocationStatus.Available)]
    [InlineData("3", 2, LocationStatus.Locked)]
    public void Leyndell_UsesTheRuneSetting(string required, int owned, LocationStatus expected)
    {
        var state = NewState();
        state.CheckedLocations.Add(DraconicSentinel);
        state.Settings["leyndellRunes"] = required;
        state.Items["Great Runes"] = owned;
        Assert.Equal(expected, Status(state, MohgOmen));
    }

    [Fact]
    public void RedmaneFight_LocksOnceFestivalStarts_UntilRadahnIsDead()
    {
        var state = NewState();
        Assert.Equal(LocationStatus.Available, Status(state, RedmaneFight));

        state.Items["Great Runes"] = 1;
        Assert.Equal(LocationStatus.Locked, Status(state, RedmaneFight));

        state.CheckedLocations.Add(Radahn);
        Assert.Equal(LocationStatus.Available, Status(state, RedmaneFight));
    }

    [Fact]
    public void Mountaintops_NeedsRoldByDefault_OrRunesWhenConfigured()
    {
        var state = NewState();
        state.Items["Great Runes"] = 7;
        Assert.Equal(LocationStatus.Locked, Status(state, Niall));

        state.CheckedLocations.Add(Morgott);
        state.Items["Rold Medallion"] = 1;
        Assert.Equal(LocationStatus.Available, Status(state, Niall));

        var byRunes = NewState();
        byRunes.Settings["mountaintopsRunes"] = "3";
        byRunes.Items["Great Runes"] = 2;
        Assert.Equal(LocationStatus.Locked, Status(byRunes, Niall));
        byRunes.Items["Great Runes"] = 3;
        Assert.Equal(LocationStatus.Available, Status(byRunes, Niall));
    }

    [Fact]
    public void StartMode_ControlsBaseGameAndDlc()
    {
        var dlcOnly = NewState();
        dlcOnly.Settings["startMode"] = "dlcOnly";
        Assert.Equal(LocationStatus.Excluded, Status(dlcOnly, Margit));
        Assert.Equal(LocationStatus.Available, Status(dlcOnly, Belurat));

        var both = NewState();
        both.Settings["startMode"] = "dlcWithBaseGame";
        Assert.Equal(LocationStatus.Available, Status(both, Margit));
        Assert.Equal(LocationStatus.Available, Status(both, Belurat));
    }

    [Fact]
    public void Exclusions_BeatCheckedAndFollowSettings()
    {
        var state = NewState();
        state.CheckedLocations.Add(Agheel);
        state.Settings["otherBosses"] = "false";
        Assert.Equal(LocationStatus.Excluded, Status(state, Agheel));

        var noMiniDungeons = NewState();
        noMiniDungeons.Settings["excludeMiniDungeons"] = "true";
        Assert.Equal(LocationStatus.Excluded, Status(noMiniDungeons, Coastal));
        Assert.Equal(LocationStatus.Available, Status(noMiniDungeons, Agheel));

        var noDlc = NewState();
        noDlc.Settings["dlcEnabled"] = "false";
        Assert.Equal(LocationStatus.Excluded, Status(noDlc, Belurat));
    }

    [Theory]
    [InlineData("other_ancient_hero_of_zamor_weeping_peninsula", 1)]
    [InlineData("other_ulcerated_tree_spirit_limgrave", 2)]
    [InlineData("other_frenzied_duelist_caelid", 2)]
    public void StoneswordChecks_NeedEnoughKeysHeld(string id, int keys)
    {
        var state = NewState();
        state.Items["Stonesword Key"] = keys - 1;
        Assert.Equal(LocationStatus.Locked, Status(state, id));

        state.Items["Stonesword Key"] = keys;
        Assert.Equal(LocationStatus.Available, Status(state, id));
    }

    [Theory]
    [InlineData("other_perfumer_tricia_and_misbegotten_warrior_altus_plateau", 2)]
    [InlineData("other_misbegotten_crusader_consecrated_snowfield", 2)]
    [InlineData("pickup_golden_seed_farum_azula", 1)]
    public void StoneswordChecks_StillNeedTheirOriginalRoute(string id, int keys)
    {
        var state = NewState();
        state.Settings["seedsAndTears"] = "true";
        state.Items["Stonesword Key"] = 99;
        Assert.Equal(LocationStatus.Locked, Status(state, id));

        // Open every route, leaving only the key count deciding.
        foreach (var item in Data.Items)
            state.Items[item.Name] = item.MaxCount;
        foreach (var location in Data.Locations.Where(l => l.Id != id))
            state.CheckedLocations.Add(location.Id);
        Assert.Equal(LocationStatus.Available, Status(state, id));

        state.Items["Stonesword Key"] = keys - 1;
        Assert.Equal(LocationStatus.Locked, Status(state, id));
    }

    [Fact]
    public void OnlyTheSixSealedChecks_NeedStoneswordKeys()
    {
        var sealedChecks = Data.Locations
            .Where(l => l.Requires.Any(option => option.Any(t => t.StartsWith("Stonesword Key"))))
            .Select(l => l.Id);

        Assert.Equal(
            new[]
            {
                "other_ancient_hero_of_zamor_weeping_peninsula",
                "other_frenzied_duelist_caelid",
                "other_misbegotten_crusader_consecrated_snowfield",
                "other_perfumer_tricia_and_misbegotten_warrior_altus_plateau",
                "other_ulcerated_tree_spirit_limgrave",
                "pickup_golden_seed_farum_azula",
            },
            sealedChecks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegionCache_RefreshesOnlyAfterInvalidate()
    {
        var state = NewState();
        var logic = new LogicEvaluator(Data, state);
        Assert.False(logic.IsRegionReachable("altus_access"));

        state.CheckedLocations.Add(Makar);
        Assert.False(logic.IsRegionReachable("altus_access"));

        logic.Invalidate();
        Assert.True(logic.IsRegionReachable("altus_access"));
    }

    [Fact]
    public void RouteCycle_EndsInsteadOfOverflowing()
    {
        var data = new GameData
        {
            Locations = [],
            Items = [],
            Settings = [],
            Regions =
            [
                new Region { Id = "a", Name = "A", Requires = [["route:b"]] },
                new Region { Id = "b", Name = "B", Requires = [["route:a"]] },
            ],
        };
        Assert.False(new LogicEvaluator(data, new TrackerState()).IsRegionReachable("a"));
    }

    [Fact]
    public void Item_RelevanceFollowsSettings()
    {
        var gaolKey = Data.Items.Single(i => i.Name == "Gaol Upper Level Key");
        var state = NewState();
        Assert.True(new LogicEvaluator(Data, state).IsItemRelevant(gaolKey));

        state.Settings["randomizeGaolKeys"] = "false";
        Assert.False(new LogicEvaluator(Data, state).IsItemRelevant(gaolKey));
    }

    /// <summary>
    /// Replays states exported from the checklist workbook by tools/convert_checklist.py.
    /// The expected statuses come from the workbook's own rules, not from the JSON logic.
    /// </summary>
    [Fact]
    public void MatchesEveryStatusFromTheChecklistWorkbook()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "sheet_vectors.json");
        using var vectors = JsonDocument.Parse(File.ReadAllText(path));
        var ids = vectors.RootElement.GetProperty("locations").EnumerateArray().Select(e => e.GetString()!).ToList();
        var locations = Data.Locations.ToDictionary(l => l.Id);
        Assert.Equal(Data.Locations.Count, ids.Count);

        var compared = 0;
        var seen = new HashSet<LocationStatus>();
        foreach (var vector in vectors.RootElement.GetProperty("states").EnumerateArray())
        {
            var state = new TrackerState();
            foreach (var setting in vector.GetProperty("settings").EnumerateObject())
                state.Settings[setting.Name] = setting.Value.GetString()!;
            foreach (var item in vector.GetProperty("items").EnumerateObject())
                state.Items[item.Name] = item.Value.GetInt32();

            var checkedFlags = vector.GetProperty("checked").GetString()!;
            for (var i = 0; i < ids.Count; i++)
            {
                if (checkedFlags[i] == '1')
                    state.CheckedLocations.Add(ids[i]);
            }

            var logic = new LogicEvaluator(Data, state);
            var expected = vector.GetProperty("expected").GetString()!;
            for (var i = 0; i < ids.Count; i++)
            {
                var status = logic.GetStatus(locations[ids[i]]);
                Assert.True(expected[i] == Letter(status), $"{ids[i]} in state {compared / ids.Count}: sheet says {expected[i]}, app says {status}");
                seen.Add(status);
                compared++;
            }
        }

        Assert.Equal(200 * 338, compared);
        Assert.Equal(4, seen.Count);
    }

    private static char Letter(LocationStatus status) => status switch
    {
        LocationStatus.Excluded => 'X',
        LocationStatus.Checked => 'C',
        LocationStatus.Available => 'A',
        _ => 'L',
    };
}
