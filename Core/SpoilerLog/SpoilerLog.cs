using System.IO;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.SpoilerLog;

public sealed class SpoilerLogException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the tracker takes from a randomizer spoiler log. <see cref="SoldItems"/> and the rest of the
/// log's placements are spoilers, so nothing from them may reach the player.
/// </summary>
public sealed class ParsedSpoilerLog
{
    public required string Seed { get; init; }
    public required string Version { get; init; }
    public required IReadOnlySet<string> Options { get; init; }

    /// <summary>The folder holding the seed's regulation.bin and location tables.</summary>
    public required string RandomizerDirectory { get; init; }

    /// <summary>Every item the log places, by name, and whether a shop sells it. Used to confirm a regulation.bin matches.</summary>
    public required IReadOnlyDictionary<string, bool> SoldItems { get; init; }

    /// <summary>
    /// With enemy randomization: which boss now stands where, as (arena's own boss entity, boss placed there).
    /// Spoiler data: used only to count a "defeat these bosses" goal, never shown. Empty without enemy randomization.
    /// </summary>
    public IReadOnlyList<(int Arena, int Boss)> BossPlacements { get; init; } = [];

    /// <summary>
    /// True when both logs describe the same randomization: the randomizer gives the same items for the same seed,
    /// options and version, which happens when a seed is randomized again. The placements must match too, to be sure.
    /// </summary>
    public bool IsSameRandomization(ParsedSpoilerLog other) =>
        Seed.Length > 0
        && Seed == other.Seed
        && Version == other.Version
        && Options.SetEquals(other.Options)
        && SoldItems.Count == other.SoldItems.Count
        && SoldItems.All(kv => other.SoldItems.TryGetValue(kv.Key, out var sold) && sold == kv.Value);

    /// <summary>Copies the randomizer options that mean the same thing as a tracker setting.</summary>
    public void ApplyOptionsTo(TrackerState state)
    {
        SetBool("dlcEnabled", "dlc");
        SetBool("otherBosses", "raceloc_altboss");
        SetBool("seedsAndTears", "raceloc_health");
        SetBool("merchantShops", "raceloc_shops");
        SetBool("blessings", "raceloc_scadu");
        SetCount("leyndellRunes", "runes_leyndell");
        SetCount("mountaintopsRunes", "runes_rold");

        void SetBool(string setting, string option) =>
            state.Settings[setting] = Options.Contains(option) ? "true" : "false";

        // These appear as "name:value" only when changed from the default, so absence changes nothing.
        void SetCount(string setting, string option)
        {
            var token = Options.FirstOrDefault(o => o.StartsWith(option + ":", StringComparison.Ordinal));
            if (token is not null)
                state.Settings[setting] = token[(option.Length + 1)..];
        }
    }
}

public static class SpoilerLogParser
{
    private const string SpoilersStart = "-- Spoilers:";
    private const string SpoilersEnd = "-- End of item spoilers";
    private const string OptionsPrefix = "Options and seed: ";
    private const string VersionPrefix = "Version: ";
    private const string WritingPrefix = "Writing to ";
    private const string SoldPrefix = "Sold by";

    public static ParsedSpoilerLog Parse(IReadOnlyList<string> lines, string logPath)
    {
        var start = IndexOf(lines, SpoilersStart);
        var end = IndexOf(lines, SpoilersEnd);
        if (start < 0 || end < start)
            throw new SpoilerLogException("This does not look like an item randomizer spoiler log: it has no item spoilers section.");

        var optionsLine = lines.FirstOrDefault(l => l.StartsWith(OptionsPrefix, StringComparison.Ordinal))
            ?? throw new SpoilerLogException("This spoiler log has no options line.");
        var options = optionsLine[OptionsPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var sold = new Dictionary<string, bool>();
        for (var i = start + 1; i < end; i++)
        {
            // Format: "Item [Nx] in Region[ - Area]: Description. [Near ...] Replaces Original [Nx]."
            var line = lines[i];
            var inIndex = line.IndexOf(" in ", StringComparison.Ordinal);
            var colon = inIndex < 0 ? -1 : line.IndexOf(": ", inIndex, StringComparison.Ordinal);
            if (colon < 0)
                continue;

            var name = StripCount(line[..inIndex]);
            var isSold = string.CompareOrdinal(line, colon + 2, SoldPrefix, 0, SoldPrefix.Length) == 0;
            sold[name] = sold.GetValueOrDefault(name) || isSold;
        }

        var written = lines.LastOrDefault(l => l.StartsWith(WritingPrefix, StringComparison.Ordinal));
        var bosses = ReadBossPlacements(lines);
        return new ParsedSpoilerLog
        {
            Seed = options.FirstOrDefault(t => t.StartsWith("seed:", StringComparison.Ordinal))?["seed:".Length..] ?? "",
            Version = lines.FirstOrDefault(l => l.StartsWith(VersionPrefix, StringComparison.Ordinal))?[VersionPrefix.Length..].Trim() ?? "",
            Options = options.ToHashSet(),

            // Logs normally live in <randomizer>/spoiler_logs, in case the log does not say where it wrote.
            RandomizerDirectory = written is not null
                ? written[WritingPrefix.Length..].Trim()
                : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(logPath))) ?? "",
            SoldItems = sold,
            BossPlacements = bosses,
        };
    }

    /// <summary>
    /// Lines of the "Boss placements" and "Miniboss placements" sections:
    /// "Replacing Margit (#10000850) in Stormveil Castle: Rennala, Queen of the Full Moon (#14000800) from ...".
    /// </summary>
    private static List<(int Arena, int Boss)> ReadBossPlacements(IReadOnlyList<string> lines)
    {
        var placements = new List<(int, int)>();
        var inSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("-- ", StringComparison.Ordinal))
            {
                inSection = line.StartsWith("-- Boss placements", StringComparison.Ordinal)
                    || line.StartsWith("-- Miniboss placements", StringComparison.Ordinal);
                continue;
            }
            if (!inSection || !line.StartsWith("Replacing ", StringComparison.Ordinal))
                continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0 || EntityIn(line[..colon]) is not { } arena || EntityIn(line[colon..]) is not { } boss)
                continue;
            placements.Add((arena, boss));
        }
        return placements;
    }

    /// <summary>The first "(#12345)" in the text.</summary>
    private static int? EntityIn(string text)
    {
        var start = text.IndexOf("(#", StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(')', start);
        return end > start && int.TryParse(text.AsSpan(start + 2, end - start - 2), out var id) ? id : null;
    }

    /// <summary>Reads a log a run is linked to, or returns null when it has been moved, deleted or is unreadable.</summary>
    public static ParsedSpoilerLog? TryParseFile(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? Parse(File.ReadAllLines(path), path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SpoilerLogException)
        {
            return null;
        }
    }

    /// <summary>"Smithing Stone [1] 3x" becomes "Smithing Stone [1]".</summary>
    internal static string StripCount(string item)
    {
        var split = item.LastIndexOf(' ');
        return split > 0 && item.EndsWith('x') && int.TryParse(item[(split + 1)..^1], out _) ? item[..split] : item;
    }

    private static int IndexOf(IReadOnlyList<string> lines, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
