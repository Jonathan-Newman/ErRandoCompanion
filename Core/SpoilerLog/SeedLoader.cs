using System.IO;
using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Navigation;

namespace ErRandoCompanion.Core.SpoilerLog;

/// <summary>A spoiler log together with what its randomizer folder says about the seed. Nothing here may be shown to the player.</summary>
public sealed record LoadedSeed(ParsedSpoilerLog Log, SeedFlags Flags, MapLinks Links)
{
    /// <summary>Every pickup in the seed, by the flag it sets (see <see cref="ItemPickups"/>). Spoiler data: shown only once taken.</summary>
    public IReadOnlyDictionary<uint, Pickup> Pickups { get; init; } = new Dictionary<uint, Pickup>();

    /// <summary>Item names by (item lot category, id), for naming what arrives in the inventory.</summary>
    public IReadOnlyDictionary<(int Category, int Id), string> ItemNames { get; init; } = new Dictionary<(int, int), string>();

    /// <summary>Map names by map id ("m60_42_36_00"), for pickups the randomizer has no description for.</summary>
    public IReadOnlyDictionary<string, string> MapNames { get; init; } = new Dictionary<string, string>();

    /// <summary>Which arena each boss is in (see <see cref="BossArenas"/>); set by <see cref="SeedLoader.Load"/>.</summary>
    public IReadOnlyDictionary<string, string> BossArenas { get; init; } = new Dictionary<string, string>();

    /// <summary>Every check the randomizer could fill in this seed, or null when the randomizer folder lacks its slot tables.</summary>
    public AllChecksSeed? All { get; init; }

    /// <summary>Every Site of Grace, named and placed, for "warp to the nearest grace" hints.</summary>
    public IReadOnlyList<Grace> Graces { get; init; } = [];

    /// <summary>Something the player should know about tracking this seed, or null when all is well. Never names an item's place.</summary>
    public string? Warning =>
        Flags.LogMismatches > 0
            ? "This spoiler log does not match the regulation.bin in the randomizer folder, so item tracking may be wrong. "
                + "Load the log from your latest randomizer run."
            : Flags.UntrackableItems.Count > 0
                ? $"These items cannot be tracked automatically, so click them when you find them: {string.Join(", ", Flags.UntrackableItems)}"
                : null;
}

/// <summary>Reads a spoiler log and the randomizer folder it was written to. Shared by the app and the mod.</summary>
public static class SeedLoader
{
    public const string RegulationFile = "regulation.bin";
    public const string OodleFile = "oo2core_6_win64.dll";

    /// <exception cref="IOException">A file is missing or unreadable.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be opened.</exception>
    /// <exception cref="SpoilerLogException">A file is not what it should be.</exception>
    public static LoadedSeed Load(GameData data, string logPath)
    {
        var log = SpoilerLogParser.Parse(File.ReadAllLines(logPath), logPath);
        var directory = log.RandomizerDirectory;
        var regulation = Regulation.Load(Path.Combine(directory, RegulationFile), Path.Combine(directory, OodleFile));
        var goods = SeedFlags.ParseGoodsNames(File.ReadLines(Path.Combine(directory, "diste", "Names", "EquipParamGoods.txt")));
        var links = new MapLinks(regulation.MapLinkRows);
        return new LoadedSeed(log, SeedFlags.Build(data, log, regulation, goods), links)
        {
            Graces = Grace.Build(regulation.GraceRows, Grace.LoadNames(directory), links),
            BossArenas = BossArenas.Build(data, log.BossPlacements),
            Pickups = ItemPickups.Build(regulation, log.RandomizerDirectory),
            MapNames = ItemPickups.LoadMapNames(log.RandomizerDirectory),
            ItemNames = ItemPickups.LoadNames(Path.Combine(log.RandomizerDirectory, "diste", "Names")),
            All = LoadAll(directory, regulation, log.Options),
        };
    }

    /// <summary>The full check list, from the randomizer's diste\Base tables; null when they are missing or unreadable.</summary>
    public static AllChecksSeed? LoadAll(string randomizerDirectory, Regulation regulation, IReadOnlySet<string> options)
    {
        var folder = Path.Combine(randomizerDirectory, "diste", "Base");
        var annotations = Path.Combine(folder, "annotations.txt");
        var slots = Path.Combine(folder, "itemslots.txt");
        if (!File.Exists(annotations) || !File.Exists(slots))
            return null;
        try
        {
            return AllChecksSeed.Build(RandomizerLogic.Load(annotations), SlotCatalog.Load(slots), regulation, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
