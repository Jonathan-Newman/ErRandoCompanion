using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.SpoilerLog;

/// <summary>
/// Where each boss is in a seed with enemy randomization: for each boss check (the boss, by who it is), the boss check
/// whose arena it now stands in. Clearing that arena (its check, ticked by its defeat flag) is defeating that boss.
/// This is spoiler data: it is only used to count a "defeat these bosses" goal, and never shown.
/// </summary>
public static class BossArenas
{
    /// <summary>
    /// Boss entity ids the log uses that are not the boss's defeat flag: second phases (the phase that ends the fight
    /// has the flag's number) and the open-world bosses whose ids differ from their flags.
    /// </summary>
    private static readonly Dictionary<int, int> EntityToFlag = new()
    {
        [11050801] = 11050800,     // Godfrey, First Elden Lord: Hoarah Loux
        [13000801] = 13000800,     // Beast Clergyman: Maliketh
        [14000801] = 14000800,     // Rennala's first phase
        [16000801] = 16000800,     // God-Devouring Serpent: Rykard
        [19000810] = 19000800,     // Radagon: the Elden Beast
        [20010801] = 20010800,     // Promised Consort Radahn's first phase
        [21010801] = 21010800,     // Messmer's first phase
        [1052380800] = 1252380800, // Starscourge Radahn
        [1052520800] = 1252520800, // Fire Giant
        [1052520801] = 1252520800, // Fire Giant's second phase
    };

    /// <summary>Boss check id to the boss check whose arena that boss is in. Bosses not listed stay in their own.</summary>
    public static IReadOnlyDictionary<string, string> Build(GameData data, IEnumerable<(int Arena, int Boss)> placements)
    {
        var byFlag = data.Locations
            .Where(IsBoss)
            .Where(l => l.FlagId != 0)
            .GroupBy(l => l.FlagId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var arenas = new Dictionary<string, string>();
        var exact = new HashSet<string>();
        foreach (var (arena, boss) in placements)
        {
            var arenaCheck = Resolve(arena, byFlag, out _);
            var bossCheck = Resolve(boss, byFlag, out var bossExact);
            if (arenaCheck is null || bossCheck is null)
                continue;
            // A boss's phases can be listed apart; the one carrying its defeat flag is the one that counts.
            if (arenas.ContainsKey(bossCheck) && (exact.Contains(bossCheck) || !bossExact))
                continue;
            arenas[bossCheck] = arenaCheck;
            if (bossExact)
                exact.Add(bossCheck);
        }
        return arenas;
    }

    public static bool IsBoss(Location location) =>
        location.Category is LocationCategory.MajorBoss or LocationCategory.OtherBoss or LocationCategory.ExtraBoss;

    private static string? Resolve(int entity, IReadOnlyDictionary<int, string> byFlag, out bool exact)
    {
        exact = byFlag.TryGetValue(entity, out var check);
        if (exact)
            return check;
        return EntityToFlag.TryGetValue(entity, out var flag) && byFlag.TryGetValue(flag, out check) ? check : null;
    }
}
